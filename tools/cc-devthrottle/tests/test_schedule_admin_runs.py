"""Tests for `cc-devthrottle schedule admin-runs` (issue #4403), held to docs/axi-standard.md.

- It reads the administrator route on ADMIN_SERVICE_TOKEN, and without that token it sends nothing.
- Every filter reaches the Gateway, in --json too.
- Every run reads back from the default output with the helper's own parse_list, reason included.
- An empty answer says count: 0, and an answer with no list of runs is a failure, never "none".
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import schedule_admin_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ACCOUNT = "11111111-1111-1111-1111-111111111111"


def _run(n, *, failed=True, reason=None):
    return {
        "account": ACCOUNT,
        "job_id": "cj_morning",
        "job_name": "Morning report",
        "run_id": f"00000000-0000-0000-0000-00000000000{n}",
        "machine": "workstation-a",
        "director_id": "director-1",
        "scheduled_utc": f"2026-10-23T1{n}:00:00Z",
        "fired_utc": f"2026-10-23T1{n}:00:02Z",
        "failed": failed,
        "status": "not-started" if failed else "started",
        "result": "problem" if failed else "ok",
        "problem": "did-not-run" if failed else None,
        "problem_words": "did not run" if failed else None,
        "reason": reason if reason is not None else ("it did not start a session: no Director, connected" if failed else None),
        "result_utc": f"2026-10-23T1{n}:00:02Z",
        "resolved_utc": None,
        "session_id": None if failed else f"sid-{n}",
    }


def _answer(runs, account=ACCOUNT):
    return {
        "scope": "account" if account else "all-accounts",
        "account": account,
        "since_utc": "2026-10-16T18:00:00Z",
        "until_utc": "2026-10-23T18:00:00Z",
        "outcome": "all",
        "limit": 100,
        "returned": len(runs),
        "runs": runs,
    }


class _Calls(list):
    state: dict


@pytest.fixture
def calls(monkeypatch):
    seen = _Calls()
    state = {"answer": _answer([_run(2), _run(1, failed=False)])}

    def fake_get_json(path, timeout=30, *, bearer=None, base_url=None):
        seen.append({"path": path, "bearer": bearer, "base_url": base_url})
        return state["answer"]

    monkeypatch.setattr(schedule_admin_ops.gateway, "get_json", fake_get_json)
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")
    seen.state = state
    return seen


def test_reads_the_admin_route_on_the_admin_token(calls):
    result = runner.invoke(app, ["schedule", "admin-runs", "--email", "alpha@example.com", "--gateway", "https://gw.example"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"].startswith("gateway/admin/cron-runs?")
    assert "email=alpha%40example.com" in calls[0]["path"]
    assert "outcome=all" in calls[0]["path"]
    assert calls[0]["bearer"] == "admin-secret"
    assert calls[0]["base_url"] == "https://gw.example"
    assert f"scope: account {ACCOUNT}" in result.output
    assert "count: 2" in result.output


def test_without_the_token_nothing_is_sent_and_the_exact_command_is_given(calls, monkeypatch):
    monkeypatch.delenv("ADMIN_SERVICE_TOKEN", raising=False)

    result = runner.invoke(app, ["schedule", "admin-runs", "--email", "alpha@example.com"])

    assert result.exit_code == 1
    assert "cc-secrets run admin-service-token -- cc-devthrottle schedule admin-runs" in result.output
    assert calls == []


def test_every_run_reads_back_from_the_default_output(calls):
    result = runner.invoke(app, ["schedule", "admin-runs"])

    block = result.output[result.output.index("runs[") :].split("\nhelp[")[0].split("\nnone match")[0]
    fields, rows = parse_list(block, "runs")
    assert fields == ["fired", "account", "name", "machine", "result", "problem", "reason"]
    assert [r["result"] for r in rows] == ["problem", "ok"]
    assert rows[0]["reason"] == "it did not start a session: no Director, connected"
    assert rows[0]["account"] == ACCOUNT


def test_filters_reach_the_gateway_in_json_too(calls):
    result = runner.invoke(
        app,
        ["schedule", "admin-runs", "--json", "--account", ACCOUNT, "--job", "cj_morning", "--since", "2d",
         "--until", "1h", "--failed", "--limit", "5"],
    )

    assert result.exit_code == 0, result.output
    path = calls[0]["path"]
    for part in (f"account={ACCOUNT}", "job=cj_morning", "since=2d", "until=1h", "outcome=failed", "limit=5"):
        assert part in path
    assert json.loads(result.output)["runs"][0]["problem_words"] == "did not run"


def test_the_gateways_own_reports_are_one_command_away(calls):
    result = runner.invoke(app, ["schedule", "admin-runs", "--email", "alpha@example.com", "--failed"])

    assert "cc-devthrottle errors list --email alpha@example.com --component gateway --since 7d" in result.output


def test_empty_answer_is_definitive(calls):
    calls.state["answer"] = _answer([])

    result = runner.invoke(app, ["schedule", "admin-runs", "--failed"])

    assert result.exit_code == 0
    assert "count: 0" in result.output
    assert "none match" in result.output


def test_answer_without_a_list_is_a_failure_not_none(calls):
    calls.state["answer"] = {"error": "something else"}

    result = runner.invoke(app, ["schedule", "admin-runs"])

    assert result.exit_code == 1
    assert "will not read that as none" in result.output


def test_long_reason_is_truncated_with_a_size_hint_unless_full(calls):
    calls.state["answer"] = _answer([_run(1, reason="x " * 200)])

    short = runner.invoke(app, ["schedule", "admin-runs"])
    full = runner.invoke(app, ["schedule", "admin-runs", "--full"])

    assert "(truncated, 400 chars total - use --full)" in short.output
    assert "truncated" not in full.output


@pytest.mark.parametrize(
    "args",
    [
        ["schedule", "admin-runs", "--account", "a", "--email", "b"],
        ["schedule", "admin-runs", "--limit", "0"],
        ["schedule", "admin-runs", "--limit", "501"],
        ["schedule", "admin-runs", "--fields", "nope"],
        ["schedule", "admin-runs", "--json", "--fields", "fired"],
        ["schedule", "admin-runs", "--no-such-flag"],
    ],
)
def test_usage_errors_exit_2(calls, args):
    result = runner.invoke(app, args)

    assert result.exit_code == 2, result.output
    assert calls == []
