"""The budget file: one file a person edits, read strictly.

The keeper reads every threshold it uses from this one file, so changing what "fast enough" means
is an edit to a file and never an edit to code. The file is validated strictly - an unknown key, a
missing key, a value of the wrong type or a value out of range is an error that names the key. A
budget file that half-loads would quietly measure against a default nobody chose, and a threshold
nobody chose is the same as no threshold at all.

The file is JSON so that reading it needs nothing beyond the standard library.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from errors import KeeperError

FORMAT = "cc-continuous-integration-keeper/budget/1"

# The one entry that means "every branch" or "every event" in repeated_red_test.
EVERY = "*"


@dataclass(frozen=True)
class RunBudget:
    """Condition 1: how long a pull request may wait for a result."""

    minutes: float
    share_within_budget: float
    minimum_runs: int


@dataclass(frozen=True)
class SuiteGrowth:
    """Condition 2: how much the number of tests a job runs may grow inside the window."""

    share: float
    minimum_days_between_readings: float


@dataclass(frozen=True)
class RepeatedRedTest:
    """Condition 3: how many finished runs in a row may be red with the same test named."""

    consecutive_runs: int
    branches: tuple[str, ...]
    events: tuple[str, ...]


@dataclass(frozen=True)
class SkippedJob:
    """Condition 4: the skipped-job rule, which is measured over finished runs on one branch."""

    branch: str
    minimum_runs: int


@dataclass(frozen=True)
class Budget:
    """Everything the keeper measures against, and what it measures it over."""

    path: Path
    workflow: str
    window_days: float
    notes: tuple[str, ...]
    run_budget: RunBudget
    suite_growth: SuiteGrowth
    repeated_red_test: RepeatedRedTest
    skipped_job: SkippedJob


def _require_keys(
    where: str, value: Any, required: list[str], optional: list[str] | None = None
) -> dict[str, Any]:
    optional = optional or []
    if not isinstance(value, dict):
        raise KeeperError(f"{where} must be an object, not {type(value).__name__}")
    missing = [key for key in required if key not in value]
    if missing:
        raise KeeperError(f"{where} is missing: {', '.join(missing)}")
    unknown = [key for key in value if key not in required and key not in optional]
    if unknown:
        raise KeeperError(
            f"{where} has key(s) the keeper does not know: {', '.join(sorted(unknown))}. "
            f"Known keys: {', '.join(required + optional)}"
        )
    return value


def _number(where: str, value: Any, minimum: float, maximum: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise KeeperError(f"{where} must be a number, not {type(value).__name__}")
    if not minimum <= float(value) <= maximum:
        raise KeeperError(f"{where} must be between {minimum} and {maximum}, not {value}")
    return float(value)


def _whole_number(where: str, value: Any, minimum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise KeeperError(f"{where} must be a whole number, not {type(value).__name__}")
    if value < minimum:
        raise KeeperError(f"{where} must be {minimum} or more, not {value}")
    return value


def _text(where: str, value: Any) -> str:
    if not isinstance(value, str) or value.strip() == "":
        raise KeeperError(f"{where} must be a non-empty piece of text")
    return value


def _text_list(where: str, value: Any) -> tuple[str, ...]:
    if not isinstance(value, list) or not value:
        raise KeeperError(f"{where} must be a list holding at least one piece of text")
    return tuple(_text(f"{where}[{index}]", item) for index, item in enumerate(value))


def _scope_list(where: str, value: Any) -> tuple[str, ...]:
    """A list of branch names or event names, or the single entry "*" meaning every one of them.

    "*" alongside named entries would read as if the names narrowed anything, and they would not,
    so it is refused rather than quietly ignored.
    """
    names = _text_list(where, value)
    if EVERY in names and len(names) > 1:
        raise KeeperError(
            f'{where} holds "{EVERY}", which means every one, together with '
            f"{len(names) - 1} named entry(ies). Write one or the other."
        )
    return names


def load_budget(path: Path) -> Budget:
    """Read and validate the budget file. Every failure names the key that is wrong."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as ex:
        raise KeeperError(f"cannot read the budget file {path}: {ex}") from ex
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as ex:
        raise KeeperError(f"the budget file {path} is not valid JSON: {ex}") from ex

    top = _require_keys(
        "the budget file",
        raw,
        ["format", "workflow", "window_days", "budget", "suite_growth", "repeated_red_test", "skipped_job"],
        optional=["notes"],
    )
    if top["format"] != FORMAT:
        raise KeeperError(
            f"the budget file {path} says format {top['format']!r}; this keeper reads {FORMAT!r}"
        )

    budget_block = _require_keys(
        "budget", top["budget"], ["minutes", "share_within_budget", "minimum_runs"]
    )
    growth_block = _require_keys(
        "suite_growth", top["suite_growth"], ["share", "minimum_days_between_readings"]
    )
    red_block = _require_keys(
        "repeated_red_test", top["repeated_red_test"], ["consecutive_runs", "branches", "events"]
    )
    skipped_block = _require_keys("skipped_job", top["skipped_job"], ["branch", "minimum_runs"])

    return Budget(
        path=path,
        notes=_text_list("notes", top["notes"]) if "notes" in top else (),
        workflow=_text("workflow", top["workflow"]),
        window_days=_number("window_days", top["window_days"], 1, 365),
        run_budget=RunBudget(
            minutes=_number("budget.minutes", budget_block["minutes"], 1, 24 * 60),
            share_within_budget=_number(
                "budget.share_within_budget", budget_block["share_within_budget"], 0.5, 1.0
            ),
            minimum_runs=_whole_number("budget.minimum_runs", budget_block["minimum_runs"], 1),
        ),
        suite_growth=SuiteGrowth(
            share=_number("suite_growth.share", growth_block["share"], 0.01, 10.0),
            minimum_days_between_readings=_number(
                "suite_growth.minimum_days_between_readings",
                growth_block["minimum_days_between_readings"],
                0.0,
                365.0,
            ),
        ),
        repeated_red_test=RepeatedRedTest(
            consecutive_runs=_whole_number(
                "repeated_red_test.consecutive_runs", red_block["consecutive_runs"], 2
            ),
            branches=_scope_list("repeated_red_test.branches", red_block["branches"]),
            events=_scope_list("repeated_red_test.events", red_block["events"]),
        ),
        skipped_job=SkippedJob(
            branch=_text("skipped_job.branch", skipped_block["branch"]),
            minimum_runs=_whole_number("skipped_job.minimum_runs", skipped_block["minimum_runs"], 2),
        ),
    )
