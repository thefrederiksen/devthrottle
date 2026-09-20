"""What every test here needs: the tool on the path, the frozen fixtures, and a way to build a
week of run records by hand for the cases the real week does not contain.

The real week is the proof that the keeper works on real records. The built records are how each
condition is shown FIRING and shown NOT firing just below its threshold, which the real week alone
could never show.
"""

from __future__ import annotations

import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

TOOL_DIR = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = TOOL_DIR.parents[1]
FIXTURES = TOOL_DIR / "tests" / "fixtures"
MAIN = TOOL_DIR / "main.py"

sys.path.insert(0, str(TOOL_DIR / "src"))
sys.path.insert(0, str(TOOL_DIR.parent))

# The record file frozen from this repository's own runs for 16 to 19 September 2026.
REAL_WEEK = FIXTURES / "run-records-2026-09-16-to-19.json"

# The budget file this repository ships, which the keeper reads in continuous integration.
REPOSITORY_BUDGET = REPOSITORY_ROOT / ".github" / "continuous-integration-budget.json"

WINDOW_FROM = datetime(2026, 9, 16, tzinfo=timezone.utc)
WINDOW_TO = datetime(2026, 9, 23, tzinfo=timezone.utc)


def moment(text: str) -> datetime:
    return datetime.strptime(text, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)


def a_job(
    name: str = "Build & Test",
    conclusion: str = "success",
    failing: tuple[str, ...] = (),
    tests: int | None = None,
    log: str = "read",
    started: str = "2026-09-16T00:00:00Z",
    minutes: float = 5.0,
):
    from records import JobRecord

    start = moment(started)
    return JobRecord(
        name=name,
        conclusion=conclusion,
        started_at=start,
        finished_at=start + timedelta(minutes=minutes),
        log=log,
        failing_tests=failing,
        tests_reported=tests,
    )


def a_run(
    identifier: int,
    *,
    event: str = "pull_request",
    branch: str = "a-branch",
    conclusion: str | None = "success",
    status: str = "completed",
    started: str = "2026-09-16T00:00:00Z",
    minutes: float = 10.0,
    jobs=(),
    jobs_read: bool = True,
):
    from records import RunRecord

    start = moment(started)
    return RunRecord(
        id=identifier,
        workflow="CI",
        event=event,
        branch=branch,
        status=status,
        conclusion=conclusion,
        created_at=start,
        started_at=start,
        finished_at=None if conclusion is None else start + timedelta(minutes=minutes),
        attempt=1,
        jobs_read=jobs_read,
        jobs=tuple(jobs),
    )


def some_records(runs, *, repository: str = "an-owner/a-repository", default_branch: str = "main"):
    from records import RunRecords

    return RunRecords(
        repository=repository,
        workflow="CI",
        default_branch=default_branch,
        collected_at=WINDOW_TO,
        window_from=WINDOW_FROM,
        window_to=WINDOW_TO,
        runs=tuple(runs),
    )


DEFAULT_BUDGET = {
    "format": "cc-continuous-integration-keeper/budget/1",
    "workflow": "CI",
    "window_days": 7,
    "budget": {"minutes": 20, "share_within_budget": 0.9, "minimum_runs": 10},
    "suite_growth": {"share": 0.2, "minimum_days_between_readings": 2},
    "repeated_red_test": {"consecutive_runs": 3, "branches": ["*"], "events": ["*"]},
    "skipped_job": {"branch": "main", "minimum_runs": 2},
}


def write_budget(path: Path, **changes) -> Path:
    """Write a budget file, with any block replaced wholesale by a keyword argument."""
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content.update(changes)
    path.write_text(json.dumps(content, indent=1) + "\n", encoding="utf-8")
    return path


@pytest.fixture
def budget(tmp_path):
    from budget import load_budget

    return load_budget(write_budget(tmp_path / "budget.json"))


@pytest.fixture
def real_week():
    from records import load_records

    return load_records(REAL_WEEK)


@pytest.fixture
def repository_budget():
    from budget import load_budget

    return load_budget(REPOSITORY_BUDGET)
