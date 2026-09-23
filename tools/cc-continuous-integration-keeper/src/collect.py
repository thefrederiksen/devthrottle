"""Reading a repository's own run records off GitHub, and turning them into RunRecord values.

This is the only module that touches the network. Everything it produces is a plain record the
keeper can judge without GitHub, which is what lets the proof run from a frozen file.

WHAT IS FETCHED, AND WHY NOT MORE
---------------------------------
  - Every run of the named workflow whose start falls inside the window. One call per hundred runs.
  - The jobs of every FINISHED run (one that concluded success or failure). A cancelled run was cut
    short by a newer push; nothing in it is judged, so its jobs are not fetched.
  - The log of every FAILED job of a finished run, because the run record does not say which test
    was red and only the log does.
  - The logs of every job of the earliest and the latest finished successful run on the default
    branch, because the number of tests a job runs is only in the log, and growth needs a reading
    at each end of the window.

Anything not fetched is recorded as not fetched ("log": "not-fetched"), never as empty. A job whose
log was asked for and refused is recorded as "unavailable". The keeper treats both as an absence of
evidence rather than as evidence of absence.
"""

from __future__ import annotations

import json
import subprocess
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import runnerlogs
from errors import KeeperError
from records import (
    LOG_NOT_FETCHED,
    LOG_READ,
    LOG_UNAVAILABLE,
    JobRecord,
    RunRecord,
    RunRecords,
    parse_time,
)

# (exit code, standard output, standard error)
CommandResult = tuple[int, str, str]
CommandRunner = Callable[[Sequence[str]], CommandResult]


def run_github_command(arguments: Sequence[str]) -> CommandResult:
    """Run the GitHub command line tool. The keeper never prompts, so a missing sign-in is an
    error with a message rather than a question."""
    try:
        finished = subprocess.run(
            ["gh", *arguments], capture_output=True, text=True, check=False
        )
    except FileNotFoundError as ex:
        raise KeeperError(
            "the GitHub command line tool 'gh' is not on the path. Install it and sign in with "
            "'gh auth login', or hand the keeper a records file collected elsewhere."
        ) from ex
    return finished.returncode, finished.stdout, finished.stderr


@dataclass
class Progress:
    """What the collection did, so the caller can print it. A collection that read nothing must
    look different from a collection that found nothing."""

    runs_listed: int = 0
    runs_in_window: int = 0
    jobs_read_for_runs: int = 0
    logs_read: int = 0
    logs_unavailable: int = 0


class GitHubRecordSource:
    """Reads run records with the GitHub command line tool."""

    def __init__(self, repository: str, runner: CommandRunner = run_github_command) -> None:
        if "/" not in repository or repository.count("/") != 1 or repository.startswith("/") or repository.endswith("/"):
            raise KeeperError(f"--repository must be written owner/name, not {repository!r}")
        self.repository = repository
        self._runner = runner
        self.progress = Progress()

    # -- the command seam -------------------------------------------------------------------

    def _api(self, path: str, paginate: bool = False) -> str:
        arguments = ["api", path]
        if paginate:
            arguments.append("--paginate")
        code, out, err = self._runner(arguments)
        if code != 0:
            raise KeeperError(
                f"reading {path} from GitHub failed (exit {code}): {err.strip() or out.strip()}"
            )
        return out

    def _api_json_objects(self, path: str, paginate: bool = False) -> list[dict]:
        """Read one or more JSON objects. With --paginate the tool writes one object per page, one
        after another, so the text is read as a stream of objects rather than as one."""
        text = self._api(path, paginate=paginate)
        decoder = json.JSONDecoder()
        objects: list[dict] = []
        index = 0
        while index < len(text):
            while index < len(text) and text[index].isspace():
                index += 1
            if index >= len(text):
                break
            value, index = decoder.raw_decode(text, index)
            if not isinstance(value, dict):
                raise KeeperError(f"reading {path} from GitHub gave something that is not an object")
            objects.append(value)
        if not objects:
            raise KeeperError(f"reading {path} from GitHub gave nothing at all")
        return objects

    # -- the pieces -------------------------------------------------------------------------

    def default_branch(self) -> str:
        return str(self._api_json_objects(f"repos/{self.repository}")[0]["default_branch"])

    def list_runs(self, window_from: datetime, window_to: datetime, workflow: str) -> list[dict]:
        # The interface filters on when a run was CREATED and the window is on when it STARTED, so
        # the request reaches a day further out at each end and the window below is what decides.
        # Without the extra day a run created at 23:58 and started after midnight would never be
        # listed, and a missing run reads exactly like a quiet night.
        created = (
            f"{(window_from - timedelta(days=1)).date().isoformat()}.."
            f"{(window_to + timedelta(days=1)).date().isoformat()}"
        )
        pages = self._api_json_objects(
            f"repos/{self.repository}/actions/runs?per_page=100&created={created}", paginate=True
        )
        raw_runs = [run for page in pages for run in page.get("workflow_runs", [])]
        self.progress.runs_listed = len(raw_runs)
        kept = [
            run
            for run in raw_runs
            if run.get("name") == workflow
            and window_from <= parse_time("run.run_started_at", run["run_started_at"]) < window_to
        ]
        self.progress.runs_in_window = len(kept)
        return kept

    def list_jobs(self, run_id: int) -> list[dict]:
        pages = self._api_json_objects(
            f"repos/{self.repository}/actions/runs/{run_id}/jobs?per_page=100", paginate=True
        )
        self.progress.jobs_read_for_runs += 1
        return [job for page in pages for job in page.get("jobs", [])]

    def read_job_log(self, job_id: int) -> str | None:
        """The job's log, or None when GitHub will not give it. Logs age out, so a missing log is
        an ordinary answer - but it is never the same answer as an empty one."""
        code, out, err = self._runner(["api", f"repos/{self.repository}/actions/jobs/{job_id}/logs"])
        if code != 0:
            self.progress.logs_unavailable += 1
            return None
        self.progress.logs_read += 1
        return out


def _job_record(raw: dict, log_text: str | None, asked_for_log: bool) -> JobRecord:
    if log_text is not None:
        reading = runnerlogs.read_log(log_text)
        return JobRecord(
            name=str(raw["name"]),
            conclusion=str(raw["conclusion"]) if raw.get("conclusion") else "none",
            started_at=parse_time("job.started_at", raw["started_at"]) if raw.get("started_at") else None,
            finished_at=parse_time("job.completed_at", raw["completed_at"]) if raw.get("completed_at") else None,
            log=LOG_READ,
            failing_tests=reading.failing_tests,
            tests_reported=reading.tests_reported,
        )
    return JobRecord(
        name=str(raw["name"]),
        conclusion=str(raw["conclusion"]) if raw.get("conclusion") else "none",
        started_at=parse_time("job.started_at", raw["started_at"]) if raw.get("started_at") else None,
        finished_at=parse_time("job.completed_at", raw["completed_at"]) if raw.get("completed_at") else None,
        log=LOG_UNAVAILABLE if asked_for_log else LOG_NOT_FETCHED,
    )


def _run_record(raw: dict, jobs: tuple[JobRecord, ...], jobs_read: bool, workflow: str) -> RunRecord:
    return RunRecord(
        id=int(raw["id"]),
        workflow=workflow,
        event=str(raw["event"]),
        branch=str(raw["head_branch"]),
        status=str(raw["status"]),
        conclusion=None if raw.get("conclusion") is None else str(raw["conclusion"]),
        created_at=parse_time("run.created_at", raw["created_at"]),
        started_at=parse_time("run.run_started_at", raw["run_started_at"]),
        finished_at=parse_time("run.updated_at", raw["updated_at"]) if raw.get("updated_at") else None,
        attempt=int(raw.get("run_attempt", 1)),
        jobs_read=jobs_read,
        jobs=jobs,
    )


def collect(
    source: GitHubRecordSource,
    workflow: str,
    window_from: datetime,
    window_to: datetime,
    now: datetime | None = None,
) -> RunRecords:
    """Collect the window's records, following the policy in this module's opening note."""
    default_branch = source.default_branch()
    raw_runs = source.list_runs(window_from, window_to, workflow)
    raw_runs.sort(key=lambda raw: raw["run_started_at"])

    finished = [
        raw
        for raw in raw_runs
        if raw.get("status") == "completed" and raw.get("conclusion") in ("success", "failure")
    ]
    green_on_default = [
        raw
        for raw in finished
        if raw.get("conclusion") == "success" and raw.get("head_branch") == default_branch
    ]
    # The two ends of the window, which is what a growth reading needs. One run at each end, not a
    # log for every green run: the logs are large and nothing else reads them.
    count_these = {raw["id"] for raw in (green_on_default[:1] + green_on_default[-1:])}

    finished_ids = {raw["id"] for raw in finished}
    runs: list[RunRecord] = []
    for raw in raw_runs:
        is_finished = raw["id"] in finished_ids
        if not is_finished:
            runs.append(_run_record(raw, (), jobs_read=False, workflow=workflow))
            continue
        want_every_log = raw["id"] in count_these
        jobs: list[JobRecord] = []
        for raw_job in source.list_jobs(int(raw["id"])):
            wants_log = want_every_log or raw_job.get("conclusion") == "failure"
            log_text = source.read_job_log(int(raw_job["id"])) if wants_log else None
            jobs.append(_job_record(raw_job, log_text, asked_for_log=wants_log))
        runs.append(_run_record(raw, tuple(jobs), jobs_read=True, workflow=workflow))

    return RunRecords(
        repository=source.repository,
        workflow=workflow,
        default_branch=default_branch,
        collected_at=(now or datetime.now(timezone.utc)).replace(microsecond=0),
        window_from=window_from,
        window_to=window_to,
        runs=tuple(runs),
    )
