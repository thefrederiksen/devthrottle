"""Tests for `cc-devthrottle errors groups` and `errors link` (issue #3675), held to docs/axi-standard.md.

- `errors groups` scopes and filters exactly as `errors list` does: this account on the session key, every
  account only with the administrator token, and every filter reaches the Gateway in --json too.
- The fingerprint is never cut short in the default output, and every group reads back with parse_list.
- An empty answer says count: 0; an answer with no list of groups is a failure, never "none".
- `errors link` is administrator-only, sends the fingerprint in the body, and refuses a malformed one locally.
- The component list here is the contract's list, read from the C# file it is defined in.
"""

import json
import re
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

CONTRACT = (Path(__file__).resolve().parents[3] / "src" / "CcDirector.Core" / "ErrorReports" / "ErrorReportContract.cs")
FINGERPRINT = "0123456789abcdef"


def _group(fingerprint=FINGERPRINT, count=3, message="WAIT ENDED session=<id> verb=prompt, with a comma"):
    return {
        "fingerprint": fingerprint,
        "component": "director",
        "source": "Session",
        "exception_type": None,
        "count": count,
        "occurrences": count + 1,
        "user_visible": 2,
        "first_seen_utc": "2026-10-07T09:00:00Z",
        "last_seen_utc": "2026-10-08T11:00:00Z",
        "sample_message": message,
        "linked_issue": "#3675",
    }


def _answer(groups, total=None):
    return {
        "scope": "account",
        "since_utc": "2026-10-07T12:00:00Z",
        "until_utc": "2026-10-08T12:00:00Z",
        "total_groups": len(groups) if total is None else total,
        "total_reports": sum(g["count"] for g in groups),
        "returned": len(groups),
        "retention_days": 90,
        "groups": groups,
    }


class _Calls(list):
    state: dict


@pytest.fixture
def calls(monkeypatch):
    seen = _Calls()
    state = {"answer": _answer([_group(), _group("fedcba9876543210", count=1, message="Save FAILED")])}

    def fake_get_json(path, timeout=30, *, bearer=None, base_url=None):
        seen.append({"verb": "GET", "path": path, "bearer": bearer, "base_url": base_url})
        return state["answer"]

    def fake_put_json(path, body, timeout=30, *, bearer=None, base_url=None):
        seen.append({"verb": "PUT", "path": path, "body": body, "bearer": bearer, "base_url": base_url})
        return state["answer"]

    monkeypatch.setattr(errors_ops.gateway, "get_json", fake_get_json)
    monkeypatch.setattr(errors_ops.gateway, "put_json", fake_put_json)
    monkeypatch.delenv("ADMIN_SERVICE_TOKEN", raising=False)
    seen.state = state
    return seen


def test_components_match_the_contract():
    text = CONTRACT.read_text(encoding="utf-8")
    constants = dict(re.findall(r'public const string (\w+) = "([a-z-]+)";', text))
    listed = re.search(r"Components = \[([^\]]+)\];", text).group(1)
    contract = tuple(constants[name.strip()] for name in listed.split(","))

    assert errors_ops.COMPONENTS == contract


def test_plain_groups_reads_this_account_on_the_session_key(calls):
    result = runner.invoke(app, ["errors", "groups"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"].startswith("gateway/director-errors/groups?")
    assert calls[0]["bearer"] is None
    assert "scope: this account" in result.output
    assert "count: 2 of 2 total" in result.output
    assert "reports in these problems: 4" in result.output


def test_every_group_reads_back_with_its_whole_fingerprint(calls):
    result = runner.invoke(app, ["errors", "groups"])

    block = result.output[result.output.index("groups[") :].split("\nhelp[")[0].split("\nnone match")[0]
    fields, rows = parse_list(block, "groups")
    assert fields == ["fingerprint", "count", "visible", "last_seen", "component", "message"]
    assert [r["fingerprint"] for r in rows] == [FINGERPRINT, "fedcba9876543210"]
    assert rows[0]["count"] == "3"
    assert rows[0]["visible"] == "2"
    assert rows[0]["message"] == "WAIT ENDED session=<id> verb=prompt, with a comma"


def test_filters_reach_the_gateway_in_json_too(calls):
    result = runner.invoke(
        app,
        ["errors", "groups", "--json", "--since", "7d", "--machine", "devthrottle-pc", "--version", "2.18",
         "--component", "mobile", "--limit", "5"],
    )

    assert result.exit_code == 0, result.output
    path = calls[0]["path"]
    for part in ("since=7d", "machine=devthrottle-pc", "version=2.18", "component=mobile", "limit=5"):
        assert part in path
    assert json.loads(result.output)["groups"][0]["linked_issue"] == "#3675"


def test_fields_option_selects_the_linked_issue(calls):
    result = runner.invoke(app, ["errors", "groups", "--fields", "fingerprint,issue,occurrences"])

    block = result.output[result.output.index("groups[") :].split("\nhelp[")[0]
    fields, rows = parse_list(block, "groups")
    assert fields == ["fingerprint", "issue", "occurrences"]
    assert rows[0]["issue"] == "#3675"
    assert rows[0]["occurrences"] == "4"


def test_all_accounts_without_the_token_fails_with_the_exact_command(calls):
    result = runner.invoke(app, ["errors", "groups", "--all-accounts"])

    assert result.exit_code == 1
    assert "cc-secrets run admin-service-token -- cc-devthrottle errors groups --all-accounts" in result.output
    assert calls == []


def test_all_accounts_sends_the_admin_token_to_the_admin_route(calls, monkeypatch):
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")

    result = runner.invoke(app, ["errors", "groups", "--account", "acct-1", "--gateway", "https://gw.example"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"].startswith("gateway/admin/director-errors/groups?")
    assert "account=acct-1" in calls[0]["path"]
    assert calls[0]["bearer"] == "admin-secret"
    assert calls[0]["base_url"] == "https://gw.example"
    assert "scope: every account" in result.output


def test_empty_answer_is_definitive(calls):
    calls.state["answer"] = _answer([])

    result = runner.invoke(app, ["errors", "groups"])

    assert result.exit_code == 0
    assert "count: 0" in result.output
    assert "none match" in result.output


def test_answer_without_a_list_is_a_failure_not_none(calls):
    calls.state["answer"] = {"error": "something else"}

    result = runner.invoke(app, ["errors", "groups"])

    assert result.exit_code == 1
    assert "will not read that as none" in result.output


@pytest.mark.parametrize(
    "args",
    [
        ["errors", "groups", "--component", "kitchen"],
        ["errors", "groups", "--component", "install"],
        ["errors", "groups", "--account", "a", "--email", "b"],
        ["errors", "groups", "--limit", "501"],
        ["errors", "groups", "--fields", "nope"],
        ["errors", "groups", "--json", "--fields", "count"],
        ["errors", "groups", "--gateway", "https://elsewhere.example"],
        ["errors", "link", "not-a-fingerprint", "#1"],
        ["errors", "link", "0123456789ABCDEF", "#1"],
        ["errors", "link", FINGERPRINT],
        ["errors", "link", FINGERPRINT, "#1", "--clear"],
    ],
)
def test_usage_errors_exit_2(calls, args):
    result = runner.invoke(app, args)

    assert result.exit_code == 2, result.output
    assert calls == []


def test_link_without_the_token_fails_with_the_exact_command(calls):
    result = runner.invoke(app, ["errors", "link", FINGERPRINT, "#3675"])

    assert result.exit_code == 1
    assert "cc-secrets run admin-service-token -- cc-devthrottle errors link" in result.output
    assert calls == []


def test_link_sends_the_fingerprint_and_issue_on_the_admin_token(calls, monkeypatch):
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")
    calls.state["answer"] = {"fingerprint": FINGERPRINT, "linked_issue": "#3675", "count": 7,
                             "first_seen_utc": "2026-10-01T00:00:00Z", "last_seen_utc": "2026-10-08T00:00:00Z"}

    result = runner.invoke(app, ["errors", "link", FINGERPRINT, "#3675"])

    assert result.exit_code == 0, result.output
    assert calls[0]["verb"] == "PUT"
    assert calls[0]["path"] == "gateway/admin/director-errors/linked-issue"
    assert calls[0]["body"] == {"fingerprint": FINGERPRINT, "linked_issue": "#3675"}
    assert calls[0]["bearer"] == "admin-secret"
    assert "linked_issue: #3675" in result.output


def test_link_clear_sends_null(calls, monkeypatch):
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")
    calls.state["answer"] = {"fingerprint": FINGERPRINT, "linked_issue": None, "count": 1}

    result = runner.invoke(app, ["errors", "link", FINGERPRINT, "--clear"])

    assert result.exit_code == 0, result.output
    assert calls[0]["body"] == {"fingerprint": FINGERPRINT, "linked_issue": None}
    assert "linked_issue: (none)" in result.output


def test_link_an_answer_that_is_not_the_summary_is_a_failure(calls, monkeypatch):
    monkeypatch.setenv("ADMIN_SERVICE_TOKEN", "admin-secret")
    calls.state["answer"] = {"something": "else"}

    result = runner.invoke(app, ["errors", "link", FINGERPRINT, "#3675"])

    assert result.exit_code == 1
    assert "cannot say the link was recorded" in result.output
