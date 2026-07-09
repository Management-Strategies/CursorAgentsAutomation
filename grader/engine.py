from __future__ import annotations

import asyncio
import os
from collections.abc import Callable
from pathlib import Path
from queue import Queue
from typing import Any

from cursor_sdk import AgentOptions, AsyncAgent, AsyncClient, LocalAgentOptions
from openpyxl import Workbook
from openpyxl.worksheet.worksheet import Worksheet

from grader.config import GradingConfig
from grader.models import AgentActivity, AgentStatus, BatchState, ExampleRow, RowTask
from grader.parser import parse_grade
from grader.prompt import build_prompt


ActivityCallback = Callable[[AgentActivity], None]


class GradingCancelled(Exception):
    """Raised when the user stops a run mid-stream."""


def _format_tool_args(name: str, args: Any) -> str:
    if not args:
        return ""

    if isinstance(args, str):
        detail = args.strip()
        return f": {detail[:120]}" if detail else ""

    if isinstance(args, dict):
        for key in ("url", "URL", "uri", "href", "path", "query", "search_term"):
            value = args.get(key)
            if value:
                return f": {str(value)[:120]}"
        if len(args) == 1:
            return f": {str(next(iter(args.values())))[:120]}"
        compact = ", ".join(f"{k}={v}" for k, v in list(args.items())[:3])
        return f": {compact[:120]}"

    return f": {str(args)[:120]}"


def _truncate(text: str, limit: int = 220) -> str:
    text = text.strip()
    if len(text) <= limit:
        return text
    return "..." + text[-(limit - 3) :]


_NOISY_STATUSES = frozenset({"RUNNING", "PENDING", "IN_PROGRESS", "STARTED", "QUEUED"})


def _finalize_assistant_line(activity: AgentActivity) -> None:
    text = activity.assistant_buffer.strip()
    if not text:
        activity.remove_prefixed_lines("💬 Responding: ")
        activity.assistant_buffer = ""
        return
    activity.replace_prefixed_line("💬 Responding: ", f"💬 {_truncate(text, 260)}")
    activity.assistant_buffer = ""


def apply_sdk_message_to_activity(activity: AgentActivity, message: Any) -> bool:
    """Update activity log from an SDK stream message. Returns True if the UI should refresh."""
    msg_type = getattr(message, "type", None)

    if msg_type == "thinking":
        _finalize_assistant_line(activity)
        text = getattr(message, "text", "") or ""
        duration = getattr(message, "thinking_duration_ms", None)
        if text:
            activity.thinking_buffer += text
            activity.upsert_prefixed_line("💭 Thinking: ", _truncate(activity.thinking_buffer, 220))
            return True
        if duration is not None:
            thought = activity.thinking_buffer.strip()
            if thought:
                secs = duration / 1000
                if secs >= 0.1:
                    final = f"💭 Thought ({secs:.1f}s): {_truncate(thought, 200)}"
                else:
                    final = f"💭 Thought: {_truncate(thought, 200)}"
                activity.replace_prefixed_line("💭 Thinking: ", final)
            else:
                activity.remove_prefixed_lines("💭 Thinking: ")
            activity.thinking_buffer = ""
            return True
        return False

    if msg_type == "tool_call":
        _finalize_assistant_line(activity)
        name = getattr(message, "name", "tool") or "tool"
        status = getattr(message, "status", "") or ""
        args = getattr(message, "args", None)
        if status == "running":
            activity.append_line(f"🔧 {name}{_format_tool_args(name, args)}")
            return True
        if status == "completed":
            activity.append_line(f"✓ {name} completed")
            return True
        if status == "error":
            activity.append_line(f"✗ {name} failed")
            return True
        activity.append_line(f"🔧 {name} ({status})")
        return True

    if msg_type == "assistant":
        content = getattr(getattr(message, "message", None), "content", None) or []
        changed = False
        for block in content:
            block_type = getattr(block, "type", None)
            if block_type == "text":
                text = getattr(block, "text", "")
                if text:
                    activity.assistant_buffer += text
                    changed = True
            elif block_type in {"tool_use", "tool_call"}:
                _finalize_assistant_line(activity)
                tool_name = getattr(block, "name", "tool")
                activity.append_line(f"🔧 {tool_name}")
                changed = True
        if changed and activity.assistant_buffer.strip():
            activity.upsert_prefixed_line(
                "💬 Responding: ",
                _truncate(activity.assistant_buffer.strip(), 260),
            )
            return True
        return changed

    if msg_type == "status":
        status = (getattr(message, "status", "") or "").upper()
        detail = (getattr(message, "message", "") or "").strip()
        if detail:
            activity.append_line(f"ℹ {detail[:200]}")
            return True
        if status and status not in _NOISY_STATUSES:
            activity.append_line(f"ℹ Status: {status}")
            return True
        return False

    if msg_type == "task":
        text = getattr(message, "text", "")
        status = getattr(message, "status", "")
        if text:
            activity.append_line(f"📋 {text[:200]}")
            return True
        if status:
            activity.append_line(f"📋 Task: {status}")
            return True
        return False

    if msg_type in {"user", "system", "usage", "request"}:
        return False

    return False


def format_activity_message(message: Any) -> str | None:
    """Legacy helper — prefer apply_sdk_message_to_activity."""
    msg_type = getattr(message, "type", None)
    if msg_type == "thinking":
        text = getattr(message, "text", "") or ""
        return f"💭 {text}" if text else None
    if msg_type == "tool_call":
        name = getattr(message, "name", "tool")
        return f"🔧 {name}"
    return None


async def grade_one_company(
    task: RowTask,
    config: GradingConfig,
    examples: list[ExampleRow],
    client: AsyncClient,
    semaphore: asyncio.Semaphore,
    activity: AgentActivity,
    on_activity: ActivityCallback | None,
    cancel_check: Callable[[], bool],
    *,
    stream: bool = True,
) -> tuple[int, str, str]:
    prompt = build_prompt(config, task.website, task.context, examples)

    def emit(update: AgentActivity) -> None:
        if on_activity:
            on_activity(update)

    async with semaphore:
        if cancel_check():
            activity.status = AgentStatus.STOPPED
            emit(activity)
            return task.row_index, "UNABLE", "Stopped by user"

        activity.status = AgentStatus.RUNNING
        activity.append_line("Starting agent...")
        emit(activity)

        last_error = "unknown error"
        api_key = os.environ.get("CURSOR_API_KEY")
        options = AgentOptions(
            model=config.model,
            api_key=api_key,
            local=LocalAgentOptions(cwd=os.getcwd()),
        )

        for attempt in range(config.max_retries + 1):
            if cancel_check():
                activity.status = AgentStatus.STOPPED
                emit(activity)
                return task.row_index, "UNABLE", "Stopped by user"

            try:
                if stream:
                    grade, reason = await _grade_streaming(
                        prompt, options, client, config, activity, emit, cancel_check
                    )
                else:
                    grade, reason = await _grade_oneshot(
                        prompt, options, client, config
                    )

                activity.status = AgentStatus.DONE
                activity.grade = grade
                activity.reason = reason
                activity.append_line(f"Decision: {grade} — {reason}")
                emit(activity)
                return task.row_index, grade, reason
            except GradingCancelled:
                activity.status = AgentStatus.STOPPED
                emit(activity)
                return task.row_index, "UNABLE", "Stopped by user"
            except asyncio.TimeoutError:
                last_error = f"timed out after {config.per_row_timeout_seconds}s"
                activity.append_line(last_error)
                emit(activity)
            except Exception as exc:
                last_error = f"{type(exc).__name__}: {exc}"
                activity.append_line(last_error)
                emit(activity)
                await asyncio.sleep(2)

        activity.status = AgentStatus.ERROR
        reason = f"Failed after {config.max_retries + 1} attempt(s): {last_error}"
        activity.reason = reason
        activity.grade = "UNABLE"
        emit(activity)
        return task.row_index, "UNABLE", reason


async def _grade_oneshot(
    prompt: str,
    options: AgentOptions,
    client: AsyncClient,
    config: GradingConfig,
) -> tuple[str, str]:
    result = await asyncio.wait_for(
        AsyncAgent.prompt(prompt, options, client=client),
        timeout=config.per_row_timeout_seconds,
    )
    if result.status != "finished":
        raise RuntimeError(f"run status={result.status}")
    return parse_grade(result.result or "")


async def _grade_streaming(
    prompt: str,
    options: AgentOptions,
    client: AsyncClient,
    config: GradingConfig,
    activity: AgentActivity,
    emit: Callable[[AgentActivity], None],
    cancel_check: Callable[[], bool],
) -> tuple[str, str]:
    async with await client.agents.create(options) as agent:
        run = await agent.send(prompt)

        async for message in run.messages():
            if cancel_check():
                if run.supports("cancel"):
                    await run.cancel()
                raise GradingCancelled()
            if apply_sdk_message_to_activity(activity, message):
                emit(activity)

        if cancel_check():
            raise GradingCancelled()

        _finalize_assistant_line(activity)
        emit(activity)

        result = await asyncio.wait_for(run.wait(), timeout=config.per_row_timeout_seconds)

        if result.status == "cancelled":
            raise GradingCancelled()
        if result.status != "finished":
            raise RuntimeError(f"run status={result.status}")

        return parse_grade(result.result or "")


async def run_batch(
    tasks: list[RowTask],
    examples: list[ExampleRow],
    config: GradingConfig,
    workers: int,
    ws: Worksheet,
    mapping_grade_fn: Callable[[int, str, str], None],
    *,
    on_activity: ActivityCallback | None = None,
    on_progress: Callable[[BatchState], None] | None = None,
    cancel_check: Callable[[], bool] | None = None,
    checkpoint_save: Callable[[], None] | None = None,
    stream: bool = True,
) -> BatchState:
    state = BatchState(total=len(tasks), completed=0, running=True, slots=[None] * workers)
    if on_progress:
        on_progress(state)

    is_cancelled = cancel_check or (lambda: False)

    client = await AsyncClient.launch_bridge(
        workspace=os.getcwd(),
        local=LocalAgentOptions(cwd=os.getcwd()),
        allow_api_key_env_fallback=True,
    )

    semaphore = asyncio.Semaphore(workers)
    pending = list(tasks)
    in_flight: dict[asyncio.Task, tuple[RowTask, AgentActivity]] = {}

    def assign_slot(activity: AgentActivity) -> int:
        for i, slot in enumerate(state.slots):
            if slot is None or slot.status in {AgentStatus.DONE, AgentStatus.ERROR, AgentStatus.STOPPED}:
                activity.slot = i
                state.slots[i] = activity
                return i
        activity.slot = 0
        state.slots[0] = activity
        return 0

    async def start_next() -> None:
        while pending and len(in_flight) < workers and not is_cancelled():
            task = pending.pop(0)
            activity = AgentActivity(
                task_id=task.task_id,
                row_index=task.row_index,
                label=task.label,
                website=task.website,
                status=AgentStatus.QUEUED,
            )
            activity.append_line("Queued...")
            assign_slot(activity)
            if on_activity:
                on_activity(activity)
            if on_progress:
                on_progress(state)

            coro = grade_one_company(
                task,
                config,
                examples,
                client,
                semaphore,
                activity,
                on_activity,
                is_cancelled,
                stream=stream,
            )
            in_flight[asyncio.ensure_future(coro)] = (task, activity)

    try:
        await start_next()

        while in_flight:
            done, _ = await asyncio.wait(in_flight.keys(), return_when=asyncio.FIRST_COMPLETED)
            for fut in done:
                task, activity = in_flight.pop(fut)
                try:
                    row_index, grade, reason = await fut
                except Exception as exc:
                    row_index, grade, reason = task.row_index, "UNABLE", f"Unexpected failure: {exc}"
                    activity.status = AgentStatus.ERROR
                    activity.grade = grade
                    activity.reason = reason

                if row_index != -1:
                    mapping_grade_fn(row_index, grade, reason)
                    state.results[row_index] = (grade, reason)

                state.completed += 1
                if on_progress:
                    on_progress(state)

                if checkpoint_save and state.completed % config.save_every == 0:
                    checkpoint_save()

            if is_cancelled():
                state.stopped = True
                break

            await start_next()

        # Drain remaining in-flight after cancel
        if in_flight:
            remaining = list(in_flight.items())
            for fut, (task, activity) in remaining:
                try:
                    row_index, grade, reason = await fut
                except Exception as exc:
                    row_index, grade, reason = task.row_index, "UNABLE", str(exc)
                if row_index != -1 and row_index not in state.results:
                    mapping_grade_fn(row_index, grade, reason)
                    state.results[row_index] = (grade, reason)
                    state.completed += 1
    finally:
        state.running = False
        if on_progress:
            on_progress(state)
        await client.aclose()

    return state


def run_batch_in_thread(
    tasks: list[RowTask],
    examples: list[ExampleRow],
    config: GradingConfig,
    workers: int,
    ws: Worksheet,
    mapping_grade_fn: Callable[[int, str, str], None],
    event_queue: Queue,
    cancel_flag: list[bool],
    stream: bool = True,
) -> None:
    """Run batch in a background thread, pushing AgentActivity snapshots to a queue."""

    def on_activity(activity: AgentActivity) -> None:
        event_queue.put(("activity", _copy_activity(activity)))

    def on_progress(state: BatchState) -> None:
        event_queue.put(("progress", _copy_state(state)))

    def cancel_check() -> bool:
        return cancel_flag[0]

    async def _run() -> None:
        try:
            await run_batch(
                tasks,
                examples,
                config,
                workers,
                ws,
                mapping_grade_fn,
                on_activity=on_activity,
                on_progress=on_progress,
                cancel_check=cancel_check,
                stream=stream,
            )
            event_queue.put(("done", None))
        except Exception as exc:
            event_queue.put(("error", str(exc)))

    asyncio.run(_run())


def _copy_activity(activity: AgentActivity) -> AgentActivity:
    return AgentActivity(
        task_id=activity.task_id,
        row_index=activity.row_index,
        label=activity.label,
        website=activity.website,
        status=activity.status,
        lines=list(activity.lines),
        grade=activity.grade,
        reason=activity.reason,
        slot=activity.slot,
        thinking_buffer=activity.thinking_buffer,
        assistant_buffer=activity.assistant_buffer,
    )


def _copy_state(state: BatchState) -> BatchState:
    return BatchState(
        total=state.total,
        completed=state.completed,
        running=state.running,
        stopped=state.stopped,
        error=state.error,
        slots=[_copy_activity(s) if s else None for s in state.slots],
        results=dict(state.results),
    )


async def run_batch_cli(
    tasks: list[RowTask],
    examples: list[ExampleRow],
    config: GradingConfig,
    workers: int,
    ws: Worksheet,
    mapping_grade_fn: Callable[[int, str, str], None],
    checkpoint_save: Callable[[], None] | None = None,
) -> int:
    state = await run_batch(
        tasks,
        examples,
        config,
        workers,
        ws,
        mapping_grade_fn,
        checkpoint_save=checkpoint_save,
        stream=False,
    )
    return state.completed
