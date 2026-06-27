"""Tests for cc-devthrottle schedule notification flags."""

import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()

_BASE_ARGS = [
    "schedule",
    "create",
    "--name",
    "nightly",
    "--machine",
    "workstation-A",
    "--repo",
    r"D:\repo",
    "--cron",
    "0 0 * * *",
    "--tz",
    "America/Chicago",
    "--seed",
    "/help",
]


def _run_create(extra_args):
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        instance = client_cls.return_value
        instance.create_job.return_value = {
            "id": "cj_abc123",
            "name": "nightly",
            "nextRunUtc": "2026-06-28T05:00:00Z",
        }
        result = runner.invoke(app, _BASE_ARGS + extra_args)
        posted = instance.create_job.call_args.args[0] if instance.create_job.call_args else None
    return result, posted


def test_create_defaults_notify_off():
    result, posted = _run_create([])
    assert result.exit_code == 0
    assert posted is not None
    assert posted["notifyOn"] == "none"
    assert posted["notifyWebhookUrl"] is None


def test_create_notify_always_with_webhook():
    result, posted = _run_create(
        ["--notify-on", "always", "--notify-webhook", "https://example.com/hook"]
    )
    assert result.exit_code == 0
    assert posted["notifyOn"] == "always"
    assert posted["notifyWebhookUrl"] == "https://example.com/hook"


def test_create_notify_failure():
    result, posted = _run_create(["--notify-on", "failure"])
    assert result.exit_code == 0
    assert posted["notifyOn"] == "failure"
    assert posted["notifyWebhookUrl"] is None


def test_create_notify_case_insensitive():
    result, posted = _run_create(["--notify-on", "ALWAYS"])
    assert result.exit_code == 0
    assert posted["notifyOn"] == "always"


def test_create_invalid_notify_on_is_rejected():
    result, posted = _run_create(["--notify-on", "sometimes"])
    assert result.exit_code != 0
    assert posted is None
    assert "notify-on" in result.output


def test_get_renders_notify_policy():
    job = {
        "id": "cj_abc123",
        "name": "nightly",
        "enabled": True,
        "target": {"machine": "workstation-A"},
        "action": {"repoPath": r"D:\repo", "seed": "/help"},
        "scheduleKind": "recurring",
        "cronExpression": "0 0 * * *",
        "timeZoneId": "America/Chicago",
        "notifyOn": "always",
        "notifyWebhookUrl": "https://example.com/hook",
    }
    with patch("src.schedule_ops.ScheduleClient") as client_cls:
        client_cls.return_value.get_job.return_value = job
        result = runner.invoke(app, ["schedule", "get", "cj_abc123"])
    assert result.exit_code == 0
    assert "Notify:" in result.output
    assert "always" in result.output
    assert "https://example.com/hook" in result.output
