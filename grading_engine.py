"""
grading_engine.py

Background batch engine used by the Streamlit app. Reuses main.py's core
design (one shared AsyncClient bridge, one event loop, concurrency-capped
workers, per-row timeout + one retry) but two things are different:

  1. Runs on its own background thread so the Streamlit UI thread is never
     blocked -- the thread owns its own asyncio event loop, same fix for the
     Windows WinError 10038 socket crash that main.py's docstring describes,
     just applied to a thread instead of the whole process.
  2. Uses `agent.send(prompt, SendOptions(on_delta=..., on_step=...))` instead
     of the one-shot `AsyncAgent.prompt()` helper, so intermediate tool-call
     and reasoning events stream out live instead of only the final answer.

Everything crosses back to the UI through a plain `queue.Queue` of small
dict events. Nothing here calls Streamlit or touches `st.session_state` --
Streamlit's own docs are explicit that background threads must not do
either; a fragment on the main thread drains this queue instead.
"""

from __future__ import annotations

import asyncio
import threading
import time
from dataclasses import dataclass, field
from queue import Queue
from typing import Any

from cursor_sdk import (
    AgentOptions,
    AsyncAgent,
    AsyncClient,
    LocalAgentOptions,
    SendOptions,
)

from prompts import build_prompt, parse_grade

MODEL_ID = "composer-2.5"
PER_ROW_TIMEOUT_SECONDS = 180
MAX_RETRIES = 1
DELTA_THROTTLE_SECONDS = 0.3  # min gap between UI events for the same streaming text
DELTA_FLUSH_CHARS = 400        # ...unless this much text has piled up, then flush anyway


@dataclass
class RowTask:
    row_index: int          # 1-based openpyxl row number
    company: str
    website: str
    fields: dict[str, str]  # {column label: value} for whatever the user picked to feed the AI


@dataclass
class EngineConfig:
    workers: int = 6
    who_we_are: str = ""
    grade_rubric: str = ""
    api_key: str | None = None
    cwd: str = "."


class GradingEngine:
    """Runs one batch of RowTasks on a background thread, streaming events
    onto `self.events` for the UI to poll."""

    def __init__(self, tasks: list[RowTask], examples: list[dict], config: EngineConfig):
        self.tasks = tasks
        self.examples = examples
        self.config = config
        self.events: Queue[dict[str, Any]] = Queue()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._delta_buffers: dict[tuple[int, str], str] = {}
        self._last_emit: dict[tuple[int, str], float] = {}

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run_thread, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        """Cooperative stop: rows not yet started (or not yet past the slot
        wait) are skipped; rows already mid-attempt are left to finish --
        same "whatever completed stays saved" spirit as main.py's
        KeyboardInterrupt handling."""
        self._stop.set()

    def is_alive(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    # -- background thread -------------------------------------------------

    def _run_thread(self) -> None:
        try:
            asyncio.run(self._async_main())
        except Exception as exc:
            self.events.put({"type": "engine_error", "error": f"{type(exc).__name__}: {exc}"})
        finally:
            self.events.put({"type": "batch_finished"})

    async def _async_main(self) -> None:
        client = await AsyncClient.launch_bridge(
            workspace=self.config.cwd,
            local=LocalAgentOptions(cwd=self.config.cwd),
            allow_api_key_env_fallback=True,
        )
        slot_pool: asyncio.Queue[int] = asyncio.Queue()
        for slot in range(self.config.workers):
            slot_pool.put_nowait(slot)

        try:
            await asyncio.gather(*(self._run_one(task, client, slot_pool) for task in self.tasks))
        finally:
            await client.aclose()

    async def _run_one(self, task: RowTask, client: AsyncClient, slot_pool: "asyncio.Queue[int]") -> None:
        if self._stop.is_set():
            self._emit_skipped(task)
            return

        slot = await slot_pool.get()
        try:
            if self._stop.is_set():
                self._emit_skipped(task)
                return

            self.events.put({
                "type": "row_started", "slot": slot, "row_index": task.row_index,
                "company": task.company, "website": task.website,
            })

            prompt = build_prompt(
                task.website, task.fields, self.config.who_we_are,
                self.config.grade_rubric, self.examples,
            )

            grade, reason, status = "UNABLE", "unknown error", "error"
            for attempt in range(MAX_RETRIES + 1):
                if self._stop.is_set():
                    reason = "Stopped by user before completion"
                    break
                try:
                    grade, reason = await asyncio.wait_for(
                        self._one_attempt(prompt, task, slot, client),
                        timeout=PER_ROW_TIMEOUT_SECONDS,
                    )
                    status = "done"
                    break
                except asyncio.TimeoutError:
                    reason = f"timed out after {PER_ROW_TIMEOUT_SECONDS}s"
                except Exception as exc:
                    reason = f"{type(exc).__name__}: {exc}"
                    await asyncio.sleep(2)

            self.events.put({
                "type": "row_finished", "slot": slot, "row_index": task.row_index,
                "company": task.company, "website": task.website,
                "grade": grade, "reason": reason, "status": status,
            })
        finally:
            await slot_pool.put(slot)

    async def _one_attempt(self, prompt: str, task: RowTask, slot: int, client: AsyncClient) -> tuple[str, str]:
        def on_delta(update: Any) -> None:
            self._emit_delta(slot, task, update)

        def on_step(step: Any) -> None:
            pass  # step boundaries aren't surfaced in the UI today; hook kept for later use

        agent = await AsyncAgent.create(
            AgentOptions(
                model=MODEL_ID,
                api_key=self.config.api_key,
                local=LocalAgentOptions(cwd=self.config.cwd),
            ),
            client=client,
        )
        try:
            run = await agent.send(prompt, SendOptions(on_delta=on_delta, on_step=on_step))
            result = await run.wait()
            if result.status != "finished":
                raise RuntimeError(f"run status={result.status}")
            return parse_grade(result.result or "")
        finally:
            await agent.close()

    def _emit_skipped(self, task: RowTask) -> None:
        self.events.put({
            "type": "row_skipped", "row_index": task.row_index,
            "company": task.company, "website": task.website,
        })

    def _emit_delta(self, slot: int, task: RowTask, update: Any) -> None:
        kind = getattr(update, "type", "")

        if kind == "tool-call-started":
            detail = _describe_tool_call(getattr(update, "tool_call", {}) or {})
        elif kind in ("thinking-delta", "text-delta"):
            text = getattr(update, "text", "")
            if not text:
                return
            buf_key = (slot, kind)
            buf = self._delta_buffers.get(buf_key, "") + text
            now = time.monotonic()
            last = self._last_emit.get(buf_key, 0.0)
            if now - last < DELTA_THROTTLE_SECONDS and len(buf) < DELTA_FLUSH_CHARS:
                self._delta_buffers[buf_key] = buf
                return
            self._delta_buffers[buf_key] = ""
            self._last_emit[buf_key] = now
            detail = buf.strip()
            if not detail:
                return
        else:
            return  # tool-call-completed, token-delta, step-*, turn-ended, etc: not shown live

        self.events.put({
            "type": "delta", "slot": slot, "row_index": task.row_index,
            "company": task.company, "website": task.website,
            "kind": kind, "detail": detail,
        })


def _describe_tool_call(tool_call: dict[str, Any]) -> str:
    """Best-effort human-readable label for a tool call.

    Confirmed by live testing: this local agent's only real fetch mechanism is
    its `shell` tool (running curl/python, not a dedicated browser tool), wire
    shape `{"type": "shell", "args": {"command": "..."}}`. Still falls back
    gracefully for any other tool type instead of raising.
    """
    kind = tool_call.get("type") or tool_call.get("name") or tool_call.get("toolName") or "tool"
    args = tool_call.get("args") or tool_call.get("input") or tool_call.get("arguments") or {}
    if isinstance(args, dict):
        command = args.get("command")
        if command:
            return str(command).strip().splitlines()[0][:90]
        target = args.get("url") or args.get("uri") or args.get("query") or args.get("path")
        if target:
            return f"{kind}: {target}"
    return str(kind)
