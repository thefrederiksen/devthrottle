"""Tests for `cc-devthrottle errors list` (issue #3311), held to docs/axi-standard.md.

- The account read and the administrator read are chosen EXPLICITLY: the plain command never sends the
  administrator token, and --all-accounts never falls back to the session key.
- Every filter reaches the Gateway, in --json too.
- Every record reads back from the default output with the helper's own parse_list.
- An empty answer says count: 0, and an answer with no list of errors is a failure, never "none".
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import errors_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _error(n, component="director", message=None):
    return {
        "received_utc": f"2026-09-22T12:0{n}:00Z",
        "component": component,
        "account": "11111111-1111-1111-1111-111111111111",
        "device": "abcd1234",
        "machine_id": "0123456789abcdef",
        "product_version": "2.9.0",
        "os": "macos",
        "os_version": "Darwin 24.6.0",
        "arch": "arm64",
        "source": "SessionManager",
        "kind": "logged",
        "message": message or f"CreateSession FAILED: error {n}, with a comma",
        "exception_type": "System.IO.IOException",
        "stack": "   at X.Y()",
        "repeat_count": 2,
    }


def _answer(errors, total=None):
    by = {}
    for e in errors:
        by[e["component"]] = by.get(e["component"], 0) + 1
    return {
        "scope": "account",
        "since_utc": "2026-09-21T12:00:00Z",
        "until_utc": "2026-09-22T12:00:00Z",
        "total_matched": len(errors) if total is None else total,
        "returned": len(errors),
        "by_component": by,
        "retention_days": 30,
        "errors": errors,
    }


class _Calls(list):
    state: dict


@pytest.fixture
def calls(monkeypatch):
    seen = _Calls()
    state = {"answer": _answer([_error(1), _error(2, "launcher")])}

    def fake_get_json(path, timeout=30, *, bearer=None, base_url=None):
        seen.append({"path": path, "bearer": bearer, "base_url": base_url})
        return state["answer"]

    monkeypatch.setattr(errors_ops.gateway, "get_json", fake_get_json)
    monkeypatch.delenv("ADMIN_SERVICE_TOKEN", raising=False)
    seen.state = state
    return seen


def test_plain_list_reads_this_account_on_the_session_key(calls):
    result = runner.invoke(app, ["errors", "list"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"].startswith("gateway/director-errors?")
    assert calls[0]["bearer"] is None
    assert "scope: this account" in result.output
    assert "count: 2 of 2 total" in result.output


def test_every_record_reads_back_from_the_default_output(calls):
    result = runner.invoke(app, ["errors", "list"])

    block = result.output[result.output.index("errors[") :].split("\nhelp[")[0].split("\nnone match")[0]
    fields, rows = parse_list(block, "errors")
    assert fields == ["time", "component", "machine", "message"]
    assert [r["message"] for r in rows] == [
        "CreateSession FAILED: error 1, with a comma",
        "CreateSession FAILED: error 2, with a comma",
    ]
    assert [r["component"] for r in rows] == ["director", "launcher"]


def test_filters_reach_the_gateway_in_json_too(calls):
    result = runner.invoke(
        app,
        ["errors", "list", "--json", "--since", "7d", "--machine", "devthrottle-mac-mini", "--version", "2.9",
         "--component", "launcher", "--limit", "5"],
    )

    assert result.exit_code == 0, result.output
    path = calls[0]["path"]
    for part in ("since=7d", "machine=devthrottle-mac-mini", "version=2.9", "component=launcher", "limit=5"):
        assert part in path
    assert json.loads(result.output)["errors"][0]["stack"] == "   at X.Y()"


def test_all_accounts_without_the_token_fails_with_the_exact_command(calls):
    result = runner.invoke(app, ["errors", "list", "--all-accounts"])

    assert result.exit_code == 1
    assert "cc-secrets run admin-service-token -- cc-devthrottle errors list --all-accounts" in result.output
    assert calls == []


def test_all_accounts_sends_the_admin_token_to_the_admin_route(calls, monkeypatch):
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")

    result = runner.invoke(app, ["errors", "list", "--email", "robert@example.com", "--gateway", "https://gw.example"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"].startswith("gateway/admin/director-errors?")
    assert "email=robert%40example.com" in calls[0]["path"]
    assert calls[0]["bearer"] == "admin-secret"
    assert calls[0]["base_url"] == "https://gw.example"
    assert "scope: every account" in result.output


def test_empty_answer_is_definitive(calls):
    calls.state["answer"] = _answer([])

    result = runner.invoke(app, ["errors", "list"])

    assert result.exit_code == 0
    assert "count: 0" in result.output
    assert "none match" in result.output


def test_answer_without_a_list_is_a_failure_not_none(calls):
    calls.state["answer"] = {"error": "something else"}

    result = runner.invoke(app, ["errors", "list"])

    assert result.exit_code == 1
    assert "will not read that as none" in result.output


def test_long_message_is_truncated_with_a_size_hint_unless_full(calls):
    calls.state["answer"] = _answer([_error(1, message="x " * 200)])

    short = runner.invoke(app, ["errors", "list"])
    full = runner.invoke(app, ["errors", "list", "--full"])

    assert "(truncated, 400 chars total - use --full)" in short.output
    assert "truncated" not in full.output


@pytest.mark.parametrize(
    "args",
    [
        ["errors", "list", "--component", "gateway"],
        ["errors", "list", "--component", "install"],
        ["errors", "list", "--account", "a", "--email", "b"],
        ["errors", "list", "--limit", "0"],
        ["errors", "list", "--fields", "nope"],
        ["errors", "list", "--json", "--fields", "time"],
    ],
)
def test_usage_errors_exit_2(calls, args):
    result = runner.invoke(app, args)

    assert result.exit_code == 2, result.output
    assert calls == []
