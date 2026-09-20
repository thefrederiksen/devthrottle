"""The run records the keeper reads, and the file they are frozen into.

The keeper never looks at GitHub while it is judging. It judges a list of RunRecord values, and
those come either from a collection run (collect.py, which calls the GitHub command line tool) or
from a file that was collected earlier. That split is what lets a test feed the keeper a real week
of a real repository's records with no network and no dependence on today's date.

Every time is a moment in Coordinated Universal Time, written the way GitHub writes it
("2026-09-19T03:24:09Z"), and parsed into an aware datetime.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from errors import KeeperError

FORMAT = "cc-continuous-integration-keeper/run-records/1"

# What the keeper knows about a job's log.
LOG_READ = "read"  # the log was downloaded and read
LOG_NOT_FETCHED = "not-fetched"  # the collector did not ask for it, by its own policy
LOG_UNAVAILABLE = "unavailable"  # it was asked for and GitHub did not give it
LOG_STATES = (LOG_READ, LOG_NOT_FETCHED, LOG_UNAVAILABLE)

FINISHED_CONCLUSIONS = ("success", "failure")


def parse_time(where: str, text: Any) -> datetime:
    if not isinstance(text, str):
        raise KeeperError(f"{where} must be a time written as text, not {type(text).__name__}")
    try:
        moment = datetime.strptime(text, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as ex:
        raise KeeperError(f"{where} is not a time of the form 2026-09-19T03:24:09Z: {text!r}") from ex
    return moment.replace(tzinfo=timezone.utc)


def write_time(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


@dataclass(frozen=True)
class JobRecord:
    """One job of one run, plus whatever its log said."""

    name: str
    conclusion: str
    started_at: datetime | None
    finished_at: datetime | None
    log: str
    failing_tests: tuple[str, ...] = ()
    tests_reported: int | None = None

    @property
    def minutes(self) -> float | None:
        if self.started_at is None or self.finished_at is None:
            return None
        return (self.finished_at - self.started_at).total_seconds() / 60

    def as_json(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "conclusion": self.conclusion,
            "started_at": write_time(self.started_at) if self.started_at else None,
            "finished_at": write_time(self.finished_at) if self.finished_at else None,
            "log": self.log,
            "failing_tests": list(self.failing_tests),
            "tests_reported": self.tests_reported,
        }

    @staticmethod
    def from_json(raw: Any, where: str) -> "JobRecord":
        if not isinstance(raw, dict):
            raise KeeperError(f"{where} must be an object")
        log = raw.get("log")
        if log not in LOG_STATES:
            raise KeeperError(f"{where}.log must be one of {', '.join(LOG_STATES)}, not {log!r}")
        tests_reported = raw.get("tests_reported")
        if tests_reported is not None and (
            isinstance(tests_reported, bool) or not isinstance(tests_reported, int)
        ):
            raise KeeperError(f"{where}.tests_reported must be a whole number or nothing")
        failing = raw.get("failing_tests", [])
        if not isinstance(failing, list) or any(not isinstance(name, str) for name in failing):
            raise KeeperError(f"{where}.failing_tests must be a list of test names")
        return JobRecord(
            name=str(raw["name"]),
            conclusion=str(raw["conclusion"]),
            started_at=parse_time(f"{where}.started_at", raw["started_at"]) if raw.get("started_at") else None,
            finished_at=parse_time(f"{where}.finished_at", raw["finished_at"]) if raw.get("finished_at") else None,
            log=log,
            failing_tests=tuple(failing),
            tests_reported=tests_reported,
        )


@dataclass(frozen=True)
class RunRecord:
    """One run of one workflow."""

    id: int
    workflow: str
    event: str
    branch: str
    status: str
    conclusion: str | None
    created_at: datetime
    started_at: datetime
    finished_at: datetime | None
    attempt: int
    jobs_read: bool
    jobs: tuple[JobRecord, ...] = ()

    @property
    def finished(self) -> bool:
        """A run that produced a result. A cancelled run did not: it was cut short by a newer push,
        so its length measures the cancelling and says nothing about how long a result takes."""
        return self.status == "completed" and self.conclusion in FINISHED_CONCLUSIONS

    @property
    def minutes(self) -> float | None:
        """How long the run took, from when it started to when it ended."""
        if self.finished_at is None:
            return None
        return (self.finished_at - self.started_at).total_seconds() / 60

    def failing_tests(self) -> tuple[str, ...]:
        names: list[str] = []
        for job in self.jobs:
            for name in job.failing_tests:
                if name not in names:
                    names.append(name)
        return tuple(names)

    def unread_failed_jobs(self) -> tuple[str, ...]:
        """Failed jobs whose log the keeper does not have, so it cannot say which test was red."""
        return tuple(
            job.name for job in self.jobs if job.conclusion == "failure" and job.log != LOG_READ
        )

    def as_json(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "workflow": self.workflow,
            "event": self.event,
            "branch": self.branch,
            "status": self.status,
            "conclusion": self.conclusion,
            "created_at": write_time(self.created_at),
            "started_at": write_time(self.started_at),
            "finished_at": write_time(self.finished_at) if self.finished_at else None,
            "attempt": self.attempt,
            "jobs_read": self.jobs_read,
            "jobs": [job.as_json() for job in self.jobs],
        }

    @staticmethod
    def from_json(raw: Any, where: str) -> "RunRecord":
        if not isinstance(raw, dict):
            raise KeeperError(f"{where} must be an object")
        for key in ("id", "workflow", "event", "branch", "status", "created_at", "started_at", "attempt", "jobs_read"):
            if key not in raw:
                raise KeeperError(f"{where} is missing {key}")
        if not isinstance(raw["jobs_read"], bool):
            raise KeeperError(f"{where}.jobs_read must be true or false")
        jobs = raw.get("jobs", [])
        if not isinstance(jobs, list):
            raise KeeperError(f"{where}.jobs must be a list")
        return RunRecord(
            id=int(raw["id"]),
            workflow=str(raw["workflow"]),
            event=str(raw["event"]),
            branch=str(raw["branch"]),
            status=str(raw["status"]),
            conclusion=None if raw.get("conclusion") is None else str(raw["conclusion"]),
            created_at=parse_time(f"{where}.created_at", raw["created_at"]),
            started_at=parse_time(f"{where}.started_at", raw["started_at"]),
            finished_at=parse_time(f"{where}.finished_at", raw["finished_at"]) if raw.get("finished_at") else None,
            attempt=int(raw["attempt"]),
            jobs_read=bool(raw["jobs_read"]),
            jobs=tuple(JobRecord.from_json(job, f"{where}.jobs[{index}]") for index, job in enumerate(jobs)),
        )


@dataclass(frozen=True)
class RunRecords:
    """A whole collection: which repository, which workflow, which window, and the runs."""

    repository: str
    workflow: str
    default_branch: str
    collected_at: datetime
    window_from: datetime
    window_to: datetime
    runs: tuple[RunRecord, ...] = field(default=())

    def as_json(self) -> dict[str, Any]:
        return {
            "format": FORMAT,
            "repository": self.repository,
            "workflow": self.workflow,
            "default_branch": self.default_branch,
            "collected_at": write_time(self.collected_at),
            "window": {"from": write_time(self.window_from), "to": write_time(self.window_to)},
            "runs": [run.as_json() for run in self.runs],
        }

    def write(self, path: Path) -> None:
        path.write_text(json.dumps(self.as_json(), indent=1) + "\n", encoding="utf-8")


def load_records(path: Path) -> RunRecords:
    """Read a frozen record file, strictly. Anything unexpected is an error that names the place."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as ex:
        raise KeeperError(f"cannot read the records file {path}: {ex}") from ex
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as ex:
        raise KeeperError(f"the records file {path} is not valid JSON: {ex}") from ex
    if not isinstance(raw, dict):
        raise KeeperError(f"the records file {path} must hold an object")
    if raw.get("format") != FORMAT:
        raise KeeperError(
            f"the records file {path} says format {raw.get('format')!r}; this keeper reads {FORMAT!r}"
        )
    for key in ("repository", "workflow", "default_branch", "collected_at", "window", "runs"):
        if key not in raw:
            raise KeeperError(f"the records file {path} is missing {key}")
    window = raw["window"]
    if not isinstance(window, dict) or "from" not in window or "to" not in window:
        raise KeeperError(f"the records file {path} needs window.from and window.to")
    runs = raw["runs"]
    if not isinstance(runs, list):
        raise KeeperError(f"the records file {path}: runs must be a list")
    return RunRecords(
        repository=str(raw["repository"]),
        workflow=str(raw["workflow"]),
        default_branch=str(raw["default_branch"]),
        collected_at=parse_time("collected_at", raw["collected_at"]),
        window_from=parse_time("window.from", window["from"]),
        window_to=parse_time("window.to", window["to"]),
        runs=tuple(RunRecord.from_json(run, f"runs[{index}]") for index, run in enumerate(runs)),
    )
