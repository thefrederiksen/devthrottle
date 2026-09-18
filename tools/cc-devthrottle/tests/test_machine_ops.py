"""Tests for the machine commands: searching another computer and starting things on it.

The Director relay is stubbed, so these test what the command layer does with an answer rather than
re-testing the relay. The truncation tests are the ones that matter: a search that stops early and reports
itself as though it finished would quietly convince the reader they had seen everything on the machine.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src import machine_ops  # noqa: E402

runner = CliRunner()


@pytest.fixture
def stub_get(monkeypatch):
    """Capture the path the command asks the Director for, and answer with a canned payload."""
    calls = {}

    def fake_get_json(path):
        calls["path"] = path
        return calls["payload"]

    monkeypatch.setattr(machine_ops.gateway, "get_json", fake_get_json)
    return calls


def test_machine_actions_are_discoverable():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    ids = {action["id"] for action in json.loads(result.output)["actions"]}
    assert {"machine-list", "machine-apps", "machine-files", "machine-launch"} <= ids


def test_machine_launch_is_marked_as_changing_state_and_the_searches_are_not():
    """An agent choosing a command needs to know which of these starts a program on someone's computer."""
    result = runner.invoke(app, ["actions", "--json"])
    actions = {a["id"]: a for a in json.loads(result.output)["actions"]}

    assert actions["machine-launch"]["mutatesState"] is True
    assert actions["machine-apps"]["mutatesState"] is False
    assert actions["machine-files"]["mutatesState"] is False
    assert actions["machine-list"]["mutatesState"] is False


def test_apps_lists_what_the_machine_reported(stub_get):
    stub_get["payload"] = {
        "machine": "SOREN_NORTH",
        "apps": [{"name": "Google Chrome", "path": r"C:\Start\Chrome.lnk", "source": "start-menu-user"}],
        "totalMatches": 1,
        "truncated": False,
        "skipped": [],
    }

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH", "chrome"])

    assert result.exit_code == 0
    assert "Google Chrome" in result.output
    assert "machines/SOREN_NORTH/apps" in stub_get["path"]
    assert "q=chrome" in stub_get["path"]


def test_apps_reports_an_incomplete_catalogue_rather_than_a_short_one(stub_get):
    stub_get["payload"] = {
        "apps": [{"name": "Chrome", "path": "p", "source": "s"}],
        "totalMatches": 1,
        "truncated": False,
        "skipped": ["C:/Users/other: access denied"],
    }

    result = runner.invoke(app, ["machine", "apps", "SOREN_NORTH"])

    assert result.exit_code == 0
    assert "incomplete" in result.output


def test_files_shows_the_hits_and_where_it_searched(stub_get, plain):
    stub_get["payload"] = {
        "files": [{"name": "deck.pptx", "path": r"D:\Work\deck.pptx", "sizeBytes": 2048,
                   "modifiedUtc": "2026-07-01T10:00:00Z"}],
        "directoriesVisited": 900,
        "elapsedMilliseconds": 1500,
        "truncated": False,
        "truncationReason": None,
        "unreadableDirectories": 0,
    }

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.pptx"])

    assert result.exit_code == 0
    # Rich styles the results table, so assert on the text rather than the rendering (conftest.py).
    output = plain(result.output)
    assert "deck.pptx" in output
    assert "900 directories" in output
    assert "  deck.pptx,2048,2026-07-01T10:00:00Z,D:\\Work\\deck.pptx" in output.splitlines()


def test_files_stopped_at_the_result_limit_says_so_and_says_what_to_change(stub_get):
    stub_get["payload"] = {
        "files": [{"name": "a.txt", "path": "a", "sizeBytes": 1, "modifiedUtc": "2026-07-01T10:00:00Z"}],
        "truncated": True,
        "truncationReason": "limit",
        "directoriesVisited": 10,
        "elapsedMilliseconds": 20,
        "unreadableDirectories": 0,
    }

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.txt"])

    assert "NOT the whole answer" in result.output
    assert "--count" in result.output


def test_files_stopped_at_the_time_limit_advises_more_time_not_a_narrower_search(stub_get):
    """The two truncation reasons need different advice, which is the whole reason they are distinguished."""
    stub_get["payload"] = {
        "files": [],
        "truncated": True,
        "truncationReason": "timeout",
        "directoriesVisited": 90000,
        "elapsedMilliseconds": 20000,
        "unreadableDirectories": 0,
    }

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.txt"])

    assert "NOT the whole answer" in result.output
    assert "--seconds" in result.output


def test_files_reports_directories_it_could_not_read(stub_get, plain):
    stub_get["payload"] = {
        "files": [], "truncated": False, "truncationReason": None, "directoriesVisited": 5,
        "elapsedMilliseconds": 10, "unreadableDirectories": 42,
    }

    result = runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.txt"])

    assert "42 directories could not be read" in plain(result.output)


def test_files_passes_the_time_limit_through_as_milliseconds(stub_get):
    stub_get["payload"] = {"files": [], "directoriesVisited": 0, "elapsedMilliseconds": 0, "truncated": False,
                           "truncationReason": None, "unreadableDirectories": 0}

    runner.invoke(app, ["machine", "files", "SOREN_NORTH", "*.txt", "--seconds", "45"])

    assert "timeoutMilliseconds=45000" in stub_get["path"]


def test_launch_without_a_name_or_a_path_is_refused_before_anything_is_sent(monkeypatch):
    """Nothing should reach a remote computer when the command did not say what to start."""
    sent = []
    monkeypatch.setattr(machine_ops.gateway, "post_json",
                        lambda *a, **k: sent.append(a) or {})

    result = runner.invoke(app, ["machine", "launch", "SOREN_NORTH"])

    # A usage error: the caller has to change what they typed (docs/axi-standard.md, exit 2).
    assert result.exit_code == 2
    assert sent == []
    assert result.stdout == ""
    assert "--app" in result.stderr and "--path" in result.stderr


def test_launch_by_name_posts_the_application_to_the_right_machine(monkeypatch):
    captured = {}

    def fake_post(path, body, timeout=30):
        captured["path"] = path
        captured["body"] = body
        # The Gateway's RelayResult (MachineEndpoints.cs): the launcher's status and its answer as text.
        return {"machine": "SOREN_NORTH", "verb": "launch", "relayStatus": 200, "payload": "{}"}

    monkeypatch.setattr(machine_ops.gateway, "post_json", fake_post)

    result = runner.invoke(app, ["machine", "launch", "SOREN_NORTH", "--app", "Google Chrome"])

    assert result.exit_code == 0
    assert captured["path"] == "machines/SOREN_NORTH/launch"
    assert captured["body"]["app"] == "Google Chrome"
    assert captured["body"]["path"] is None
    # The Gateway refuses any launch without this explicit confirmation (tenant-boundary hardening,
    # CR-5); the typed command carries it, so the capability keeps working end to end.
    assert captured["body"]["confirmProtected"] is True
