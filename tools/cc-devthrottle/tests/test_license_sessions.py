"""`cc-devthrottle license sessions`: what is using a Claude account, read from the Gateway and laid out as given.

The Gateway's answer is faked; every session is invented ("Developer one", "Lead"), never a real fleet's names.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_fleet_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _session(sid, number, name, rate, so_far, top="lead"):
    return {
        "sessionId": sid, "number": number, "name": name, "missionId": None, "missionName": "Build the thing",
        "topLevelSessionId": top, "isLive": True, "state": "working", "stateText": "Working", "machineName": "machine-one",
        "model": "opus", "agentKind": "claude", "rateNowPercentPerHour": rate, "last4HoursPercent": 0.8,
        "soFarPercent": so_far, "runningHours": 2.5, "averagePercentPerHour": 0.4, "dollarsSoFar": 0.46,
        "weightedLastHour": 1, "weightedLast4Hours": 1, "weightedSoFar": 1, "startedAtUtc": "2026-10-20T10:00:00Z",
        "lastSeenAtUtc": "2026-10-20T12:30:00Z",
    }


ANSWER = {
    "asOfUtc": "2026-10-20T12:30:00Z",
    "accountEmail": "account-a@example.com",
    "accountInUse": True,
    "accounts": [
        {"accountEmail": "account-a@example.com", "inUse": True, "label": "account-a@example.com (in use)"},
        {"accountEmail": "account-b@example.com", "inUse": False, "label": "account-b@example.com"},
    ],
    "emptyText": None,
    "calibration": {"isMeasured": True, "percentPerMillionWeighted": 2.0, "fromUtc": None, "toUtc": None,
                    "risePercent": 2, "weighted": 1000000, "text": "Measured over the last 5 hours: ..."},
    "unitText": ["One unit for everything: percent of account-a@example.com's weekly limit."],
    "weekUsedPercent": 58,
    "weekResetsAtUtc": "2026-10-26T18:00:00Z",
    "burnPercentPerHour": 0.5,
    "burnText": "All sessions together are using about 0.5% of the weekly limit an hour, measured over the last hour.",
    "monthlyPriceUsd": 200,
    "dollarsPerPercent": 0.46,
    "sessions": [_session("dev", 101, "Developer one", 0.358, 1.25), _session("lead", 100, "Lead", None, None)],
    "topLevel": [{"id": "lead", "number": 100, "label": "#100 Lead", "sessionIds": ["lead", "dev"], "liveSessionIds": ["lead", "dev"],
                  "rateNowPercentPerHour": 0.358, "last4HoursPercent": 1.6, "soFarPercent": 1.25, "dollarsSoFar": 0.58}],
    "missions": [{"id": None, "number": None, "label": "No Mission", "sessionIds": ["lead", "dev"], "liveSessionIds": ["lead"],
                  "rateNowPercentPerHour": 0.358, "last4HoursPercent": 1.6, "soFarPercent": 1.25, "dollarsSoFar": None}],
}


def _fake(monkeypatch, answer=ANSWER):
    calls = []

    def fake_get_json(path, timeout=30, *, bearer=None, base_url=None):
        calls.append(path)
        return answer

    monkeypatch.setattr(license_fleet_ops.gateway, "get_json", fake_get_json)
    return calls


def test_sessions_lists_each_session_in_percent_of_the_weekly_limit(monkeypatch):
    calls = _fake(monkeypatch)
    result = runner.invoke(app, ["license", "sessions"])

    assert result.exit_code == 0, result.output
    assert calls == ["gateway/licenses/usage"]
    assert "account: account-a@example.com (in use)" in result.output
    assert "week: 58% used" in result.output
    assert "sessions[2]{number,name,state,rate-now,last-4h,so-far,running,average}" in result.output
    assert "101,Developer one,Working,0.36%/h,0.80%,1.25%,2.5h,0.40%/h" in result.output
    # A percent the Gateway could not measure is said so - never printed as a zero.
    assert "100,Lead,Working,not measured,0.80%,not measured,2.5h,0.40%/h" in result.output
    assert "cc-devthrottle license sessions --account account-b@example.com" in result.output


def test_sessions_by_top_level_lists_each_group(monkeypatch):
    _fake(monkeypatch)
    result = runner.invoke(app, ["license", "sessions", "--by", "top", "--fields", "label,sessions,rate-now,dollars"])

    assert result.exit_code == 0, result.output
    assert "top-level[1]{label,sessions,rate-now,dollars}" in result.output
    assert "#100 Lead,2,0.36%/h,$0.58" in result.output


def test_sessions_passes_the_account_and_closed_to_the_gateway(monkeypatch):
    calls = _fake(monkeypatch)
    result = runner.invoke(app, ["license", "sessions", "--account", "account-b@example.com", "--closed"])

    assert result.exit_code == 0, result.output
    assert calls == ["gateway/licenses/usage?account=account-b%40example.com&closed=true"]


def test_sessions_json_is_the_gateways_answer_unchanged(monkeypatch):
    _fake(monkeypatch)
    result = runner.invoke(app, ["license", "sessions", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == ANSWER


def test_sessions_refuses_an_answer_with_no_list_rather_than_reading_it_as_none(monkeypatch):
    _fake(monkeypatch, answer={"accountEmail": "a@example.com"})
    result = runner.invoke(app, ["license", "sessions"])

    assert result.exit_code != 0
    assert "has no list of sessions" in result.output


def test_sessions_refuses_an_unknown_by(monkeypatch):
    _fake(monkeypatch)
    result = runner.invoke(app, ["license", "sessions", "--by", "machine"])

    assert result.exit_code != 0
    assert "unknown --by 'machine'" in result.output


def test_actions_lists_license_sessions_as_a_read():
    result = runner.invoke(app, ["actions", "--json"])

    action = next(a for a in json.loads(result.output)["actions"] if a["id"] == "license-sessions")
    assert action["mutatesState"] is False
    assert action["command"].startswith("cc-devthrottle license sessions")
