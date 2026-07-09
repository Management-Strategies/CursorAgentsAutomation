from __future__ import annotations

import json
import re

from grader.config import VALID_GRADES


def parse_grade(raw_text: str) -> tuple[str, str]:
    """Best-effort extraction of {grade, reason} from the agent's final text."""
    text = raw_text.strip()

    text_stripped = re.sub(r"^```(?:json)?|```$", "", text.strip(), flags=re.MULTILINE).strip()
    try:
        data = json.loads(text_stripped)
        grade = str(data.get("grade", "")).strip().upper()
        reason = str(data.get("reason", "")).strip()
        if grade in VALID_GRADES:
            return grade, reason or "(no reason given)"
    except (json.JSONDecodeError, AttributeError):
        pass

    match = re.search(r"\b(GOOD|MAYBE|UNABLE)\b", text.upper())
    if match:
        return match.group(1), text[:200]

    return "UNABLE", f"Could not parse agent response: {text[:200]}"
