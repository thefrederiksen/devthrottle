"""The budget file is the one place a person changes what "fast enough" means, so it is read
strictly: a key that is missing, a key nobody knows, a value of the wrong kind or out of range is
an error naming the key. A budget file that half-loaded would measure against a default nobody
chose, and a threshold nobody chose is the same as no threshold at all.
"""

from __future__ import annotations

import json

import pytest

from budget import load_budget
from conftest import DEFAULT_BUDGET, REPOSITORY_BUDGET, write_budget
from errors import KeeperError


def _write(tmp_path, content) -> str:
    path = tmp_path / "budget.json"
    path.write_text(json.dumps(content, indent=1), encoding="utf-8")
    return path


def test_the_budget_file_this_repository_ships_loads(repository_budget):
    assert repository_budget.workflow == "CI"
    assert repository_budget.run_budget.minutes == 20
    assert repository_budget.run_budget.share_within_budget == 0.9
    assert repository_budget.suite_growth.share == 0.2
    assert repository_budget.repeated_red_test.consecutive_runs == 3
    assert repository_budget.skipped_job.branch == "main"
    assert repository_budget.notes, "the shipped file explains its own choices"


def test_the_shipped_file_is_the_one_the_workflow_reads():
    """The keeper is pointed at this path by .github/workflows/continuous-integration-keeper.yml.
    A budget file nothing reads is a preference, not a budget."""
    workflow = (REPOSITORY_BUDGET.parent / "workflows" / "continuous-integration-keeper.yml").read_text(
        encoding="utf-8"
    )
    assert ".github/continuous-integration-budget.json" in workflow


def test_a_missing_block_is_named(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    del content["budget"]
    with pytest.raises(KeeperError, match="is missing: budget"):
        load_budget(_write(tmp_path, content))


def test_a_key_nobody_knows_is_refused_with_the_keys_that_are_known(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["budget"]["minuts"] = 20
    with pytest.raises(KeeperError) as caught:
        load_budget(_write(tmp_path, content))
    assert "minuts" in str(caught.value)
    assert "Known keys: minutes, share_within_budget, minimum_runs" in str(caught.value)


def test_a_value_of_the_wrong_kind_is_refused(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["budget"]["minutes"] = "twenty"
    with pytest.raises(KeeperError, match="budget.minutes must be a number"):
        load_budget(_write(tmp_path, content))


def test_a_value_out_of_range_is_refused(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["budget"]["share_within_budget"] = 1.5
    with pytest.raises(KeeperError, match="between 0.5 and 1.0"):
        load_budget(_write(tmp_path, content))


def test_a_rule_written_for_one_run_in_a_row_is_refused(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["repeated_red_test"]["consecutive_runs"] = 1
    with pytest.raises(KeeperError, match="must be 2 or more"):
        load_budget(_write(tmp_path, content))


def test_every_branch_together_with_a_named_branch_is_refused(tmp_path):
    """"*" means every branch, so listing it beside "main" reads as if "main" narrowed something.
    It does not, so the file is refused rather than quietly taken one way."""
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["repeated_red_test"]["branches"] = ["*", "main"]
    with pytest.raises(KeeperError, match="Write one or the other"):
        load_budget(_write(tmp_path, content))


def test_a_file_from_a_later_keeper_is_refused_rather_than_guessed_at(tmp_path):
    content = json.loads(json.dumps(DEFAULT_BUDGET))
    content["format"] = "cc-continuous-integration-keeper/budget/2"
    with pytest.raises(KeeperError, match="this keeper reads"):
        load_budget(_write(tmp_path, content))


def test_a_file_that_is_not_json_says_so(tmp_path):
    path = tmp_path / "budget.json"
    path.write_text("workflow: CI\n", encoding="utf-8")
    with pytest.raises(KeeperError, match="is not valid JSON"):
        load_budget(path)


def test_a_file_that_is_not_there_says_so(tmp_path):
    with pytest.raises(KeeperError, match="cannot read the budget file"):
        load_budget(tmp_path / "nowhere.json")


def test_notes_are_optional_and_carried_through(tmp_path):
    loaded = load_budget(write_budget(tmp_path / "budget.json", notes=["one reason", "another"]))
    assert loaded.notes == ("one reason", "another")
