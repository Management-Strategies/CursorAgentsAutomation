from __future__ import annotations

from grader.config import GradingConfig
from grader.models import ExampleRow


def build_prompt(
    config: GradingConfig,
    website: str,
    context: dict[str, str],
    examples: list[ExampleRow],
) -> str:
    context_lines = "\n".join(f"{key}: {value}" for key, value in context.items() if value)

    example_block = ""
    if examples:
        lines = []
        for ex in examples:
            ctx = "\n".join(f"  {k}: {v}" for k, v in ex.context.items() if v)
            lines.append(
                f"- Website: {ex.website}\n"
                f"{ctx}\n"
                f"  -> Grade: {ex.grade}"
            )
        example_block = (
            "\nHere are a few examples of how these companies were graded before, "
            "for calibration:\n" + "\n".join(lines) + "\n"
        )

    return f"""You are screening a B2B lead for outreach on our behalf.

{config.business_description}

Now research this prospect:

Website: {website}
{context_lines}
{example_block}
Instructions:
{config.extra_instructions}
- Grade the company using ONLY one of these three values:
  - "GOOD"   -> {config.grade_good}
  - "MAYBE"  -> {config.grade_maybe}
  - "UNABLE" -> {config.grade_unable}

Respond with ONLY this exact JSON shape and nothing else -- no markdown, no code
fences, no extra commentary:
{{"grade": "GOOD", "reason": "one short sentence"}}
"""
