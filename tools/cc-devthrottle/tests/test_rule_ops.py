"""Tests for cc-devthrottle rules (Session Rules part 2, phase 2: agents manage rules).

Named method_scenario_result, each in Arrange-Act-Assert. The Gateway is replaced at the RuleClient seam, so
these test what the command sends and what it prints, against the AXI rules in docs/axi-standard.md: --json
keeps its shape, every filter applies to --json, unknown flags fail, empty says count: 0, ids are never cut.
"""

import json
import sys
from pathlib import Path
from unittest.mock import patch

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from src import rule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

DRY_ID = "6f1c2a7e-0d3b-4e55-9a51-3c2b8d4e7f10"
LIVE_ID = "8a2d3b4c-1e2f-4a5b-8c6d-7e8f9a0b1c2d"
SESSION_ID = "9a6f1510-57a2-4cde-8571-8bbdbd9938a0"

_DRY = {
    "id": DRY_ID,
    "instruction": "When a session runs echo RULE-PROBE, tell me.",
    "screenDescription": "A shell command that echoes RULE-PROBE.",
    "triggerWords": ["RULE-PROBE"],
    "checks": [],
    "scope": {"agent": None, "repository": "github.com/acme/app", "machine": None, "mission": None},
    "cooldownSeconds": 300,
    "dailyCap": 5,
    "state": "dry_run",
    "kind": "screen",
    "appliesTo": {"kind": "everywhere", "machines": [], "exceptMachines": [], "repo": ""},
    "event": "",
    "tool": "",
    "handler": "",
    "match": [],
    "result": "",
    "promotedBy": "",
    "createdUtc": "2026-10-18T12:00:00Z",
    "updatedUtc": "2026-10-18T12:00:00Z",
}
_LIVE = dict(_DRY, id=LIVE_ID, state="live", promotedBy="session:" + SESSION_ID,
             scope={"agent": None, "repository": None, "machine": None, "mission": None},
             instruction="When I run out of allowance, switch me to Opus, then carry on.")

HOOK_ID = "2b7c9d10-4e5f-4a6b-9c8d-0e1f2a3b4c5d"

#: A hook rule as the Gateway projects it (Session Rules part 2, phase 1): flat, with the screen fields empty.
_HOOK = {
    "id": HOOK_ID,
    "instruction": "Never force push from a session.",
    "screenDescription": "",
    "triggerWords": [],
    "checks": [],
    "scope": {"agent": None, "repository": None, "machine": None, "mission": None},
    "cooldownSeconds": 0,
    "dailyCap": 0,
    "state": "dry_run",
    "kind": "hook",
    "appliesTo": {"kind": "repo_on_machine", "machines": ["devlinux", "laptop"], "exceptMachines": [],
                  "repo": "github.com/acme/app"},
    "event": "before_command",
    "tool": "shell",
    "handler": "match",
    "match": ["git push --force", "git push -f"],
    "result": "deny",
    "promotedBy": "",
    "createdUtc": "2026-10-19T12:00:00Z",
    "updatedUtc": "2026-10-19T12:00:00Z",
}

_CHANGE = {
    "id": "11111111-2222-4333-8444-555555555555",
    "ruleId": DRY_ID,
    "action": "change",
    "changedBy": "session:" + SESSION_ID,
    "sessionId": SESSION_ID,
    "agent": "ClaudeCode",
    "occurredUtc": "2026-10-18T12:05:00Z",
    "before": {"dailyCap": 5, "state": "live"},
    "after": {"dailyCap": 2, "state": "dry_run"},
}


def _client():
    return patch("src.rule_ops.RuleClient")


# ---- list ----------------------------------------------------------------------------------------------


def test_list_default_output_hasCountByStateAndEveryIdInFull():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_DRY, _LIVE]

        # Act
        result = runner.invoke(app, ["rules", "list"])

    # Assert
    assert result.exit_code == 0, result.output
    assert "count: 2 (dry_run 1, live 1)" in result.stdout
    fields, rows = axi_output.parse_list(result.stdout, "rules")
    assert fields == ["id", "state", "kind", "scope", "instruction"]
    assert [r["id"] for r in rows] == [DRY_ID, LIVE_ID]
    assert rows[0]["scope"] == "repository=github.com/acme/app"
    assert rows[1]["scope"] == "all-sessions"
    assert rows[1]["instruction"] == _LIVE["instruction"]


def test_list_stateFilter_appliesToPlainAndJsonAlike():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_DRY, _LIVE]

        # Act
        plain = runner.invoke(app, ["rules", "list", "--state", "live"])
        as_json = runner.invoke(app, ["rules", "list", "--state", "live", "--json"])

    # Assert
    assert plain.exit_code == 0, plain.output
    assert "count: 1 of 2 total (live 1)" in plain.stdout
    assert json.loads(as_json.stdout) == [_LIVE]


def test_list_json_unfiltered_isTheGatewaysRulesAsABareArray():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_DRY, _LIVE]

        # Act
        result = runner.invoke(app, ["rules", "list", "--json"])

    # Assert
    assert result.exit_code == 0
    assert json.loads(result.stdout) == [_DRY, _LIVE]


def test_list_noRules_saysCountZero():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = []

        # Act
        result = runner.invoke(app, ["rules", "list"])

    # Assert
    assert result.exit_code == 0
    assert "count: 0" in result.stdout
    assert "No rules in this account." in result.stdout


def test_list_unknownState_isAUsageErrorNamingTheValidStates():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "list", "--state", "paused"])

    # Assert
    assert result.exit_code == 2
    assert "dry_run, live" in result.output
    client_cls.return_value.list.assert_not_called()


def test_list_fieldsWithJson_isAUsageError():
    # Act
    result = runner.invoke(app, ["rules", "list", "--json", "--fields", "id"])

    # Assert
    assert result.exit_code == 2


def test_list_unknownField_isAUsageErrorNamingTheValidFields():
    # Act
    result = runner.invoke(app, ["rules", "list", "--fields", "id,colour"])

    # Assert
    assert result.exit_code == 2
    assert "daily-cap" in result.output


def test_list_unknownFlag_isAUsageError():
    # Act
    result = runner.invoke(app, ["rules", "list", "--everything"])

    # Assert
    assert result.exit_code == 2
    assert "--everything" in result.output


# ---- create --------------------------------------------------------------------------------------------


def test_create_fromFlags_postsTheBodyWithCooldownInSeconds():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _DRY}

        # Act
        result = runner.invoke(app, [
            "rules", "create", "--instruction", _DRY["instruction"],
            "--screen-description", _DRY["screenDescription"], "--trigger-word", "RULE-PROBE",
            "--scope", "repository=github.com/acme/app", "--cooldown", "5m", "--daily-cap", "5",
        ])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert posted == {
        "instruction": _DRY["instruction"],
        "screenDescription": _DRY["screenDescription"],
        "triggerWords": ["RULE-PROBE"],
        "scope": {"repository": "github.com/acme/app"},
        "cooldownSeconds": 300,
        "dailyCap": 5,
        "checks": [],
    }
    assert f"Created rule {DRY_ID} in dry run" in result.stdout


def test_create_bodyWithFlags_flagsAreLaidOverTheBody():
    # Arrange
    body = json.dumps({"instruction": "from the body", "dailyCap": 9, "kind": "hook"})
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _DRY}

        # Act
        result = runner.invoke(app, ["rules", "create", "--body", body, "--daily-cap", "3"])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert posted["instruction"] == "from the body"
    assert posted["dailyCap"] == 3
    # A field this module has never heard of is carried through, for the Gateway to rule on.
    assert posted["kind"] == "hook"


def test_create_bodyThatIsNotJson_isAUsageErrorAndPostsNothing():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "create", "--body", "{not json"])

    # Assert
    assert result.exit_code == 2
    assert "not valid JSON" in result.output
    client_cls.return_value.create.assert_not_called()


def test_create_refusedByTheGateway_exitsOneWithTheGatewaysReasonVerbatim():
    # Arrange
    reason = "the rule names a check 'run_this_python', and that is not a check this product ships. (HTTP 400)"
    with _client() as client_cls:
        client_cls.return_value.create.side_effect = rule_ops.GatewayError(reason)

        # Act
        result = runner.invoke(app, ["rules", "create", "--instruction", "x", "--checks", '[{"name": "run_this_python"}]'])

    # Assert
    assert result.exit_code == 1
    assert "run_this_python" in result.stderr
    assert "help[" in result.stderr


def test_create_nothingGiven_isAUsageError():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "create"])

    # Assert
    assert result.exit_code == 2
    client_cls.return_value.create.assert_not_called()


# ---- change --------------------------------------------------------------------------------------------


def test_change_sendsOnlyTheFieldsGiven_andSaysALiveRuleIsBackInDryRun():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.change.return_value = {"rule": dict(_LIVE, state="dry_run", dailyCap=2), "wasLive": True}

        # Act
        result = runner.invoke(app, ["rules", "change", LIVE_ID, "--daily-cap", "2"])
        rule_id, sent = client_cls.return_value.change.call_args.args

    # Assert
    assert result.exit_code == 0, result.output
    assert rule_id == LIVE_ID
    assert sent == {"dailyCap": 2}
    assert "back in dry run" in result.stdout


def test_change_anyFieldThroughBody_isSentAsGiven():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.change.return_value = {"rule": _DRY, "wasLive": False}

        # Act
        result = runner.invoke(app, ["rules", "change", DRY_ID, "--body", '{"scope": {"machine": "devlinux"}}'])
        _, sent = client_cls.return_value.change.call_args.args

    # Assert
    assert result.exit_code == 0, result.output
    assert sent == {"scope": {"machine": "devlinux"}}


def test_change_noFields_isAUsageErrorAndSendsNothing():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "change", DRY_ID])

    # Assert
    assert result.exit_code == 2
    client_cls.return_value.change.assert_not_called()


def test_change_ruleThatDoesNotExist_exitsOneWithTheGatewaysWords():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.change.side_effect = rule_ops.GatewayError(
            f"there is no rule with the id {DRY_ID}. (HTTP 404)")

        # Act
        result = runner.invoke(app, ["rules", "change", DRY_ID, "--daily-cap", "2"])

    # Assert
    assert result.exit_code == 1
    assert f"there is no rule with the id {DRY_ID}" in result.stderr


def test_change_byPrefix_resolvesTheOneRuleItStarts():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_DRY, _LIVE]
        client_cls.return_value.change.return_value = {"rule": _DRY, "wasLive": False}

        # Act
        result = runner.invoke(app, ["rules", "change", "6f1c", "--daily-cap", "2"])

    # Assert
    assert result.exit_code == 0, result.output
    assert client_cls.return_value.change.call_args.args[0] == DRY_ID


def test_change_byAPrefixOfNoRule_exitsOneAndSendsNothing():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_DRY, _LIVE]

        # Act
        result = runner.invoke(app, ["rules", "change", "ffff", "--daily-cap", "2"])

    # Assert
    assert result.exit_code == 1
    assert "no rule id starts with 'ffff'" in result.stderr
    client_cls.return_value.change.assert_not_called()


# ---- promote and delete --------------------------------------------------------------------------------


def test_promote_sendsTheAcknowledgementAndSaysWhoPromoted():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.promote.return_value = {"rule": _LIVE}

        # Act
        result = runner.invoke(app, ["rules", "promote", LIVE_ID, "-m", "The dry run is right."])

    # Assert
    assert result.exit_code == 0, result.output
    assert client_cls.return_value.promote.call_args.args == (LIVE_ID, "The dry run is right.")
    assert f"promoted by session:{SESSION_ID}" in result.stdout


def test_promote_withoutAnAcknowledgement_isAUsageError():
    # Act
    result = runner.invoke(app, ["rules", "promote", LIVE_ID])

    # Assert
    assert result.exit_code == 2


def test_delete_withoutYesAndNoTerminal_isAUsageErrorAndDeletesNothing():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "delete", DRY_ID])

    # Assert
    assert result.exit_code == 2
    assert "--yes" in result.output
    client_cls.return_value.delete.assert_not_called()


def test_delete_ruleTheGatewayDidNotHave_exitsOne():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.delete.return_value = {"deleted": False}

        # Act
        result = runner.invoke(app, ["rules", "delete", DRY_ID, "--yes"])

    # Assert
    assert result.exit_code == 1
    assert "nothing was deleted" in result.stderr


def test_delete_withYes_saysTheRecordIsKept():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.delete.return_value = {"deleted": True}

        # Act
        result = runner.invoke(app, ["rules", "delete", DRY_ID, "--yes"])

    # Assert
    assert result.exit_code == 0, result.output
    assert f"Deleted rule {DRY_ID}" in result.stdout


# ---- history -------------------------------------------------------------------------------------------


def test_history_everyFilter_isSentToTheGateway_soJsonIsFilteredToo():
    # Arrange
    answer = {"count": 1, "changes": [_CHANGE]}
    with _client() as client_cls:
        client_cls.return_value.history.return_value = answer

        # Act
        result = runner.invoke(app, [
            "rules", "history", DRY_ID, "--session", SESSION_ID, "--action", "change", "-n", "5", "--json"])

    # Assert
    assert result.exit_code == 0, result.output
    assert client_cls.return_value.history.call_args.args == (DRY_ID, SESSION_ID, "change", 5)
    assert json.loads(result.stdout) == answer


def test_history_default_showsSessionAndAgentInFull():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.history.return_value = {"count": 1, "changes": [_CHANGE]}

        # Act
        result = runner.invoke(app, ["rules", "history", "--fields", "action,rule,session,agent,changed"])

    # Assert
    assert result.exit_code == 0, result.output
    assert "count: 1 (change 1)" in result.stdout
    _, rows = axi_output.parse_list(result.stdout, "changes")
    assert rows == [{"action": "change", "rule": DRY_ID, "session": SESSION_ID, "agent": "ClaudeCode",
                     "changed": "dailyCap;state"}]


def test_history_sessionMe_isThisSessionsId(monkeypatch):
    # Arrange
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
    with _client() as client_cls:
        client_cls.return_value.history.return_value = {"count": 0, "changes": []}

        # Act
        result = runner.invoke(app, ["rules", "history", "--session", "me"])

    # Assert
    assert result.exit_code == 0, result.output
    assert client_cls.return_value.history.call_args.args[1] == SESSION_ID
    assert "count: 0" in result.stdout


def test_history_deletedRuleById_doesNotNeedTheRuleToExist():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.history.return_value = {"count": 0, "changes": []}

        # Act
        result = runner.invoke(app, ["rules", "history", DRY_ID])

    # Assert
    assert result.exit_code == 0, result.output
    client_cls.return_value.list.assert_not_called()
    assert "No changes match." in result.stdout


def test_history_unknownAction_isAUsageError():
    # Act
    result = runner.invoke(app, ["rules", "history", "--action", "rename"])

    # Assert
    assert result.exit_code == 2
    assert "create, change, promote, delete" in result.output


def test_history_countAboveTheGatewaysCeiling_isAUsageErrorAndAsksNothing():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "history", "--count", "501"])

    # Assert
    assert result.exit_code == 2
    assert "1 to 500" in result.output
    client_cls.return_value.history.assert_not_called()


def test_history_countAtTheCeiling_isSent():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.history.return_value = {"count": 0, "changes": []}

        # Act
        result = runner.invoke(app, ["rules", "history", "--count", "500"])

    # Assert
    assert result.exit_code == 0, result.output
    assert client_cls.return_value.history.call_args.args[3] == 500


# ---- hook rules (Session Rules part 2, phase 1 meets phase 2) -------------------------------------------


def test_list_hookRule_saysItsKindAndHasNoSessionScope():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_HOOK, _DRY]

        # Act
        result = runner.invoke(app, ["rules", "list"])

    # Assert
    assert result.exit_code == 0, result.output
    _, rows = axi_output.parse_list(result.stdout, "rules")
    assert rows[0]["kind"] == "hook"
    # A hook rule says where it applies with appliesTo; "all-sessions" would claim a scope it does not have.
    assert rows[0]["scope"] == "-"
    assert rows[1]["kind"] == "screen"


def test_list_appliesToMatchAndResultFields_renderAHookRule():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.list.return_value = [_HOOK]

        # Act
        result = runner.invoke(app, ["rules", "list", "--fields", "id,applies-to,event,tool,handler,match,result"])

    # Assert
    assert result.exit_code == 0, result.output
    _, rows = axi_output.parse_list(result.stdout, "rules")
    assert rows[0]["id"] == HOOK_ID
    assert rows[0]["applies-to"] == "repo_on_machine repo=github.com/acme/app machines=devlinux;laptop"
    assert rows[0]["event"] == "before_command"
    assert rows[0]["tool"] == "shell"
    assert rows[0]["handler"] == "match"
    assert rows[0]["match"] == "git push --force;git push -f"
    assert rows[0]["result"] == "deny"


def test_show_hookRule_showsItsKindWhereItAppliesAndItsTrigger():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.get.return_value = _HOOK
        client_cls.return_value.firings.return_value = []

        # Act
        result = runner.invoke(app, ["rules", "show", HOOK_ID])

    # Assert
    assert result.exit_code == 0, result.output
    out = result.stdout
    assert "kind: hook" in out
    assert "applies-to: repo_on_machine repo=github.com/acme/app machines=devlinux;laptop" in out
    assert "event: before_command" in out
    assert "tool: shell" in out
    assert "handler: match" in out
    assert "match: git push --force; git push -f" in out
    assert "result: deny" in out
    # The screen fields mean nothing for a hook rule, and a change to them is refused - so they are not offered.
    assert "screen-description" not in out
    assert "daily-cap" not in out
    assert f"cc-devthrottle rules change {HOOK_ID} --match" in out


def test_show_screenRule_showsItsKindAndWhereItApplies():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.get.return_value = _DRY
        client_cls.return_value.firings.return_value = []

        # Act
        result = runner.invoke(app, ["rules", "show", DRY_ID])

    # Assert
    assert result.exit_code == 0, result.output
    assert "kind: screen" in result.stdout
    assert "applies-to: everywhere" in result.stdout
    assert "screen-description: " + _DRY["screenDescription"] in result.stdout
    assert "match:" not in result.stdout


def test_create_hookFromFlags_postsTheHookBodyAndNoChecks():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _HOOK}

        # Act
        result = runner.invoke(app, [
            "rules", "create", "--kind", "hook", "--instruction", _HOOK["instruction"],
            "--applies-to", "kind=repo_on_machine,repo=github.com/acme/app,machines=devlinux;laptop",
            "--event", "before_command", "--tool", "shell", "--handler", "match",
            "--match", "git push --force", "--match", "git push -f", "--result", "deny",
        ])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert posted == {
        "kind": "hook",
        "instruction": _HOOK["instruction"],
        "appliesTo": {"kind": "repo_on_machine", "repo": "github.com/acme/app", "machines": ["devlinux", "laptop"]},
        "event": "before_command",
        "tool": "shell",
        "handler": "match",
        "match": ["git push --force", "git push -f"],
        "result": "deny",
    }
    assert f"Created rule {HOOK_ID} in dry run" in result.stdout


def test_create_hookFromBody_isNotGivenEmptyChecks():
    # Arrange
    body = json.dumps({"kind": "hook", "instruction": "x", "appliesTo": {"kind": "everywhere"}})
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _HOOK}

        # Act
        result = runner.invoke(app, ["rules", "create", "--body", body])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert "checks" not in posted


def test_create_appliesToEverywhere_isSaidOutLoud():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _HOOK}

        # Act
        result = runner.invoke(app, ["rules", "create", "--kind", "hook", "--instruction", "x", "--applies-to", "everywhere"])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert posted["appliesTo"] == {"kind": "everywhere"}


def test_create_appliesToExceptMachines_isAList():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.create.return_value = {"rule": _HOOK}

        # Act
        result = runner.invoke(app, [
            "rules", "create", "--kind", "hook", "--instruction", "x", "--applies-to", "kind=machine,exceptMachines=laptop"])
        posted = client_cls.return_value.create.call_args.args[0]

    # Assert
    assert result.exit_code == 0, result.output
    assert posted["appliesTo"] == {"kind": "machine", "exceptMachines": ["laptop"]}


def test_create_appliesToThatIsNotPairs_isAUsageErrorAndPostsNothing():
    # Act
    with _client() as client_cls:
        result = runner.invoke(app, ["rules", "create", "--kind", "hook", "--instruction", "x", "--applies-to", "devlinux"])

    # Assert
    assert result.exit_code == 2
    assert "--applies-to" in result.output
    client_cls.return_value.create.assert_not_called()


def test_change_hookMatch_sendsOnlyTheMatch():
    # Arrange
    with _client() as client_cls:
        client_cls.return_value.change.return_value = {"rule": dict(_HOOK, match=["rm -rf /"]), "wasLive": False}

        # Act
        result = runner.invoke(app, ["rules", "change", HOOK_ID, "--match", "rm -rf /"])
        rule_id, sent = client_cls.return_value.change.call_args.args

    # Assert
    assert result.exit_code == 0, result.output
    assert rule_id == HOOK_ID
    assert sent == {"match": ["rm -rf /"]}


def test_change_kindRefusedByTheGateway_exitsOneWithTheGatewaysReason():
    # Arrange
    reason = ("a rule's kind cannot be changed: this is a hook rule, and a hook rule and a screen rule are "
              "different instructions. Delete this one and create the screen rule instead. (HTTP 400)")
    with _client() as client_cls:
        client_cls.return_value.change.side_effect = rule_ops.GatewayError(reason)

        # Act
        result = runner.invoke(app, ["rules", "change", HOOK_ID, "--body", '{"kind": "screen"}'])

    # Assert
    assert result.exit_code == 1
    assert "kind cannot be changed" in result.stderr
