"""`schedule run` waits for the Gateway's own bound, and giving up is never a failure (Factory Definition, phase 5,
QA finding 9).

A repository seat's start takes longer than the 10 seconds every other schedule call waits. The command line used to
give up at 10 seconds and print an error, while the Gateway went on and the seat started. The Gateway no longer lets a
caller hanging up cancel a run it has decided to make, so this tool waits as long as that run is bounded - the
Gateway's limit for one Director command, plus a margin - and when it does stop waiting it says the run may still be
starting and names the command that shows it.
"""

import re
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import requests
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import schedule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ROUTER = Path(__file__).resolve().parents[3] / "src" / "CcDirector.Gateway" / "Api" / "DirectorCommandRouter.cs"


def _ok(body):
    resp = MagicMock(spec=requests.Response)
    resp.status_code = 200
    resp.content = b"x"
    resp.text = ""
    resp.headers = {"Content-Type": "application/json"}
    resp.json.return_value = body
    return resp


def test_GatewayDirectorCommandSeconds_IsTheGatewaysOwnNumber():
    source = ROUTER.read_text(encoding="utf-8")
    match = re.search(r"DefaultCommandTimeout\s*=\s*TimeSpan\.FromSeconds\((\d+)\)", source)

    assert match is not None, f"DirectorCommandRouter.DefaultCommandTimeout not found in {ROUTER}"
    assert int(match.group(1)) == schedule_ops.GATEWAY_DIRECTOR_COMMAND_SECONDS


def test_RunNowTimeout_IsTheDirectorCommandLimitPlusTheMargin():
    assert schedule_ops.RUN_NOW_TIMEOUT_SECONDS == 45
    assert schedule_ops.RUN_NOW_TIMEOUT_SECONDS == (
        schedule_ops.GATEWAY_DIRECTOR_COMMAND_SECONDS + schedule_ops.RUN_NOW_MARGIN_SECONDS)


def test_run_now_WaitsTheRunNowTimeout_AndEveryOtherCallStillWaitsTen():
    client = schedule_ops.ScheduleClient(base_url="http://127.0.0.1:7878")
    with patch("src.schedule_ops.requests.request", return_value=_ok({"firedUtc": "x"})) as req:
        client.run_now("cj_1")
        assert req.call_args.kwargs["timeout"] == schedule_ops.RUN_NOW_TIMEOUT_SECONDS
    with patch("src.schedule_ops.requests.request", return_value=_ok({"id": "cj_1"})) as req:
        client.get_job("cj_1")
        assert req.call_args.kwargs["timeout"] == schedule_ops.TIMEOUT_SECONDS == 10


def test_schedule_run_GatewayDidNotAnswerInTime_SaysTheRunMayStillBeStartingAndHowToSeeIt(monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:7996")
    monkeypatch.setattr(schedule_ops, "_auth_token", lambda: "key")
    with patch("src.schedule_ops.requests.request", side_effect=requests.exceptions.Timeout()):
        result = runner.invoke(app, ["schedule", "run", "cj_38f92e"])

    assert result.exit_code == 1
    assert ("Not known yet: the Gateway did not answer within 45 seconds, so whether the run started is not known "
            "yet. It may still be starting: the Gateway goes on with it after this command stops waiting. The "
            "schedule's runs show how it went.") in result.stderr
    assert "cc-devthrottle schedule runs cj_38f92e" in result.stderr
    assert "Error:" not in result.stderr
    assert "failed" not in result.stderr.lower()


def test_schedule_run_GatewayNotReachable_IsStillAnError(monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:7996")
    monkeypatch.setattr(schedule_ops, "_auth_token", lambda: "key")
    with patch("src.schedule_ops.requests.request", side_effect=requests.exceptions.ConnectionError()):
        result = runner.invoke(app, ["schedule", "run", "cj_38f92e"])

    assert result.exit_code == 1
    assert "Error: Gateway not reachable at http://127.0.0.1:7996." in result.stderr
