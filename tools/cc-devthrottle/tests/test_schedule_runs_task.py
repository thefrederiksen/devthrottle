"""`cc-devthrottle schedule runs` prints the task status the Gateway folds (Factory Definition, phase 5b follow-up).

The phase 5b QA report found Task `unknown` printed for a run whose check found nothing to do. The Gateway now answers
`checking`, `nothing-to-do` or `check-failed` for such a run (CronRunEndingFold.TaskStatusOf); the command prints that
word as it comes, never deciding one itself, and `--json` keeps the Gateway's answer byte for byte.
"""

import io
import json
import sys
from pathlib import Path

from rich.console import Console
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import schedule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _run(infra, task, session=None):
    return {
        "scheduledUtc": "2026-10-16T01:26:00Z",
        "firedUtc": "2026-10-16T01:26:01Z",
        "machine": "SOREN_NORTH",
        "targetDirectorId": "fda95749-1c67-4b4e-b75c-5526e91736ac",
        "sessionId": session,
        "infraStatus": infra,
        "taskStatus": task,
        "result": "nothing-to-do",
        "ending": "nothing-to-do",
        "endingText": "nothing to do - its check found no work, so no session was needed",
    }


RUNS = [
    _run("nothing-to-do", "nothing-to-do"),
    _run("check-due", "checking"),
    _run("check-failed", "check-failed"),
    _run("started", "closed-itself", session="5f182646-342c-4b49-abaa-49bdf35b05f0"),
]


def _serve(monkeypatch, runs):
    monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(schedule_ops.ScheduleClient, "list_runs", lambda self, job_id: runs)
    out = io.StringIO()
    monkeypatch.setattr(schedule_ops, "console", Console(file=out, width=240, color_system=None))
    return out


def test_schedule_runs_CheckedRuns_PrintTheGatewaysTaskWordNotUnknown(monkeypatch):
    out = _serve(monkeypatch, RUNS)

    result = runner.invoke(app, ["schedule", "runs", "cj_03510b"])

    assert result.exit_code == 0, result.output
    rows = [line for line in out.getvalue().splitlines() if "2026-10-16T01:26:00Z" in line]
    tasks = [row.strip().strip("|").split("|")[-1].strip() for row in rows]
    assert tasks == ["nothing-to-do", "checking", "check-failed", "closed-itself"]
    assert "unknown" not in out.getvalue()


def test_schedule_runs_Json_IsTheGatewaysRunsUnchanged(monkeypatch):
    _serve(monkeypatch, RUNS)

    result = runner.invoke(app, ["schedule", "runs", "cj_03510b", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == RUNS
