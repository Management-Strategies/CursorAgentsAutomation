from __future__ import annotations

from dataclasses import dataclass, field

VALID_GRADES = frozenset({"GOOD", "MAYBE", "UNABLE"})

LEGACY_COLUMN_NAMES = {
    "website": "Website Link",
    "products": "Company primary products",
    "about": "about Company who they are selling",
    "grade_out": "WEBSITE_GRADE",
    "comment_out": "Comment",
    "label": "Company Name",
}

DEFAULT_BUSINESS_DESCRIPTION = """We are Alliance Test Equipment (alliancetesteq.com), based in Webster, MA.
We sell, rent, and lease used/refurbished electronic test & measurement
equipment -- oscilloscopes, spectrum/signal analyzers, signal generators,
meters, counters, power supplies, and wireless/optical test equipment, from
brands like Agilent/HP/Keysight, Tektronix, Anritsu, Fluke, and Rohde &
Schwarz. We also buy back customers' excess equipment and offer repair,
calibration, and consignment.

Our real customer is any company that has people on staff who need to
measure, test, calibrate, or diagnose electronic hardware -- not just any
company that happens to sell or use electronics."""

DEFAULT_GRADE_GOOD = (
    'clear evidence of in-house engineering, R&D, manufacturing, '
    'calibration, diagnostics, or repair work on electronic hardware -- '
    'these are people who plausibly own or need test & measurement gear'
)

DEFAULT_GRADE_MAYBE = (
    'electronics-adjacent, but unclear whether they do real in-house '
    'engineering/test work, or they mainly resell/install/integrate '
    'finished components without their own test function'
)

DEFAULT_GRADE_UNABLE = (
    'you could not load or meaningfully read the site (broken link, '
    'times out, blocked, parked domain, etc.)'
)

DEFAULT_EXTRA_INSTRUCTIONS = """- Actually open and read the website. Do not guess from the URL or company name alone.
- Do NOT write, edit, or create any files. Do NOT run shell commands. Only use your
  web browsing/fetch capability to view the site, then answer.
- The real question is NOT "is this company in electronics" -- it's "do they have
  people on staff who would actually use test & measurement equipment like an
  oscilloscope, signal generator, or power supply?" Look for signs of in-house
  engineering, R&D, manufacturing, calibration, diagnostics, or repair work on
  electronic hardware. A company that just resells or installs finished electronic
  products, with no visible engineering/test function, is NOT a good fit even if
  the word "electronics" is all over their site.
- Keep the reason to one short sentence, and name the specific evidence you saw
  (e.g. "runs their own ADAS calibration lab" or "just resells finished units,
  no engineering team visible")."""


@dataclass
class GradingConfig:
    business_description: str = DEFAULT_BUSINESS_DESCRIPTION
    grade_good: str = DEFAULT_GRADE_GOOD
    grade_maybe: str = DEFAULT_GRADE_MAYBE
    grade_unable: str = DEFAULT_GRADE_UNABLE
    extra_instructions: str = DEFAULT_EXTRA_INSTRUCTIONS
    model: str = "composer-2.5"
    per_row_timeout_seconds: int = 180
    max_retries: int = 1
    save_every: int = 5
    use_examples: bool = True
    max_examples: int = 5

    @classmethod
    def alliance_defaults(cls) -> GradingConfig:
        return cls()
