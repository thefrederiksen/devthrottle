"""Tests for `cc-devthrottle factory name-check`, and that the retired Factory Manager commands are gone.

The route is written out as a literal, never read from the module, so a changed route turns a test red. No HTTP
happens: the Gateway calls are stubbed.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import factory_name_check_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

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


def _stub(monkeypatch, name_check=None, fail_status=None):
    """A Gateway answering the name check, recording every read."""
    state = {"gets": []}
    gw = factory_name_check_ops.gateway

    def fake_get_json(path, timeout=30):
        state["gets"].append(path)
        if fail_status is not None:
            raise gw.GatewayError("refused", status=fail_status)
        assert path == "gateway/factory/name-check", path
        return name_check

    monkeypatch.setattr(gw, "get_json", fake_get_json)
    return state


def _flat(plain, result):
    return " ".join(plain(result.output).split())


def test_factoryManager_IsNoLongerACommand_AndNothingIsSent(monkeypatch, plain):
    state = _stub(monkeypatch)

    for command in ("show", "clear", "set"):
        result = runner.invoke(app, ["factory", "manager", command])
        assert result.exit_code != 0, plain(result.output)

    assert state["gets"] == []


def test_nameCheck_TheSwitchOff_SaysSoInWords(monkeypatch, plain):
    _stub(monkeypatch, fail_status=404)

    result = runner.invoke(app, ["factory", "name-check"])

    assert result.exit_code == 1
    assert "factory agents are switched off" in _flat(plain, result)


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


def test_actions_ListTheNameCheck_AndNoFactoryManagerCommand(plain):
    result = runner.invoke(app, ["actions", "--json"])

    actions = {a["id"]: a for a in json.loads(plain(result.output))["actions"]}
    assert actions["factory-name-check"]["mutatesState"] is False
    assert not [a["id"] for a in actions.values() if "factory manager" in a["command"] or "factory-manager" in a["id"]]
