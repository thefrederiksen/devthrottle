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


# ---------- schedule unlink (Factory Seats mission): take a schedule off its seat ----------

_UNLINKED = {
    "text": "Taken off Mail Desk of DevThrottle. 'Mail Desk - morning' is still switched on and keeps running at its "
            "times, as a plain schedule in no factory.",
    "factory": "devthrottle",
    "seat": "mail-desk",
    "job": {"id": "cj_abc123", "name": "Mail Desk - morning", "enabled": True, "factory": None, "seat": None},
}


def _run_unlink(answer=None, error=None, extra_args=()):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        if error is not None:
            instance.unlink_job.side_effect = error
        else:
            instance.unlink_job.return_value = answer
        result = runner.invoke(app, ["schedule", "unlink", "cj_abc123", *extra_args])
        called_with = instance.unlink_job.call_args.args if instance.unlink_job.call_args else None
        # Taking a schedule off its seat is one route, never a read followed by a whole-definition update: an update
        # with a blank factory means "said nothing" and would keep the link.
        assert not instance.update_job.called and not instance.get_job.called
    return result, called_with


def test_unlink_calls_the_one_route_and_says_what_the_gateway_did():
    result, called_with = _run_unlink(_UNLINKED)
    assert result.exit_code == 0, result.output
    assert called_with == ("cj_abc123",)
    assert "Taken off Mail Desk of DevThrottle." in result.output
    assert "is still switched on and keeps running" in result.output
    assert "(cj_abc123)" in result.output


def test_unlink_of_a_schedule_that_keeps_running_offers_the_command_that_stops_it_first():
    result, _ = _run_unlink(_UNLINKED)
    lines = result.output.splitlines()
    disable = next(i for i, line in enumerate(lines) if "cc-devthrottle schedule disable cj_abc123" in line)
    put_back = next(i for i, line in enumerate(lines)
                    if "cc-devthrottle schedule link cj_abc123 --factory devthrottle --seat mail-desk" in line)
    assert disable < put_back


def test_unlink_of_a_switched_off_schedule_does_not_offer_to_disable_it():
    answer = dict(_UNLINKED, job=dict(_UNLINKED["job"], enabled=False),
                  text="Taken off Mail Desk of DevThrottle. 'Mail Desk - morning' is still switched off, and is now in no factory.")
    result, _ = _run_unlink(answer)
    assert result.exit_code == 0, result.output
    assert "schedule disable" not in result.output
    assert "cc-devthrottle schedule link cj_abc123 --factory devthrottle --seat mail-desk" in result.output


def test_unlink_json_prints_what_the_gateway_answered_and_nothing_else():
    import json as _json
    result, _ = _run_unlink(_UNLINKED, extra_args=["--json"])
    assert result.exit_code == 0, result.output
    assert _json.loads(result.output) == _UNLINKED


def test_unlink_refused_for_a_session_outside_the_factory_shows_the_gateways_sentence_and_fails():
    refusal = ("this schedule belongs to factory 'devthrottle', so only a person or a session of 'devthrottle' "
               "may change or run it")
    result, _ = _run_unlink(error=GatewayError(refusal))
    assert result.exit_code == 1
    assert refusal in result.output
    assert "Taken off" not in result.output


def test_unlink_of_a_schedule_on_no_seat_is_a_failure_not_a_quiet_success():
    result, _ = _run_unlink(error=GatewayError("'Daily job search' is on no seat of any factory, so there is nothing to take off."))
    assert result.exit_code == 1
    assert "there is nothing to take off" in result.output


def test_unlink_answer_that_does_not_say_what_happened_is_never_reported_as_taken_off():
    for answer in ({}, {"job": {"id": "cj_abc123"}}, {"text": "  ", "job": {"id": "cj_abc123"}}, {"text": "Taken off."}):
        result, _ = _run_unlink(answer)
        assert result.exit_code == 1, (answer, result.output)
        assert "will not report that as taken off" in result.output


def test_unlink_needs_the_schedule_id_and_refuses_an_unknown_flag():
    assert runner.invoke(app, ["schedule", "unlink"]).exit_code == 2
    assert runner.invoke(app, ["schedule", "unlink", "cj_abc123", "--factory", "devthrottle"]).exit_code == 2
