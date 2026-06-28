"""Tests for cc-devthrottle action discovery."""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()


def test_actions_json_lists_all_implemented_setup_settings_schedule_commands():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    payload = json.loads(result.output)
    ids = {action["id"] for action in payload["actions"]}

    assert {
        "settings-list",
        "settings-path",
        "schedule-get",
        "schedule-runs",
        "schedule-enable",
        "schedule-disable",
        "schedule-delete",
        "schedule-endpoint",
        "setup-update",
        "setup-repair",
        "setup-doctor",
    }.issubset(ids)


def test_actions_json_keeps_manifest_smoke_action():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    payload = json.loads(result.output)
    ids = {action["id"] for action in payload["actions"]}
    assert "settings-get" in ids
