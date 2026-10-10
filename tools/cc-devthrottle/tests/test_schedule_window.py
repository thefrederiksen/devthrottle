"""Tests for window schedules on the command line (the owner, 2026-10-09): `schedule create --window`, where the
Gateway chooses the minute, with --days, --deadline, --after and --gap; and `schedule list` reading a window row."""

import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

_BASE = [
    "schedule", "create",
    "--name", "Nightly digest",
    "--machine", "SOREN_NORTH",
    "--repo", r"D:\ReposFred\devthrottle",
    "--tz", "America/Toronto",
    "--seed", "write the digest",
]


def _create(extra, answer=None):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.create_job.return_value = answer or {
            "id": "cj_win123",
            "name": "Nightly digest",
            "scheduleKind": "window",
            "cronExpression": "window=00:00-06:30 deadline=07:00 placed=03:40",
            "scheduleText": "window 00:00-06:30, placed 03:40, done by 07:00",
            "nextRunUtc": "2026-10-10T07:40:00Z",
        }
        result = runner.invoke(app, _BASE + extra)
        posted = instance.create_job.call_args.args[0] if instance.create_job.call_args else None
    return result, posted


def test_create_window_posts_the_window_kind_and_its_settings_and_shows_where_it_was_placed():
    result, posted = _create(["--window", "00:00-06:30", "--deadline", "07:00"])

    assert result.exit_code == 0, result.output
    assert posted["scheduleKind"] == "window"
    assert posted["cronExpression"] == "window=00:00-06:30 deadline=07:00"
    assert posted["runAt"] is None
    assert "Schedule:  window 00:00-06:30, placed 03:40, done by 07:00" in result.output
    assert "cc-devthrottle schedule load --machine SOREN_NORTH" in result.output


def test_create_an_ordered_pair_sends_after_and_gap():
    result, posted = _create(["--window", "03:00-06:00", "--after", "cj_first", "--gap", "120", "--days", "1-5"])

    assert result.exit_code == 0, result.output
    assert posted["cronExpression"] == "window=03:00-06:00 days=1-5 after=cj_first gap=120"


def test_window_flags_without_window_are_a_usage_error_before_any_call():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        result = runner.invoke(app, _BASE + ["--cron", "0 2 * * *", "--deadline", "07:00", "--after", "cj_a"])
        assert not client_cls.return_value.create_job.called

    assert result.exit_code == 2
    assert "--deadline, --after only go with --window." in result.output


def test_gap_without_after_is_a_usage_error():
    result, posted = _create(["--window", "00:00-06:30", "--gap", "60"])

    assert result.exit_code == 2
    assert posted is None
    assert "--gap is the time after another schedule, so it needs --after <schedule-id>." in result.output


def test_window_and_cron_together_are_a_usage_error():
    result, posted = _create(["--window", "00:00-06:30", "--cron", "0 2 * * *"])

    assert result.exit_code == 2
    assert posted is None
    assert "specify exactly one of --at" in result.output


def test_list_reads_a_window_row_and_shows_its_settings_in_the_cron_field():
    row = {
        "id": "cj_win123", "name": "Nightly digest", "enabled": True, "scheduleKind": "window",
        "cronExpression": "window=00:00-06:30 placed=03:40", "runAt": None, "timeZoneId": "America/Toronto",
        "target": {"machine": "SOREN_NORTH"},
        "action": {"repoPath": "D:\\repo", "seed": "x", "workListName": None, "autoDismiss": False},
        "notifyOn": "none", "notifyWebhookUrl": None, "preventOverlap": True,
        "nextRunUtc": "2026-10-10T07:40:00Z", "lastFiredUtc": None, "lastStatus": None,
        "createdUtc": "2026-10-09T10:00:00Z",
    }
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.list_jobs.return_value = [row]
        result = runner.invoke(app, ["schedule", "list", "--fields", "id,kind,cron,next-run"])

    assert result.exit_code == 0, result.output
    _, records = parse_list(result.output, "schedules")
    assert records == [{"id": "cj_win123", "kind": "window", "cron": "window=00:00-06:30 placed=03:40",
                        "next-run": "2026-10-10T07:40:00Z"}]
