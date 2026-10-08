"""Tests for a schedule's factory seat (issue #3650): `schedule create --factory --seat` and `schedule link`."""

import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src.schedule_ops import GatewayError  # noqa: E402

runner = CliRunner()

_BASE_ARGS = [
    "schedule", "create",
    "--name", "Mail Desk - morning",
    "--machine", "SOREN_NORTH",
    "--repo", r"D:\factory",
    "--cron", "0 7 * * *",
    "--tz", "America/Toronto",
    "--seed", "/mail-desk",
]


def _run_create(extra_args):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.create_job.return_value = {
            "id": "cj_abc123", "name": "Mail Desk - morning", "nextRunUtc": "2026-10-09T11:00:00Z",
            "factory": "devthrottle", "seat": "mail-desk",
        }
        result = runner.invoke(app, _BASE_ARGS + extra_args)
        posted = instance.create_job.call_args.args[0] if instance.create_job.call_args else None
    return result, posted


def test_create_with_factory_and_seat_sends_both_and_says_which_seat():
    result, posted = _run_create(["--factory", "devthrottle", "--seat", "mail-desk"])
    assert result.exit_code == 0, result.output
    assert (posted["factory"], posted["seat"]) == ("devthrottle", "mail-desk")
    assert "mail-desk of devthrottle" in result.output


def test_create_without_either_is_a_plain_job_and_sends_neither():
    result, posted = _run_create([])
    assert result.exit_code == 0, result.output
    assert "factory" not in posted and "seat" not in posted


def test_create_with_a_factory_but_no_seat_is_refused_before_anything_is_sent():
    result, posted = _run_create(["--factory", "devthrottle"])
    assert result.exit_code != 0
    assert posted is None
    assert "--seat" in result.output


def test_create_with_a_seat_but_no_factory_is_refused_before_anything_is_sent():
    result, posted = _run_create(["--seat", "mail-desk"])
    assert result.exit_code != 0
    assert posted is None


def test_link_puts_the_factory_and_seat_on_the_stored_schedule():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.get_job.return_value = {"id": "cj_abc123", "name": "Mail Desk - morning", "enabled": True}
        instance.update_job.return_value = {
            "id": "cj_abc123", "name": "Mail Desk - morning", "factory": "devthrottle", "seat": "mail-desk",
        }
        result = runner.invoke(app, ["schedule", "link", "cj_abc123", "--factory", "devthrottle", "--seat", "mail-desk"])
        sent_id, sent = instance.update_job.call_args.args
    assert result.exit_code == 0, result.output
    assert sent_id == "cj_abc123"
    assert (sent["factory"], sent["seat"], sent["enabled"]) == ("devthrottle", "mail-desk", True)
    assert "Linked Mail Desk - morning (cj_abc123) to mail-desk of devthrottle." in result.output


def test_link_shows_the_gateways_refusal():
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.get_job.return_value = {"id": "cj_abc123", "name": "x"}
        instance.update_job.side_effect = GatewayError("'mail-desk' is not a seat of DevThrottle (devthrottle)")
        result = runner.invoke(app, ["schedule", "link", "cj_abc123", "--factory", "devthrottle", "--seat", "mail-desk"])
    assert result.exit_code != 0
    assert "is not a seat of DevThrottle" in result.output
