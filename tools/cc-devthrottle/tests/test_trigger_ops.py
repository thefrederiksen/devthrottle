"""Tests for cc-devthrottle trigger (the Website Business Factory mission, product track)."""

import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import trigger_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

_ADD_ARGS = [
    "trigger", "add",
    "--name", "website-new-mail",
    "--factory", "website-factory",
    "--agent", "Front Desk",
    "--machine", "SOREN_NORTH",
    "--repo", r"D:\ReposFred\cc-consult",
    "--check", "cc-website-factory mail-waiting --json",
    "--every", "5m",
    "--prompt", "New mail: {count} threads. Handle them.",
]

_TRIGGER = {
    "id": "7c7f2e1e-0000-4000-8000-000000000001",
    "name": "website-new-mail",
    "factory": "website-factory",
    "factoryAgent": "Front Desk",
    "machine": "SOREN_NORTH",
    "repoPath": r"D:\ReposFred\cc-consult",
    "checkCommand": "cc-website-factory mail-waiting --json",
    "intervalSeconds": 300,
    "prompt": "New mail: {count} threads. Handle them.",
    "paused": False,
    "status": "red",
    "statusText": "check failed: exit code 1",
    "lastOutcome": "failed",
    "lastCheckUtc": "2026-09-21T10:00:00Z",
}


@pytest.mark.parametrize("text,seconds", [("5m", 300), ("90s", 90), ("1h", 3600), ("60", 60), (" 2M ", 120)])
def test_parse_interval_reads_seconds_minutes_and_hours(text, seconds):
    assert trigger_ops.parse_interval(text) == seconds


@pytest.mark.parametrize("text", ["30s", "59", "0m", "5 minutes", "", "m"])
def test_parse_interval_refuses_less_than_a_minute_or_nonsense(text):
    with pytest.raises(ValueError):
        trigger_ops.parse_interval(text)


def test_add_posts_the_whole_definition_with_the_interval_in_seconds():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.create.return_value = dict(_TRIGGER, status="ok", statusText="waiting for the first check")
        result = runner.invoke(app, _ADD_ARGS)
        posted = client_cls.return_value.create.call_args.args[0]

    assert result.exit_code == 0, result.output
    assert posted == {
        "name": "website-new-mail",
        "factory": "website-factory",
        "factoryAgent": "Front Desk",
        "machine": "SOREN_NORTH",
        "repoPath": r"D:\ReposFred\cc-consult",
        "checkCommand": "cc-website-factory mail-waiting --json",
        "intervalSeconds": 300,
        "prompt": "New mail: {count} threads. Handle them.",
        "paused": False,
    }
    assert "Created trigger website-new-mail" in result.stdout


def test_add_with_a_too_short_interval_is_a_usage_error_and_posts_nothing():
    args = list(_ADD_ARGS)
    args[args.index("5m")] = "30s"
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        result = runner.invoke(app, args)
    assert result.exit_code == 2
    client_cls.return_value.create.assert_not_called()


def test_list_prints_the_status_the_gateway_decided_verbatim():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.list.return_value = [_TRIGGER]
        result = runner.invoke(app, ["trigger", "list"])
    assert result.exit_code == 0, result.output
    assert "website-new-mail  [RED - check failed: exit code 1]" in result.stdout


def test_list_json_is_the_gateway_answer():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.list.return_value = [_TRIGGER]
        result = runner.invoke(app, ["trigger", "list", "--json"])
    assert result.exit_code == 0
    assert json.loads(result.stdout) == [_TRIGGER]


def test_pause_and_resume_call_the_gateway_with_the_name():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.set_paused.return_value = dict(_TRIGGER, paused=True)
        paused = runner.invoke(app, ["trigger", "pause", "website-new-mail"])
        resumed = runner.invoke(app, ["trigger", "resume", "website-new-mail"])
        calls = [c.args for c in client_cls.return_value.set_paused.call_args_list]
    assert paused.exit_code == 0 and resumed.exit_code == 0
    assert calls == [("website-new-mail", True), ("website-new-mail", False)]
    assert "Paused trigger website-new-mail" in paused.stdout


def test_runs_prints_every_outcome_with_its_session_and_reason():
    history = {"triggerId": _TRIGGER["id"], "runs": [
        {"checkedUtc": "2026-09-21T10:05:00Z", "outcome": "started", "count": 2, "sessionId": "abc"},
        {"checkedUtc": "2026-09-21T10:00:00Z", "outcome": "failed", "count": None, "reason": "exit code 1"},
        {"checkedUtc": "2026-09-21T09:55:00Z", "outcome": "nothing-to-do", "count": 0},
    ]}
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.runs.return_value = history
        result = runner.invoke(app, ["trigger", "runs", "website-new-mail", "--count", "3"])
        count = client_cls.return_value.runs.call_args.args[1]
    assert result.exit_code == 0, result.output
    assert count == 3
    assert "started  count=2 session abc" in result.stdout
    assert "failed  count=- reason: exit code 1" in result.stdout
    assert "nothing-to-do  count=0" in result.stdout


def test_runs_takes_the_fleets_short_count_flag():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.runs.return_value = {"triggerId": _TRIGGER["id"], "runs": []}
        result = runner.invoke(app, ["trigger", "runs", "website-new-mail", "-n", "7"])
        count = client_cls.return_value.runs.call_args.args[1]
    assert result.exit_code == 0, result.output
    assert count == 7


def test_runs_refuses_the_limit_flag_the_fleet_does_not_use():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        result = runner.invoke(app, ["trigger", "runs", "website-new-mail", "--limit", "3"])
        client_cls.return_value.runs.assert_not_called()
    assert result.exit_code != 0


def test_a_gateway_with_the_switch_off_is_reported_with_the_remedy():
    response = MagicMock(status_code=404, text="")
    response.json.side_effect = ValueError("no json")
    message = trigger_ops._gateway_message(response)
    assert "factoryAgents" in message and "HTTP 404" in message


def test_no_such_trigger_is_reported_in_the_gateways_own_words_without_the_switch_hint():
    response = MagicMock(status_code=404)
    response.json.return_value = {"code": "trigger_not_found", "error": "no trigger 'x' in this account"}
    message = trigger_ops._gateway_message(response)
    assert message == "no trigger 'x' in this account (HTTP 404)"


def test_a_failed_call_exits_1_and_names_a_next_step():
    with patch("src.trigger_ops.TriggerClient") as client_cls:
        client_cls.return_value.get.side_effect = trigger_ops.GatewayError("no trigger 'x' in this account (HTTP 404)")
        result = runner.invoke(app, ["trigger", "show", "x"])
    assert result.exit_code == 1
    assert "no trigger 'x'" in result.stderr
    assert "cc-devthrottle trigger list" in result.stderr
