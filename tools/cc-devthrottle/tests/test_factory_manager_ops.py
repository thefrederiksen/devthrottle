"""Tests for `cc-devthrottle factory manager show | clear` and `cc-devthrottle factory name-check`.

The account has at most one Factory Manager and the Gateway holds which session it is. These tests pin what
the commands send and print: the routes and bodies are written out as literals, never read from the module,
so a changed route turns a test red. No HTTP happens: the Gateway calls are stubbed.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import factory_manager_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

FM_ID = "f1000000-0000-4000-8000-000000000001"
OTHER_ID = "a2000000-0000-4000-8000-000000000002"
FLEET = [
    {"sessionId": FM_ID, "name": "Factory Manager - talk with the owner"},
    {"sessionId": OTHER_ID, "name": "Release - Architect"},
]

FINDINGS = {
    "count": 2,
    "factoriesChecked": 3,
    "ruleText": "A seat is flagged when its id, name or role says CEO, or when its name is not a role word of its factory.",
    "passText": None,
    "findings": [
        {"factory": "warmforward", "factoryTitle": "WarmForward", "seatId": "ceo", "kind": "ceo",
         "text": "WarmForward: the seat 'ceo' (named 'Boss', role 'Boss') says CEO. A factory has a boss, not a CEO."},
        {"factory": "tallyhand", "factoryTitle": "Tallyhand", "seatId": "boss", "kind": "not-a-role-word",
         "text": "Tallyhand: the seat 'boss' is named 'Nora Hale', which is not a role word of this factory (its role is Boss). Name it by its role."},
    ],
}


def _stub(monkeypatch, stored=None, computer=None, folder=None, name_check=None, fail_status=None):
    """A Gateway holding one mark (or none), recording every PUT."""
    state = {"sessionId": stored, "puts": [], "gets": []}
    gw = factory_manager_ops.gateway

    def binding():
        return {"sessionId": state["sessionId"], "computer": computer, "folder": folder}

    def fake_get_json(path, timeout=30):
        state["gets"].append(path)
        if fail_status is not None:
            raise gw.GatewayError("refused", status=fail_status)
        if path == "gateway/factory/name-check":
            return name_check
        assert path == "gateway/factory-manager", path
        return binding()

    def fake_put_json(path, body, timeout=30):
        assert path == "gateway/factory-manager", path
        if fail_status is not None:
            raise gw.GatewayError("refused", status=fail_status)
        state["puts"].append(body)
        state["sessionId"] = body["sessionId"]
        return binding()

    monkeypatch.setattr(gw, "get_json", fake_get_json)
    monkeypatch.setattr(gw, "put_json", fake_put_json)
    monkeypatch.setattr(gw, "get_fleet", lambda: (FLEET, True, None, None))
    return state


def _flat(plain, result):
    return " ".join(plain(result.output).split())


def test_show_WithNoMark_SaysNoneAndThatTheOwnerSetsIt_NeverPointingASessionAtSet(monkeypatch, plain):
    _stub(monkeypatch)

    result = runner.invoke(app, ["factory", "manager", "show"])

    assert result.exit_code == 0
    out = _flat(plain, result)
    assert "factory-manager: none" in out
    assert "computer: (not chosen yet)" in out
    assert "set-by: the owner, with Talk to the Factory Manager on the Factories screen" in out
    assert "factory manager set" not in out


def test_show_WithAMark_PrintsTheFullIdTheNameAndWhereItStarts(monkeypatch, plain):
    _stub(monkeypatch, stored=FM_ID, computer="SOREN_NORTH", folder=r"D:\ReposFred\factories")

    result = runner.invoke(app, ["factory", "manager", "show"])

    assert result.exit_code == 0
    out = plain(result.output)
    assert f"id: {FM_ID}" in out
    assert "name: Factory Manager - talk with the owner" in out
    assert "computer: SOREN_NORTH" in out
    assert r"folder: D:\ReposFred\factories" in out


def test_show_Json_KeepsTheGatewayShape(monkeypatch, plain):
    _stub(monkeypatch, stored=FM_ID, computer="SOREN_NORTH", folder="D:/f")

    result = runner.invoke(app, ["factory", "manager", "show", "--json"])

    assert result.exit_code == 0
    assert json.loads(plain(result.output)) == {"sessionId": FM_ID, "computer": "SOREN_NORTH", "folder": "D:/f"}


def test_set_IsNotACommand_FailsAsUnknownAndSendsNothing(monkeypatch, plain):
    # Only the owner's own device may set the mark, so a command line `set` could never succeed and is not kept.
    for args in (["set"], ["set", "Release - Architect"], ["set", "--json"]):
        state = _stub(monkeypatch)
        monkeypatch.setenv("CC_SESSION_ID", FM_ID)

        result = runner.invoke(app, ["factory", "manager", *args])

        assert result.exit_code == 2, (args, plain(result.output))
        assert state["puts"] == [], args
        assert state["gets"] == [], args
        assert "No such command 'set'" in _flat(plain, result), args


def test_help_ListsShowAndClear_AndNoSet(plain):
    result = runner.invoke(app, ["factory", "manager", "--help"])

    assert result.exit_code == 0
    out = _flat(plain, result)
    assert "show" in out and "clear" in out
    assert "Only the owner sets it" in out
    assert "factory manager set" not in out
    assert " set " not in out.replace("owner sets it", "")


def test_theModule_HasNoWayToSetTheMark():
    assert not hasattr(factory_manager_ops, "set_mark")


def test_clear_SendsANullSessionId(monkeypatch, plain):
    state = _stub(monkeypatch, stored=FM_ID)

    result = runner.invoke(app, ["factory", "manager", "clear"])

    assert result.exit_code == 0
    assert state["puts"] == [{"sessionId": None}]
    assert "factory-manager: none (cleared)" in plain(result.output)


def test_show_TheSwitchOff_SaysSoInWords(monkeypatch, plain):
    _stub(monkeypatch, fail_status=404)

    result = runner.invoke(app, ["factory", "manager", "show"])

    assert result.exit_code == 1
    assert "factory agents are switched off for this account" in _flat(plain, result)


def test_nameCheck_ListsEveryFindingVerbatim_WithTheRule(monkeypatch, plain):
    state = _stub(monkeypatch, name_check=FINDINGS)

    result = runner.invoke(app, ["factory", "name-check"])

    assert result.exit_code == 0
    assert state["gets"] == ["gateway/factory/name-check"]
    out = _flat(plain, result)
    assert "count: 2" in out
    assert "factories-checked: 3" in out
    assert "rule: A seat is flagged when its id, name or role says CEO" in out
    assert "A factory has a boss, not a CEO." in out
    assert "is named 'Nora Hale', which is not a role word of this factory" in out


def test_nameCheck_NothingFlagged_SaysCountZeroAndThePassSentence(monkeypatch, plain):
    _stub(monkeypatch, name_check={"count": 0, "factoriesChecked": 2, "ruleText": "rule", "passText": "Every factory passes.",
                                   "findings": []})

    result = runner.invoke(app, ["factory", "name-check"])

    assert result.exit_code == 0
    out = plain(result.output)
    assert "count: 0" in out
    assert "Every factory passes." in out


def test_nameCheck_AnAnswerWithNoFindingsList_FailsRatherThanPassing(monkeypatch, plain):
    _stub(monkeypatch, name_check={"count": 0})

    result = runner.invoke(app, ["factory", "name-check"])

    assert result.exit_code == 1
    assert "will not report that as a pass" in _flat(plain, result)


def test_nameCheck_AnAnswerWithNoFactoryCount_FailsRatherThanPrintingZero(monkeypatch, plain):
    _stub(monkeypatch, name_check={k: v for k, v in FINDINGS.items() if k != "factoriesChecked"})

    result = runner.invoke(app, ["factory", "name-check"])

    assert result.exit_code == 1
    out = _flat(plain, result)
    assert "does not say how many factories it checked" in out
    assert "factories-checked:" not in out


def test_nameCheck_AnAnswerWithNoRule_FailsRatherThanPrintingAnEmptyRule(monkeypatch, plain):
    for rule in ({}, {"ruleText": None}, {"ruleText": "  "}):
        answer = {k: v for k, v in FINDINGS.items() if k != "ruleText"}
        answer.update(rule)
        _stub(monkeypatch, name_check=answer)

        result = runner.invoke(app, ["factory", "name-check"])

        assert result.exit_code == 1, rule
        out = _flat(plain, result)
        assert "does not state the rule" in out
        assert "rule:" not in out


def test_nameCheck_Json_AnAnswerMissingItsCountOrRule_IsRefusedToo(monkeypatch, plain):
    _stub(monkeypatch, name_check={"count": 0, "findings": []})

    result = runner.invoke(app, ["factory", "name-check", "--json"])

    assert result.exit_code == 1


def test_nameCheck_Json_IsTheGatewaysAnswer(monkeypatch, plain):
    _stub(monkeypatch, name_check=FINDINGS)

    result = runner.invoke(app, ["factory", "name-check", "--json"])

    assert result.exit_code == 0
    assert json.loads(plain(result.output)) == FINDINGS


def test_actions_ListTheFactoryManagerCommandsAndTheNameCheck(plain):
    result = runner.invoke(app, ["actions", "--json"])

    actions = {a["id"]: a for a in json.loads(plain(result.output))["actions"]}
    assert {"factory-manager-show", "factory-manager-clear", "factory-name-check"}.issubset(actions)
    assert actions["factory-name-check"]["mutatesState"] is False
    assert actions["factory-manager-clear"]["mutatesState"] is True
    # No action offers a session something the Gateway always refuses.
    assert "factory-manager-set" not in actions
    assert not [a["id"] for a in actions.values() if "factory manager set" in a["command"]]
