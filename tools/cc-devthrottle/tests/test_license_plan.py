"""`cc-devthrottle license plan`: will a Claude account last until a time, and what to stop - the Gateway's plan, laid out.

The Gateway's answer is faked; every session and account is invented, never a real fleet's names.
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

PLAN = {
    "asOfUtc": "2026-10-20T12:00:00Z",
    "accountEmail": "account-a@example.com",
    "accountInUse": True,
    "emptyText": None,
    "targets": [
        {"id": "room:account-b@example.com", "label": "account-b@example.com has room again", "atUtc": "2026-10-20T18:00:00Z"},
        {"id": "reset", "label": "this account's own weekly reset", "atUtc": "2026-10-21T12:00:00Z"},
    ],
    "targetId": "room:account-b@example.com",
    "targetAtUtc": "2026-10-20T18:00:00Z",
    "targetText": "until account-b@example.com has room again",
    "weekUsedPercent": 80,
    "leftPercent": 17,
    "hoursToGo": 6,
    "allowedPercentPerHour": 2.83,
    "burnPercentPerHour": 3,
    "burnText": "3% an hour over the last 6 hours, faster than this week's average of 1%.",
    "shortfallPercentPerHour": 0.17,
    "runsOutAtUtc": "2026-10-20T17:40:00Z",
    "lasts": False,
    "verdict": "At 3% an hour it reaches 97% in about 6 hours, 20 minutes too early.",
    "verdictTone": "bad",
    "suggestion": {"sessionIds": ["s1"], "savesPercentPerHour": 1.0,
                   "text": "Stop 1 session (1 waiting, 0 working) to save about 1% an hour - enough to make it last."},
    "choices": [{"topLevelSessionId": "w1", "number": 101, "label": "#101 Lead", "sessionIds": ["w1", "s2"],
                 "rateNowPercentPerHour": 2.0, "working": 1, "waiting": 1}],
    "sessions": [
        {"sessionId": "w1", "number": 101, "name": "Lead", "topLevelSessionId": "w1", "state": "working",
         "stateText": "Working", "rateNowPercentPerHour": 1.5, "suggested": False},
        {"sessionId": "s1", "number": 102, "name": "Developer one", "topLevelSessionId": "s1", "state": "waiting",
         "stateText": "Waiting", "rateNowPercentPerHour": 1.0, "suggested": True},
    ],
    "whatIf": None,
}


def _fake(monkeypatch, answer=PLAN):
    calls = []

    def fake_get_json(path, timeout=30, *, bearer=None, base_url=None):
        calls.append(path)
        return answer

    monkeypatch.setattr(license_fleet_ops.gateway, "get_json", fake_get_json)
    return calls


def test_plan_shows_the_verdict_the_numbers_and_what_to_stop(monkeypatch):
    calls = _fake(monkeypatch)
    result = runner.invoke(app, ["license", "plan"])

    assert result.exit_code == 0, result.output
    assert calls == ["gateway/licenses/plan"]
    assert "target: until account-b@example.com has room again (2026-10-20T18:00:00Z)" in result.output
    assert "left: 17.00% before 97% over 6.0h = 2.83%/h allowed" in result.output
    assert "verdict: At 3% an hour it reaches 97% in about 6 hours, 20 minutes too early." in result.output
    assert "suggestion: Stop 1 session (1 waiting, 0 working)" in result.output
    assert "sessions[2]{number,name,state,rate-now,suggested}" in result.output
    assert "102,Developer one,Waiting,1.00%/h,stop" in result.output
    assert "101,#101 Lead" not in result.output
    assert "#101 Lead,2,1,1,2.00%/h,w1" in result.output
    assert "cc-devthrottle license plan --until reset" in result.output
    assert "cc-devthrottle license plan --stop-group w1" in result.output


def test_plan_passes_the_target_and_the_what_if_to_the_gateway(monkeypatch):
    answer = dict(PLAN, whatIf={"stoppedSessionIds": ["w1", "s2"], "notFound": ["nobody"], "savesPercentPerHour": 2.0,
                                "burnPercentPerHour": 1.0, "runsOutAtUtc": None, "lasts": True,
                                "verdict": "Stopping 2 sessions saves about 2% an hour. At 1% an hour it lasts.",
                                "verdictTone": "good"})
    calls = _fake(monkeypatch, answer)
    result = runner.invoke(app, ["license", "plan", "--until", "reset", "--stop", "nobody", "--stop-group", "w1"])

    assert result.exit_code == 0, result.output
    assert calls == ["gateway/licenses/plan?until=reset&stop=nobody&stopGroup=w1"]
    assert "what-if: Stopping 2 sessions saves about 2% an hour. At 1% an hour it lasts." in result.output
    assert "not found: nobody" in result.output


def test_plan_json_is_the_gateways_answer_unchanged(monkeypatch):
    _fake(monkeypatch)
    result = runner.invoke(app, ["license", "plan", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == PLAN


def test_plan_refuses_an_answer_with_no_verdict(monkeypatch):
    _fake(monkeypatch, {"sessions": []})
    result = runner.invoke(app, ["license", "plan"])

    assert result.exit_code != 0
    assert "has no verdict" in result.output


def test_plan_is_listed_in_actions_as_a_read():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0, result.output
    action = next(a for a in json.loads(result.output)["actions"] if a["id"] == "license-plan")
    assert action["mutatesState"] is False
    assert action["command"].startswith("cc-devthrottle license plan")
