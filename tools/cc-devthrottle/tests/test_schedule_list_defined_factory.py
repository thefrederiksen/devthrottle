"""`schedule list` and a defined factory's schedule (Factory Definition, phase 5, QA finding 5).

A defined factory's run is built from its published definition, not started in a folder, so the Gateway writes
the schedule's `action.repoPath` empty and says where the folder goes in `pathText`. The command line used to
refuse the whole list on that empty folder. It now shows the Gateway's words in the folder's place, on every
path that reads the rows, and decides nothing itself: a blank folder with no `pathText` - a schedule of an
undefined factory - is still the broken row it always was.

And `schedule run` (QA finding 6): a run whose seat the Director refused answers with its recorded run, and the
reason the Gateway recorded - the Director's own sentence - is printed, so a person sees why nothing started.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import schedule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

BUILT_FROM_THE_DEFINITION = "built from the factory's definition"


def _job(job_id, name, *, repo, path_text=None, defined=False, factory=None, seat=None):
    job = {
        "id": job_id,
        "name": name,
        "enabled": True,
        "scheduleKind": "recurring",
        "cronExpression": "0 6 * * *",
        "runAt": None,
        "timeZoneId": "UTC",
        "factory": factory,
        "seat": seat,
        "target": {"machine": "SOREN_NORTH"},
        "action": {"repoPath": repo, "seed": "Morning round.", "workListName": None, "autoDismiss": True},
        "notifyOn": "none",
        "notifyWebhookUrl": None,
        "preventOverlap": True,
        "nextRunUtc": "2026-10-16T06:00:00Z",
        "lastFiredUtc": None,
        "lastStatus": None,
        "createdUtc": "2026-10-15T20:00:00Z",
        "definedByDefinition": defined,
        "pathText": path_text,
    }
    return job


def _defined(job_id="cj_19cb4d", repo=""):
    """A defined factory's schedule as phase 5 writes it: no folder, and the Gateway's words for where it is built."""
    return _job(job_id, "QA Kit - Boss", repo=repo, path_text=BUILT_FROM_THE_DEFINITION, defined=True,
                factory="qa-kit", seat="boss")


def _plain():
    return _job("cj_plain1", "Nightly triage", repo="D:\\ReposFred\\devthrottle")


@pytest.fixture
def serve(monkeypatch):
    """Serve a chosen list of jobs through the Gateway client, with no real HTTP."""

    def serve_(jobs):
        monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
        monkeypatch.setattr(schedule_ops.ScheduleClient, "list_jobs", lambda self: jobs)

    return serve_


READ_PATHS = [
    ["schedule", "list"],
    ["schedule", "list", "--fields", ",".join(schedule_ops.SCHEDULE_LIST_FIELDS)],
    ["schedule", "list", "--machine", "SOREN_NORTH"],
    ["schedule", "list", "--enabled"],
    ["schedule", "list", "--enabled", "--json"],
]


@pytest.mark.parametrize("args", READ_PATHS)
def test_schedule_list_ADefinedFactorysScheduleWithNoFolder_IsListedOnEveryPath(serve, args):
    serve([_defined(), _plain()])

    result = runner.invoke(app, args)

    assert result.exit_code == 0, result.stderr
    assert "blank action.repoPath" not in result.stderr


def test_schedule_list_TheFolderField_ShowsTheGatewaysWordsForADefinedSchedule_AndTheFolderOtherwise(serve):
    serve([_defined(), _plain()])

    result = runner.invoke(app, ["schedule", "list", "--fields", "id,path"])

    assert result.exit_code == 0, result.stderr
    _, records = parse_list(result.stdout, "schedules")
    assert [(r["id"], r["path"]) for r in records] == [
        ("cj_19cb4d", BUILT_FROM_THE_DEFINITION),
        ("cj_plain1", "D:\\ReposFred\\devthrottle"),
    ]


def test_schedule_list_ADefinedScheduleStillHoldingAnOldFolder_ShowsTheGatewaysWords(serve):
    # A publish over a factory that had a folder keeps it on the row, and no run starts in it.
    serve([_defined(repo="D:\\ReposFred\\old-folder")])

    result = runner.invoke(app, ["schedule", "list", "--fields", "id,path"])

    assert result.exit_code == 0, result.stderr
    _, records = parse_list(result.stdout, "schedules")
    assert records[0]["path"] == BUILT_FROM_THE_DEFINITION


def test_schedule_list_Json_IsTheGatewaysRowsUnchanged(serve):
    jobs = [_defined(), _plain()]
    serve(jobs)

    result = runner.invoke(app, ["schedule", "list", "--json"])

    assert result.exit_code == 0, result.stderr
    assert json.loads(result.stdout) == jobs


@pytest.mark.parametrize("path_text", [None, "absent"])
def test_schedule_list_UNDEFINED_FACTORY_ABlankFolderWithNoPathText_IsStillABrokenRow(serve, path_text):
    # The Gateway never stores a blank folder on an undefined factory's schedule, and an older Gateway sends no
    # pathText at all: both are refused exactly as before.
    job = _job("cj_0rphan", "orphan", repo="")
    if path_text == "absent":
        del job["pathText"]
    serve([job])

    result = runner.invoke(app, ["schedule", "list"])

    assert result.exit_code == 1
    assert ("the Gateway returned schedule cj_0rphan with a blank action.repoPath; the Gateway never stores one."
            in result.stderr)
    assert result.stdout == ""


def test_schedule_list_UNDEFINED_FACTORY_AScheduleWithAFolder_ShowsTheFolderAsBefore(serve):
    job = _plain()
    del job["pathText"]
    serve([job])

    result = runner.invoke(app, ["schedule", "list", "--fields", "id,path"])

    assert result.exit_code == 0, result.stderr
    _, records = parse_list(result.stdout, "schedules")
    assert records[0]["path"] == "D:\\ReposFred\\devthrottle"


@pytest.mark.parametrize("path_text, said", [
    ("", "a blank pathText"),
    ("   ", "a blank pathText"),
    (7, "pathText a int"),
])
def test_schedule_list_APathTextTheGatewayNeverSends_IsABrokenRow(serve, path_text, said):
    job = _defined()
    job["pathText"] = path_text
    serve([job])

    result = runner.invoke(app, ["schedule", "list"])

    assert result.exit_code == 1
    assert "schedule cj_19cb4d with " + said in result.stderr
    assert "Traceback" not in result.stderr


def test_schedule_list_ADefinedScheduleWhoseFolderIsNotText_IsStillABrokenRow(serve):
    job = _defined()
    job["action"]["repoPath"] = None
    serve([job])

    result = runner.invoke(app, ["schedule", "list"])

    assert result.exit_code == 1
    assert "schedule cj_19cb4d with action.repoPath null" in result.stderr


# ---------- schedule run: the reason a run started nothing (QA finding 6) ----------

REFUSED = ("The seat keeper needs the secret 'qa-p6-no-such-secret', which SOREN_NORTH does not hold, so no "
           "session was started. Add it there with cc-secrets.")


def _serve_run(monkeypatch, record):
    monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(schedule_ops.ScheduleClient, "run_now", lambda self, job_id: record)


def test_schedule_run_ASeatTheDirectorRefused_PrintsTheReasonTheGatewayRecorded(monkeypatch):
    _serve_run(monkeypatch, {
        "firedUtc": "2026-10-16T00:19:00Z", "targetDirectorId": "d-1", "sessionId": None,
        "infraStatus": "not-started", "taskStatus": "unknown",
        "resultReason": "it did not start a session: " + REFUSED,
    })

    result = runner.invoke(app, ["schedule", "run", "cj_19cb4d"])

    assert result.exit_code == 0, result.stderr
    assert "  Reason:  it did not start a session: " + REFUSED in result.stdout.splitlines()


def test_schedule_run_UNDEFINED_FACTORY_ARunThatStarted_PrintsNoReasonLine(monkeypatch):
    _serve_run(monkeypatch, {
        "firedUtc": "2026-10-16T00:19:00Z", "targetDirectorId": "d-1", "sessionId": "s-1",
        "infraStatus": "started", "taskStatus": "unknown", "resultReason": None,
    })

    result = runner.invoke(app, ["schedule", "run", "cj_plain1"])

    assert result.exit_code == 0, result.stderr
    assert "Reason:" not in result.stdout
    assert "  Session: s-1" in result.stdout.splitlines()
