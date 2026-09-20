"""A frozen record file is evidence, so it is read strictly.

A record file that half-loaded would be judged as though the parts it dropped had never happened,
and a run that vanished reads exactly like a run that never existed.
"""

from __future__ import annotations

import json

import pytest

from conftest import REAL_WEEK, a_job, a_run, some_records
from errors import KeeperError
from records import load_records


def _write(tmp_path, content):
    path = tmp_path / "records.json"
    path.write_text(json.dumps(content, indent=1), encoding="utf-8")
    return path


def _one_run_file(tmp_path, **changes):
    records = some_records([a_run(1, jobs=[a_job()])]).as_json()
    records["runs"][0].update(changes)
    return _write(tmp_path, records)


def test_a_file_from_a_later_keeper_is_refused_rather_than_guessed_at(tmp_path):
    content = some_records([]).as_json()
    content["format"] = "cc-continuous-integration-keeper/run-records/2"
    with pytest.raises(KeeperError, match="this keeper reads"):
        load_records(_write(tmp_path, content))


def test_a_missing_window_is_refused(tmp_path):
    content = some_records([]).as_json()
    del content["window"]
    with pytest.raises(KeeperError, match="is missing window"):
        load_records(_write(tmp_path, content))


def test_a_run_that_concluded_with_no_end_time_is_refused(tmp_path):
    """Its length cannot be read, and a run of unknown length inside the budget's own pool would be
    counted as a run with no length at all."""
    with pytest.raises(KeeperError, match="has no finished_at"):
        load_records(_one_run_file(tmp_path, finished_at=None))


def test_a_job_with_no_word_about_its_log_is_refused(tmp_path):
    """Whether a log was read, not fetched or refused is the difference between "no test was named"
    and "nobody looked", so a record that does not say is not a record."""
    records = some_records([a_run(1, jobs=[a_job()])]).as_json()
    del records["runs"][0]["jobs"][0]["log"]
    with pytest.raises(KeeperError, match="is missing log"):
        load_records(_write(tmp_path, records))


def test_a_log_state_nobody_knows_is_refused(tmp_path):
    records = some_records([a_run(1, jobs=[a_job()])]).as_json()
    records["runs"][0]["jobs"][0]["log"] = "probably-fine"
    with pytest.raises(KeeperError, match="must be one of read, not-fetched, unavailable"):
        load_records(_write(tmp_path, records))


def test_a_time_written_some_other_way_is_refused(tmp_path):
    with pytest.raises(KeeperError, match="not a time of the form"):
        load_records(_one_run_file(tmp_path, started_at="16 September 2026"))


def test_a_file_that_is_not_json_says_so(tmp_path):
    path = tmp_path / "records.json"
    path.write_text("not json at all", encoding="utf-8")
    with pytest.raises(KeeperError, match="is not valid JSON"):
        load_records(path)


def test_a_file_that_is_not_there_says_so(tmp_path):
    with pytest.raises(KeeperError, match="cannot read the records file"):
        load_records(tmp_path / "nowhere.json")


def test_the_frozen_week_passes_all_of_that():
    """The strictness above is worth nothing if the one real file in the repository does not meet
    it, so it is loaded here too."""
    records = load_records(REAL_WEEK)
    assert len(records.runs) == 485
