"""
prompts.py

Prompt construction and result parsing for the website-grading agent, shared
between the CLI script (main.py) and the Streamlit app. Unlike main.py's
original version, the business description and grading rubric are parameters
here (not hardcoded), so the Streamlit UI can let a user edit them per run.
"""

from __future__ import annotations

import json
import re

VALID_GRADES = {"GOOD", "MAYBE", "UNABLE"}

# ---------------------------------------------------------------------------
# Editable defaults -- shown pre-filled in the Streamlit "Grading criteria"
# panel; a user can change these per session without touching code.
# ---------------------------------------------------------------------------

DEFAULT_WHO_WE_ARE = """We are Alliance Test Equipment (alliancetesteq.com), based in Webster, MA.
We sell, rent, and lease used/refurbished electronic test & measurement
equipment -- oscilloscopes, spectrum/signal analyzers, signal generators,
meters, counters, power supplies, and wireless/optical test equipment, from
brands like Agilent/HP/Keysight, Tektronix, Anritsu, Fluke, and Rohde &
Schwarz. We also buy back customers' excess equipment and offer repair,
calibration, and consignment.

Our real customer is any company that has people on staff who need to
measure, test, calibrate, or diagnose electronic hardware -- not just any
company that happens to sell or use electronics."""

DEFAULT_GRADE_RUBRIC = """The real question is NOT "is this company in electronics" -- it's "do they have
people on staff who would actually use test & measurement equipment like an
oscilloscope, signal generator, or power supply?" Look for signs of in-house
engineering, R&D, manufacturing, calibration, diagnostics, or repair work on
electronic hardware. A company that just resells or installs finished electronic
products, with no visible engineering/test function, is NOT a good fit even if
the word "electronics" is all over their site.

Grade the company using ONLY one of these three values:
- "GOOD"   -> clear evidence of in-house engineering, R&D, manufacturing,
              calibration, diagnostics, or repair work on electronic hardware --
              these are people who plausibly own or need test & measurement gear
- "MAYBE"  -> electronics-adjacent, but unclear whether they do real in-house
              engineering/test work, or they mainly resell/install/integrate
              finished components without their own test function
- "UNABLE" -> you could not load or meaningfully read the site (broken link,
              times out, blocked, parked domain, etc.)"""


def build_prompt(
    website: str,
    fields: dict[str, str],
    who_we_are: str,
    grade_rubric: str,
    examples: list[dict],
) -> str:
    """Build the grading prompt.

    fields: ordered {column label: value} for whichever columns the user
    picked as "what should the AI read" (besides the website itself).
    examples: [{"website":..., "fields": {label: value, ...}, "grade": ...}]
    """
    field_lines = "\n".join(f"{label}: {value}" for label, value in fields.items())

    example_block = ""
    if examples:
        lines = []
        for ex in examples:
            ex_field_lines = "\n  ".join(
                f"{label}: {value}" for label, value in ex.get("fields", {}).items()
            )
            lines.append(
                f'- Website: {ex["website"]}\n  {ex_field_lines}\n  -> Grade: {ex["grade"]}'
            )
        example_block = (
            "\nHere are a few examples of how these companies were graded before, "
            "for calibration:\n" + "\n".join(lines) + "\n"
        )

    return f"""You are screening a B2B lead for outreach on our behalf.

{who_we_are}

Now research this prospect:

Website: {website}
{field_lines}
{example_block}
Instructions:
- Actually open and read the website. Do not guess from the URL or company name alone.
- Do NOT write, edit, or create any files. Do NOT run shell commands. Only use your
  web browsing/fetch capability to view the site, then answer.
- {grade_rubric}
- Keep the reason to one short sentence, and name the specific evidence you saw
  (e.g. "runs their own ADAS calibration lab" or "just resells finished units,
  no engineering team visible").

Respond with ONLY this exact JSON shape and nothing else -- no markdown, no code
fences, no extra commentary:
{{"grade": "GOOD", "reason": "one short sentence"}}
"""


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
