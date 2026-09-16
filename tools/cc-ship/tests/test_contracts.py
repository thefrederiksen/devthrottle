"""The review.json and verify.json contracts (issue 2935)."""

import copy
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import contracts  # noqa: E402


def _review():
    return copy.deepcopy(contracts.REVIEW_EXAMPLE)


def _scenario(result="pass", live=True, evidence="shot.png", reason=""):
    return {"name": "n", "result": result, "live": live, "evidence": evidence, "reason": reason}


def test_validate_review_ExampleShape_IsValid():
    assert contracts.validate_review(_review()) == []


def test_validate_review_NoFindings_IsValid():
    review = _review()
    review["findings"] = []
    assert contracts.validate_review(review) == []


def test_validate_review_UnknownRisk_Rejected():
    review = _review()
    review["risk_level"] = "severe"
    assert any("risk_level" in p for p in contracts.validate_review(review))


def test_validate_review_FindingsNotAList_Rejected():
    review = _review()
    review["findings"] = "none"
    assert any("findings" in p for p in contracts.validate_review(review))


def test_validate_review_UnknownAction_Rejected():
    review = _review()
    review["findings"][0]["action"] = "maybe"
    assert any("action" in p for p in contracts.validate_review(review))


def test_validate_review_MissingAction_IsValidAndCountsAsAskOwner():
    review = _review()
    del review["findings"][0]["action"]
    assert contracts.validate_review(review) == []
    assert contracts.finding_action(review["findings"][0]) == "ask-owner"


def test_validate_review_DuplicateIds_Rejected():
    review = _review()
    review["findings"].append(copy.deepcopy(review["findings"][0]))
    assert any("used twice" in p for p in contracts.validate_review(review))


def test_validate_review_BooleanLine_Rejected():
    review = _review()
    review["findings"][0]["line"] = True
    assert any("line" in p for p in contracts.validate_review(review))


def test_validate_review_NothingChecked_Rejected():
    review = _review()
    review["checked"] = []
    assert any("checked" in p for p in contracts.validate_review(review))


def test_load_json_NotJson_ReportsProblem(tmp_path):
    path = tmp_path / "review.json"
    path.write_text("not json", encoding="ascii")
    data, problems = contracts.load_json(path)
    assert data is None and problems


def test_load_json_TopLevelList_ReportsProblem(tmp_path):
    path = tmp_path / "review.json"
    path.write_text(json.dumps([1]), encoding="ascii")
    data, problems = contracts.load_json(path)
    assert data is None and "object" in problems[0]


def test_validate_verify_PassWithEvidence_IsValid():
    assert contracts.validate_verify({"verdict": "go", "scenarios": [_scenario()]}) == []


def test_validate_verify_NoScenarios_Rejected():
    assert contracts.validate_verify({"verdict": "go", "scenarios": []})


def test_validate_verify_PassWithoutEvidence_Rejected():
    problems = contracts.validate_verify({"verdict": "go", "scenarios": [_scenario(evidence="")]})
    assert any("needs evidence" in p for p in problems)


def test_validate_verify_UntestedWithoutReason_Rejected():
    scenario = _scenario(result="untested", live=False, evidence="", reason="")
    problems = contracts.validate_verify({"verdict": "go", "scenarios": [scenario]})
    assert any("reason" in p for p in problems)


def test_validate_verify_FailWithGo_Rejected():
    problems = contracts.validate_verify({"verdict": "go", "scenarios": [_scenario(result="fail")]})
    assert any("no-go" in p for p in problems)


def test_validate_verify_NoSurfaceWithLiveScenario_Rejected():
    problems = contracts.validate_verify({"verdict": "no-surface", "scenarios": [_scenario()]})
    assert any("no-surface" in p for p in problems)


def test_validate_verify_NoSurfaceAllUntested_IsValid():
    scenario = _scenario(result="untested", live=False, evidence="", reason="documents only")
    assert contracts.validate_verify({"verdict": "no-surface", "scenarios": [scenario]}) == []


def test_validate_verify_LiveNotBoolean_Rejected():
    problems = contracts.validate_verify({"verdict": "go", "scenarios": [_scenario(live="yes")]})
    assert any("live" in p for p in problems)
