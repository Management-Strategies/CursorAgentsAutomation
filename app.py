# Lead Website Grader — Streamlit web app
#
# Setup:
#   uv sync
#   copy .streamlit/secrets.toml.example to .streamlit/secrets.toml and set CURSOR_API_KEY
#   uv run streamlit run app.py

from __future__ import annotations

import os
import threading
from datetime import timedelta
from queue import Empty, Queue

import streamlit as st

from grader.config import GradingConfig, VALID_GRADES
from grader.engine import run_batch_in_thread
from grader.models import AgentActivity, AgentStatus, BatchState, ColumnMapping
from grader.spreadsheet import (
    apply_dataframe_edits,
    apply_result,
    build_pending_tasks,
    collect_examples,
    get_headers,
    guess_context_columns,
    guess_website_column,
    load_workbook_from_bytes,
    workbook_to_bytes,
    worksheet_to_dataframe,
)

STEP_LABELS = ["Upload", "Configure", "Run", "Review"]


def _init_session_state() -> None:
    defaults = {
        "step": 0,
        "wb": None,
        "ws": None,
        "sheet_name": None,
        "upload_name": None,
        "preview_df": None,
        "headers": [],
        "mapping": None,
        "grading_config": GradingConfig.alliance_defaults(),
        "workers": 6,
        "use_examples": True,
        "grade_limit": 5,
        "tasks": [],
        "batch_state": None,
        "event_queue": None,
        "cancel_flag": None,
        "batch_thread": None,
        "batch_error": None,
        "batch_done": False,
        "results_df": None,
        "agent_focus_slot": 0,
        "agent_focus_manual": False,
    }
    for key, value in defaults.items():
        if key not in st.session_state:
            st.session_state[key] = value


def _api_key_configured() -> bool:
    key = os.environ.get("CURSOR_API_KEY")
    if key:
        return True
    try:
        return bool(st.secrets.get("CURSOR_API_KEY"))
    except Exception:
        return False


def _require_api_key() -> bool:
    if not _api_key_configured():
        st.error(
            "CURSOR_API_KEY is not configured. Set it in the environment or in "
            "`.streamlit/secrets.toml` (see `.streamlit/secrets.toml.example`)."
        )
        return False

    if not os.environ.get("CURSOR_API_KEY"):
        try:
            key = st.secrets.get("CURSOR_API_KEY")
            if key:
                os.environ["CURSOR_API_KEY"] = str(key)
        except Exception:
            pass
    return True


def _sidebar_progress() -> None:
    st.sidebar.title("Lead Website Grader")
    st.sidebar.markdown("**Progress**")
    for i, label in enumerate(STEP_LABELS):
        marker = "→" if i == st.session_state.step else " "
        done = "✓" if i < st.session_state.step else " "
        st.sidebar.markdown(f"{done} {marker} **{i + 1}. {label}**")

    if _api_key_configured():
        st.sidebar.success("API key configured")
    else:
        st.sidebar.warning("API key missing")


def _reset_batch() -> None:
    for key in [
        "batch_state",
        "event_queue",
        "cancel_flag",
        "batch_thread",
        "batch_error",
        "batch_done",
        "tasks",
    ]:
        st.session_state[key] = None if key != "batch_done" else False
    st.session_state.batch_done = False
    st.session_state.agent_focus_slot = 0
    st.session_state.agent_focus_manual = False


def _agent_status_icon(status: AgentStatus) -> str:
    return {
        AgentStatus.RUNNING: "🟢",
        AgentStatus.QUEUED: "🟡",
        AgentStatus.DONE: "✅",
        AgentStatus.ERROR: "❌",
        AgentStatus.STOPPED: "⏹",
        AgentStatus.IDLE: "⚪",
    }.get(status, "⚪")


def _agent_summary(slot: AgentActivity | None, slot_index: int) -> str:
    if slot is None:
        return f"Agent {slot_index + 1} · Waiting"
    icon = _agent_status_icon(slot.status)
    grade = f" · {slot.grade}" if slot.grade else ""
    return f"{icon} Agent {slot_index + 1} · {slot.label}{grade}"


def _format_activity_log(lines: list[str]) -> str:
    if not lines:
        return "_No activity yet._"
    parts: list[str] = []
    for line in lines:
        if line.startswith("💭 Thinking:"):
            parts.append(f"> **Thinking…** {line.removeprefix('💭 Thinking:').strip()}")
        elif line.startswith("💭 Thought"):
            parts.append(f"> **Thought** {line.removeprefix('💭 Thought').strip()}")
        elif line.startswith("🔧"):
            parts.append(f"`{line}`")
        elif line.startswith("✓") or line.startswith("✗"):
            parts.append(f"**{line}**")
        elif line.startswith("💬"):
            parts.append(line)
        else:
            parts.append(line)
    return "\n\n".join(parts)


def _pick_auto_focus_slot(state: BatchState, workers: int) -> int:
    for i in range(workers):
        slot = state.slots[i] if i < len(state.slots) else None
        if slot and slot.status == AgentStatus.RUNNING:
            return i
    for i in range(workers):
        slot = state.slots[i] if i < len(state.slots) else None
        if slot and slot.status not in {AgentStatus.IDLE, AgentStatus.QUEUED}:
            return i
    return 0


def _render_agent_detail(slot: AgentActivity | None, slot_index: int, *, height: int = 420) -> None:
    if slot is None:
        st.caption("Waiting for work…")
        return

    header_cols = st.columns([3, 1])
    with header_cols[0]:
        st.markdown(f"**{slot.label}**")
        st.caption(slot.website)
    with header_cols[1]:
        st.markdown(f"**{_status_badge(slot.status)}**")

    if slot.grade:
        st.markdown(f":{_grade_color(slot.grade)}[**{slot.grade}**] {slot.reason or ''}")

    with st.container(height=height, border=True):
        st.markdown(_format_activity_log(slot.lines))


def _step_upload() -> None:
    st.header("1. Upload your leads spreadsheet")
    st.markdown("Upload an Excel file (`.xlsx`) with your company leads. You'll preview the full sheet before grading.")

    uploaded = st.file_uploader("Choose a spreadsheet", type=["xlsx"])
    if uploaded is not None:
        data = uploaded.getvalue()
        wb, ws, sheet_name = load_workbook_from_bytes(data)
        headers = get_headers(ws)
        preview_df = worksheet_to_dataframe(ws)

        if preview_df.empty:
            st.warning("The spreadsheet has no data rows.")
            return

        st.session_state.wb = wb
        st.session_state.ws = ws
        st.session_state.sheet_name = sheet_name
        st.session_state.upload_name = uploaded.name
        st.session_state.preview_df = preview_df
        st.session_state.headers = headers

        website_guess = guess_website_column(headers)
        context_guess = guess_context_columns(headers)
        label_guess = "Company Name" if "Company Name" in headers else None

        st.session_state.mapping = ColumnMapping(
            website=website_guess or headers[0],
            context_columns=context_guess,
            grade_out="WEBSITE_GRADE" if "WEBSITE_GRADE" in headers else "WEBSITE_GRADE",
            comment_out="Comment" if "Comment" in headers else "Comment",
            label=label_guess,
        )

    if st.session_state.preview_df is not None:
        st.success(f"Loaded **{st.session_state.upload_name}** ({len(st.session_state.preview_df)} rows)")
        st.dataframe(st.session_state.preview_df, width="stretch", height=400)

        if st.button("Continue to configuration", type="primary"):
            st.session_state.step = 1
            st.rerun()


def _step_configure() -> None:
    st.header("2. Configure columns and grading criteria")

    headers = st.session_state.headers
    mapping: ColumnMapping = st.session_state.mapping

    col_left, col_right = st.columns(2)

    with col_left:
        st.subheader("Column mapping")
        website = st.selectbox(
            "Website column (required)",
            headers,
            index=headers.index(mapping.website) if mapping.website in headers else 0,
        )
        context_cols = st.multiselect(
            "Context columns sent to the AI",
            headers,
            default=[c for c in mapping.context_columns if c in headers],
        )
        grade_out = st.text_input("Grade output column", value=mapping.grade_out)
        comment_out = st.text_input("Comment output column", value=mapping.comment_out or "")
        label_options = ["(none)"] + headers
        label_default = mapping.label if mapping.label in headers else "(none)"
        label = st.selectbox("Display label column", label_options, index=label_options.index(label_default))

        st.subheader("Run settings")
        workers = st.slider("Concurrent agents", min_value=1, max_value=8, value=st.session_state.workers)
        use_examples = st.checkbox("Use already-graded rows as examples", value=st.session_state.use_examples)
        grade_limit = st.number_input(
            "Companies to grade (test run)",
            min_value=0,
            value=st.session_state.grade_limit,
            help="How many pending companies to grade this run. Default is 5 for a quick test. Set to 0 to grade all pending rows.",
        )

    with col_right:
        st.subheader("Grading criteria")
        cfg = st.session_state.grading_config
        business = st.text_area("Business description", value=cfg.business_description, height=160)
        grade_good = st.text_area("GOOD means", value=cfg.grade_good, height=80)
        grade_maybe = st.text_area("MAYBE means", value=cfg.grade_maybe, height=80)
        grade_unable = st.text_area("UNABLE means", value=cfg.grade_unable, height=80)
        with st.expander("Advanced instructions"):
            extra = st.text_area("Additional instructions", value=cfg.extra_instructions, height=200)

    if not context_cols:
        st.warning("Select at least one context column for the AI.")

    nav1, nav2 = st.columns(2)
    with nav1:
        if st.button("← Back"):
            st.session_state.step = 0
            st.rerun()
    with nav2:
        if st.button("Continue to grading", type="primary", disabled=not context_cols):
            st.session_state.mapping = ColumnMapping(
                website=website,
                context_columns=context_cols,
                grade_out=grade_out.strip() or "WEBSITE_GRADE",
                comment_out=comment_out.strip() or None,
                label=None if label == "(none)" else label,
            )
            st.session_state.grading_config = GradingConfig(
                business_description=business,
                grade_good=grade_good,
                grade_maybe=grade_maybe,
                grade_unable=grade_unable,
                extra_instructions=extra,
                use_examples=use_examples,
            )
            st.session_state.workers = workers
            st.session_state.use_examples = use_examples
            st.session_state.grade_limit = int(grade_limit)
            _reset_batch()
            ws = st.session_state.ws
            mapping = st.session_state.mapping
            tasks = build_pending_tasks(ws, mapping)
            if st.session_state.grade_limit > 0:
                tasks = tasks[: st.session_state.grade_limit]
            st.session_state.tasks = tasks
            st.session_state.step = 2
            st.rerun()


def _status_badge(status: AgentStatus) -> str:
    labels = {
        AgentStatus.IDLE: "Idle",
        AgentStatus.QUEUED: "Queued",
        AgentStatus.RUNNING: "Running",
        AgentStatus.DONE: "Done",
        AgentStatus.ERROR: "Error",
        AgentStatus.STOPPED: "Stopped",
    }
    return labels.get(status, status.value)


def _grade_color(grade: str | None) -> str:
    if grade == "GOOD":
        return "green"
    if grade == "MAYBE":
        return "orange"
    if grade == "UNABLE":
        return "red"
    return "gray"


def _drain_events() -> None:
    queue: Queue | None = st.session_state.event_queue
    if queue is None:
        return

    while True:
        try:
            kind, payload = queue.get_nowait()
        except Empty:
            break

        if kind == "activity" and isinstance(payload, AgentActivity):
            state: BatchState | None = st.session_state.batch_state
            if state and payload.slot is not None and 0 <= payload.slot < len(state.slots):
                state.slots[payload.slot] = payload
        elif kind == "progress" and isinstance(payload, BatchState):
            st.session_state.batch_state = payload
        elif kind == "done":
            st.session_state.batch_done = True
        elif kind == "error":
            st.session_state.batch_error = str(payload)
            st.session_state.batch_done = True


@st.fragment(run_every=timedelta(seconds=1))
def _live_monitor() -> None:
    _drain_events()

    state: BatchState | None = st.session_state.batch_state
    if state is None:
        return

    workers = st.session_state.workers
    completed = state.completed
    total = state.total
    st.progress(completed / total if total else 0.0, text=f"{completed} / {total} complete")

    if not st.session_state.agent_focus_manual:
        st.session_state.agent_focus_slot = _pick_auto_focus_slot(state, workers)

    focus_options = list(range(workers))
    selected = st.selectbox(
        "Focus agent",
        focus_options,
        format_func=lambda i: _agent_summary(
            state.slots[i] if i < len(state.slots) else None,
            i,
        ),
        index=st.session_state.agent_focus_slot,
        key="agent_focus_select",
    )
    if selected != st.session_state.agent_focus_slot:
        st.session_state.agent_focus_slot = selected
        st.session_state.agent_focus_manual = True

    focus = st.session_state.agent_focus_slot
    focused_slot = state.slots[focus] if focus < len(state.slots) else None

    st.subheader("Agent activity")
    _render_agent_detail(focused_slot, focus, height=420)

    st.markdown("**All agents**")
    for slot_index in range(workers):
        slot = state.slots[slot_index] if slot_index < len(state.slots) else None
        is_focused = slot_index == focus
        latest = slot.lines[-1] if slot and slot.lines else "No activity yet"
        title = _agent_summary(slot, slot_index)
        with st.expander(title, expanded=is_focused):
            if is_focused:
                st.caption("This agent is expanded above for a full readable view.")
            elif slot is None:
                st.caption("Waiting for work…")
            else:
                st.caption(slot.website)
                if slot.grade:
                    st.markdown(f":{_grade_color(slot.grade)}[**{slot.grade}**] {slot.reason or ''}")
                st.markdown(_format_activity_log(slot.lines[-6:] if slot.lines else []))
                if not is_focused:
                    st.caption(f"Latest: {latest}")

    if st.session_state.batch_done:
        if st.session_state.batch_error:
            st.error(st.session_state.batch_error)
        else:
            st.success("Grading complete!")
            ws = st.session_state.ws
            st.session_state.results_df = worksheet_to_dataframe(ws)
            if st.button("Review results →", type="primary", key="goto_review"):
                st.session_state.step = 3
                st.rerun()


def _start_batch() -> None:
    ws = st.session_state.ws
    mapping: ColumnMapping = st.session_state.mapping
    config: GradingConfig = st.session_state.grading_config
    tasks = st.session_state.tasks

    if not tasks:
        st.session_state.batch_done = True
        st.session_state.results_df = worksheet_to_dataframe(ws)
        return

    examples = []
    if config.use_examples:
        examples = collect_examples(ws, mapping, max_examples=config.max_examples)

    event_queue: Queue = Queue()
    cancel_flag = [False]

    def apply(row_index: int, grade: str, reason: str) -> None:
        apply_result(ws, mapping, row_index, grade, reason)

    initial_state = BatchState(
        total=len(tasks),
        completed=0,
        running=True,
        slots=[None] * st.session_state.workers,
    )
    st.session_state.event_queue = event_queue
    st.session_state.cancel_flag = cancel_flag
    st.session_state.batch_state = initial_state
    st.session_state.batch_done = False
    st.session_state.batch_error = None

    thread = threading.Thread(
        target=run_batch_in_thread,
        kwargs={
            "tasks": tasks,
            "examples": examples,
            "config": config,
            "workers": st.session_state.workers,
            "ws": ws,
            "mapping_grade_fn": apply,
            "event_queue": event_queue,
            "cancel_flag": cancel_flag,
            "stream": True,
        },
        daemon=True,
    )
    st.session_state.batch_thread = thread
    thread.start()


def _step_run() -> None:
    st.header("3. Grade websites")
    tasks = st.session_state.tasks
    limit_note = (
        f" (test run: first {st.session_state.grade_limit})"
        if st.session_state.grade_limit > 0
        else ""
    )
    st.markdown(
        f"**{len(tasks)}** companies to grade{limit_note} with "
        f"**{st.session_state.workers}** concurrent agents."
    )

    if not _require_api_key():
        return

    if st.session_state.batch_state is None and not st.session_state.batch_done:
        if st.button("Start grading", type="primary"):
            _start_batch()
            st.rerun()

    if st.session_state.batch_state is not None or st.session_state.batch_done:
        stop_col, back_col = st.columns([1, 1])
        with stop_col:
            if (
                st.session_state.batch_state
                and st.session_state.batch_state.running
                and not st.session_state.batch_done
                and st.button("Stop batch")
            ):
                if st.session_state.cancel_flag is not None:
                    st.session_state.cancel_flag[0] = True
                st.warning("Stopping after in-flight agents finish...")
        with back_col:
            if st.button("← Back to configuration"):
                if st.session_state.cancel_flag is not None:
                    st.session_state.cancel_flag[0] = True
                st.session_state.step = 1
                st.rerun()

        _live_monitor()

    if not tasks and st.session_state.results_df is None:
        st.info("All rows already have grades. Continue to review.")
        if st.button("Review results →", type="primary"):
            st.session_state.results_df = worksheet_to_dataframe(st.session_state.ws)
            st.session_state.step = 3
            st.rerun()


def _step_review() -> None:
    st.header("4. Review and export")

    ws = st.session_state.ws
    mapping: ColumnMapping = st.session_state.mapping
    df = st.session_state.results_df
    if df is None:
        df = worksheet_to_dataframe(ws)
        st.session_state.results_df = df

    grade_col = mapping.grade_out
    comment_col = mapping.comment_out or "Comment"

    column_config = {}
    if grade_col in df.columns:
        column_config[grade_col] = st.column_config.SelectboxColumn(
            grade_col,
            options=sorted(VALID_GRADES),
            required=False,
        )

    edited = st.data_editor(
        df,
        use_container_width=True,
        height=500,
        column_config=column_config,
        disabled=[c for c in df.columns if c not in {grade_col, comment_col}],
        key="results_editor",
    )

    if st.button("Apply manual edits"):
        apply_dataframe_edits(ws, edited, mapping)
        st.session_state.results_df = worksheet_to_dataframe(ws)
        st.success("Edits saved.")

    xlsx_bytes = workbook_to_bytes(st.session_state.wb)
    out_name = (st.session_state.upload_name or "companies").replace(".xlsx", "_graded.xlsx")
    if not out_name.endswith(".xlsx"):
        out_name += ".xlsx"

    st.download_button(
        "Download graded spreadsheet",
        data=xlsx_bytes,
        file_name=out_name,
        mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        type="primary",
    )

    if st.button("Start new batch"):
        for key in list(st.session_state.keys()):
            del st.session_state[key]
        st.rerun()


def main() -> None:
    st.set_page_config(page_title="Lead Website Grader", layout="wide")
    _init_session_state()
    _sidebar_progress()

    step = st.session_state.step
    if step == 0:
        _step_upload()
    elif step == 1:
        _step_configure()
    elif step == 2:
        _step_run()
    elif step == 3:
        _step_review()


if __name__ == "__main__":
    main()
