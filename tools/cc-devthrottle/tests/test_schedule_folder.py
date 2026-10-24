"""Issue #4421: a schedule's folder is checked on its machine when it is saved. The Gateway refuses a folder the machine
does not have - or a Windows path for a Mac - and warns when the machine could not be asked; this tool prints both in
the Gateway's own words."""

import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src.schedule_ops import GatewayError  # noqa: E402

runner = CliRunner()

_CREATE = [
    "schedule", "create",
    "--name", "Nightly tidy",
    "--machine", "STUDIO_MAC",
    "--repo", "/Users/pat/Documents/project",
    "--cron", "0 3 * * *",
    "--tz", "UTC",
    "--seed", "tidy the repository",
]

_OFFLINE = ("STUDIO_MAC is offline, so the folder '/Users/pat/Documents/project' could not be checked. "
            "It is checked again when the schedule first runs.")

_REFUSAL = (r"'\\documents\\devthrottle' is a Windows path, but STUDIO_MAC is a Mac. "
            "Use a path like /Users/<you>/Documents/devthrottle.")


def _created(**extra):
    return {"id": "cj_new", "name": "Nightly tidy", "scheduleKind": "recurring", "cronExpression": "0 3 * * *",
            "nextRunUtc": "2026-10-25T03:00:00Z", **extra}


def test_create_for_an_offline_machine_is_saved_and_prints_the_folder_warning():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.create_job.return_value = _created(folderWarning=_OFFLINE)
        result = runner.invoke(app, _CREATE)

    assert result.exit_code == 0, result.output
    assert "Created schedule." in result.output
    assert f"WARNING:   {_OFFLINE}" in result.output


def test_create_whose_folder_was_checked_prints_no_warning():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.create_job.return_value = _created(folderWarning=None)
        result = runner.invoke(app, _CREATE)

    assert result.exit_code == 0, result.output
    assert "WARNING" not in result.output


def test_create_with_a_windows_path_for_a_mac_fails_with_the_gateways_sentence():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.create_job.side_effect = GatewayError(f"the Gateway refused the schedule: {_REFUSAL}")
        result = runner.invoke(app, _CREATE)

    assert result.exit_code != 0
    assert "is a Windows path, but STUDIO_MAC is a Mac" in result.output
    assert "Created schedule." not in result.output


def test_run_now_of_a_schedule_whose_folder_is_missing_prints_the_gateways_plain_ending():
    # What POST /cron/jobs/{id}/run answers for the customer's schedule: the Gateway's ending says what was wrong and
    # how to fix it, beside the Director's own reason.
    ending = ("did not start a session: " + _REFUSAL + " Then edit the schedule.")
    fired = {
        "firedUtc": "2026-10-23T18:05:00Z", "machine": "STUDIO_MAC", "infraStatus": "not-started", "taskStatus": "unknown",
        "endingText": ending,
        "resultReason": r"it did not start a session: director returned BadRequest: repoPath does not exist: \\documents\\devthrottle",
    }
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.run_now.return_value = fired
        result = runner.invoke(app, ["schedule", "run", "cj_new"])

    assert result.exit_code == 0, result.output
    assert f"  Ending:  {ending}" in result.output.splitlines()
