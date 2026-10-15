"""A defined factory's refusal, as the command line shows it (Factory Definition mission, phase 4).

Once a factory has a published definition, the Gateway refuses every route that would define it another way with
HTTP 409 and one sentence. The command line decides nothing: each command that writes one of those routes prints
the Gateway's sentence verbatim and exits non-zero. The fake Gateway here answers at the HTTP call itself - the
`requests` call the schedule and trigger commands make, and the shared client's opener the factory commands use - so
each command's whole error path runs, and no port is opened.

The switch commands (`schedule enable|disable`) are the one write the Gateway still allows on a defined factory, and
only because they re-send the stored schedule with nothing changed but `enabled`; that is proven here too.
"""

import io
import json
import sys
import urllib.error
from pathlib import Path

import pytest
import requests
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

SENTENCE = "This factory is defined by its definition; edit the definition."
REFUSAL = {"error": SENTENCE, "outcome": "DefinedByDefinition"}

_STORED_JOB = {
    "id": "cj_boss",
    "name": "DevThrottle - Boss",
    "enabled": True,
    "scheduleKind": "recurring",
    "cronExpression": "0 8 * * 1-5",
    "runAt": None,
    "timeZoneId": "Europe/Copenhagen",
    "factory": "devthrottle",
    "seat": "boss",
    "target": {"machine": "SOREN_NORTH"},
    "action": {"repoPath": r"D:\factories\devthrottle", "seed": "Morning round.", "workListName": None, "autoDismiss": True},
    "preventOverlap": True,
    "notifyOn": "none",
    "notifyWebhookUrl": None,
    "scheduleText": "Weekdays 08:00",
}


class _Response:
    """What `requests.request` returns, as far as the commands read it."""

    def __init__(self, status, body):
        self.status_code = status
        self._body = body
        self.text = json.dumps(body)
        self.headers = {"Content-Type": "application/json; charset=utf-8"}
        self.content = self.text.encode("utf-8")

    def json(self):
        return self._body


@pytest.fixture
def gateway_env(monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gateway.test")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key")


@pytest.fixture
def requests_gateway(monkeypatch, gateway_env):
    """The Gateway the schedule and trigger commands call: a GET of a schedule answers the stored job, a PUT of it
    that changes only `enabled` is answered as stored with the switch made, and every other write is refused as a
    defined factory's. Every request is recorded."""
    calls = []

    def fake(method, url, json=None, headers=None, timeout=None):
        calls.append((method, url, json))
        path = url.split("gateway.test", 1)[1]
        if method == "GET" and path == "/cron/jobs/cj_boss":
            return _Response(200, dict(_STORED_JOB))
        if method == "PUT" and path == "/cron/jobs/cj_boss" and json is not None:
            switched = dict(_STORED_JOB, enabled=json.get("enabled"))
            if json == switched:
                return _Response(200, switched)
        return _Response(409, REFUSAL)

    monkeypatch.setattr(requests, "request", fake)
    return calls


@pytest.fixture
def opener_gateway(monkeypatch, gateway_env):
    """The Gateway the factory commands call through the shared client: every write is refused."""
    calls = []

    class _Opener:
        def open(self, req, timeout=None):
            calls.append((req.get_method(), req.full_url, json.loads(req.data) if req.data else None))
            raise urllib.error.HTTPError(req.full_url, 409, "Conflict", {}, io.BytesIO(json.dumps(REFUSAL).encode("utf-8")))

    monkeypatch.setattr(gateway, "_OPENER", _Opener())
    return calls


def _assert_refused_verbatim(result):
    assert result.exit_code != 0, result.output
    assert SENTENCE in result.output, result.output


# ---------- schedules ----------


def test_schedule_create_on_a_defined_factory_prints_the_sentence_and_exits_non_zero(requests_gateway):
    result = runner.invoke(app, [
        "schedule", "create", "--name", "Another start", "--machine", "SOREN_NORTH", "--repo", r"D:\factories\devthrottle",
        "--cron", "0 9 * * *", "--tz", "UTC", "--seed", "Go.", "--factory", "devthrottle", "--seat", "boss",
    ])

    _assert_refused_verbatim(result)
    assert [(m, u.split("gateway.test", 1)[1]) for m, u, _ in requests_gateway] == [("POST", "/cron/jobs")]


def test_schedule_link_onto_a_defined_factory_prints_the_sentence_and_exits_non_zero(requests_gateway):
    result = runner.invoke(app, ["schedule", "link", "cj_boss", "--factory", "devthrottle", "--seat", "outreach"])

    _assert_refused_verbatim(result)


def test_schedule_unlink_of_a_defined_factory_prints_the_sentence_and_exits_non_zero(requests_gateway):
    result = runner.invoke(app, ["schedule", "unlink", "cj_boss"])

    _assert_refused_verbatim(result)


def test_schedule_delete_of_a_defined_factory_prints_the_sentence_and_exits_non_zero(requests_gateway):
    result = runner.invoke(app, ["schedule", "delete", "cj_boss"])

    _assert_refused_verbatim(result)
    assert "Deleted" not in result.output


@pytest.mark.parametrize("command, enabled", [("disable", False), ("enable", True)])
def test_schedule_enable_and_disable_resend_the_stored_job_with_only_enabled_changed(requests_gateway, command, enabled):
    result = runner.invoke(app, ["schedule", command, "cj_boss"])

    assert result.exit_code == 0, result.output
    method, _, sent = requests_gateway[-1]
    assert method == "PUT"
    assert sent == dict(_STORED_JOB, enabled=enabled)


# ---------- triggers ----------


def test_trigger_add_on_a_defined_factory_prints_the_sentence_and_exits_non_zero(requests_gateway):
    result = runner.invoke(app, [
        "trigger", "add", "--name", "another-trigger", "--factory", "devthrottle", "--agent", "outreach",
        "--machine", "SOREN_NORTH", "--repo", r"D:\factories\devthrottle", "--check", "python check.py",
        "--every", "5m", "--prompt", "{count} new.",
    ])

    _assert_refused_verbatim(result)


# ---------- the factory ----------


def test_factory_register_of_a_defined_factory_prints_the_sentence_and_exits_non_zero(opener_gateway, tmp_path):
    manifest = tmp_path / "factory.json"
    manifest.write_text(json.dumps({
        "factory": "devthrottle", "title": "DevThrottle", "folder": str(tmp_path), "computer": "SOREN_NORTH",
        "bossSeat": "boss",
        "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"}],
    }), encoding="utf-8")

    result = runner.invoke(app, ["factory", "register", "--manifest", str(manifest)])

    _assert_refused_verbatim(result)
    assert [m for m, _, _ in opener_gateway] == ["PUT"]


def test_factory_purpose_of_a_defined_factory_prints_the_sentence_and_exits_non_zero(opener_gateway):
    result = runner.invoke(app, ["factory", "purpose", "devthrottle", "Builds DevThrottle by hand"])

    _assert_refused_verbatim(result)
