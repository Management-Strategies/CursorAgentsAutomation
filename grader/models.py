from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class AgentStatus(str, Enum):
    IDLE = "idle"
    QUEUED = "queued"
    RUNNING = "running"
    DONE = "done"
    ERROR = "error"
    STOPPED = "stopped"


@dataclass
class ColumnMapping:
    website: str
    context_columns: list[str]
    grade_out: str = "WEBSITE_GRADE"
    comment_out: str | None = "Comment"
    label: str | None = None


@dataclass
class RowTask:
    task_id: str
    row_index: int
    label: str
    website: str
    context: dict[str, str]


@dataclass
class AgentActivity:
    task_id: str
    row_index: int
    label: str
    website: str
    status: AgentStatus = AgentStatus.QUEUED
    lines: list[str] = field(default_factory=list)
    grade: str | None = None
    reason: str | None = None
    slot: int | None = None
    thinking_buffer: str = ""
    assistant_buffer: str = ""

    def append_line(self, line: str, max_lines: int = 30) -> None:
        if not line.strip():
            return
        self.lines.append(line.strip())
        if len(self.lines) > max_lines:
            self.lines = self.lines[-max_lines:]

    def upsert_prefixed_line(self, prefix: str, content: str, max_lines: int = 30) -> None:
        line = f"{prefix}{content}".strip()
        if not line:
            return
        for i in range(len(self.lines) - 1, -1, -1):
            if self.lines[i].startswith(prefix):
                self.lines[i] = line
                return
        self.append_line(line, max_lines)

    def replace_prefixed_line(self, old_prefix: str, new_line: str, max_lines: int = 30) -> None:
        new_line = new_line.strip()
        if not new_line:
            return
        for i in range(len(self.lines) - 1, -1, -1):
            if self.lines[i].startswith(old_prefix):
                self.lines[i] = new_line
                return
        self.append_line(new_line, max_lines)

    def remove_prefixed_lines(self, prefix: str) -> None:
        self.lines = [line for line in self.lines if not line.startswith(prefix)]


@dataclass
class BatchState:
    total: int = 0
    completed: int = 0
    running: bool = False
    stopped: bool = False
    error: str | None = None
    slots: list[AgentActivity | None] = field(default_factory=list)
    results: dict[int, tuple[str, str]] = field(default_factory=dict)

    def slot_for_task(self, task_id: str) -> AgentActivity | None:
        for slot in self.slots:
            if slot and slot.task_id == task_id:
                return slot
        return None


@dataclass
class ExampleRow:
    website: str
    context: dict[str, str]
    grade: str

    def to_prompt_dict(self) -> dict[str, Any]:
        data = {"website": self.website, "grade": self.grade}
        data.update(self.context)
        return data
