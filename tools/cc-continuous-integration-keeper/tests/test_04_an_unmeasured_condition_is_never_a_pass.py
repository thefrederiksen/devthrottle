"""Not-measured is not a pass.

A check whose pass condition is an ABSENCE certifies a run that never happened. Every one of these
cases would read as "nothing wrong" if the keeper reported two states instead of three: an empty
window, a window with too few runs, a failed run whose log nobody could read, a repository whose
test runner the keeper does not recognise. Each one must come back not-measured, and the command
must end with an error rather than with success.
"""

from __future__ import annotations

import io

import cli
import keeper
from conftest import a_job, a_run, some_records, write_budget


def test_an_empty_window_measures_nothing_at_all(budget):
    report = keeper.judge(some_records([]), budget)
    assert [condition.status for condition in report.conditions] == [keeper.NOT_MEASURED] * 4
    assert report.findings == ()
    assert not report.raised_any


def test_too_few_pull_request_runs_to_judge_the_budget(budget):
    records = some_records(
        [a_run(1, event="pull_request", minutes=300.0, started=f"2026-09-16T0{n}:00:00Z") for n in range(3)]
    )
    result = keeper.check_budget(records, budget)
    assert result.status == keeper.NOT_MEASURED
    assert "3 finished pull request run(s)" in result.measured_over
    assert "at least 10" in result.measured_over


def test_a_failed_run_whose_log_was_not_read_leaves_the_red_test_rule_unmeasured(budget):
    """The run failed. Whether the test in question was named in it is unknown, and unknown must
    not read as green."""
    records = some_records(
        [
            a_run(1, started="2026-09-16T01:00:00Z", jobs=[a_job(conclusion="success")]),
            a_run(2, started="2026-09-16T02:00:00Z", jobs=[a_job(conclusion="success")]),
            a_run(
                3,
                conclusion="failure",
                started="2026-09-16T03:00:00Z",
                jobs=[a_job(conclusion="failure", log="unavailable")],
            ),
        ]
    )
    result = keeper.check_repeated_red_test(records, budget)
    assert result.status == keeper.NOT_MEASURED
    assert "1 run(s) had a failed job whose log the keeper could not read" in result.measured_over
    assert "unread instrument rather than a clean week" in result.measured_over


def test_a_failed_run_whose_log_named_no_test_is_information_not_a_broken_instrument(budget):
    """A job can fail before any test runs - a restore, a build, an installation. The log was read
    and named nothing, which is an answer."""
    records = some_records(
        [
            a_run(1, started="2026-09-16T01:00:00Z", jobs=[a_job(conclusion="success")]),
            a_run(2, started="2026-09-16T02:00:00Z", jobs=[a_job(conclusion="success")]),
            a_run(3, conclusion="failure", started="2026-09-16T03:00:00Z",
                  jobs=[a_job(conclusion="failure", log="read")]),
        ]
    )
    result = keeper.check_repeated_red_test(records, budget)
    assert result.status == keeper.CLEAR
    assert "1 red, 2 green" in result.measured_over


def test_a_runner_the_keeper_cannot_read_leaves_growth_unmeasured(budget):
    """No job reported a test count, so nothing can be said about growth. Silence here would be a
    repository whose suite doubled while the keeper reported a clean week."""
    records = some_records(
        [
            a_run(1, event="push", branch="main", started="2026-09-16T00:00:00Z", jobs=[a_job("Build")]),
            a_run(2, event="push", branch="main", started="2026-09-20T00:00:00Z", jobs=[a_job("Build")]),
        ]
    )
    result = keeper.check_suite_growth(records, budget)
    assert result.status == keeper.NOT_MEASURED
    assert "0 job name(s) had any reading at all" in result.measured_over


def test_one_run_on_main_cannot_say_what_another_should_have_done(budget):
    records = some_records(
        [a_run(1, event="push", branch="main", started="2026-09-16T00:00:00Z", jobs=[a_job("Build")])]
    )
    result = keeper.check_skipped_job(records, budget)
    assert result.status == keeper.NOT_MEASURED
    assert "1 finished successful run(s) on main" in result.measured_over


def test_the_command_ends_with_an_error_when_it_measured_nothing(tmp_path):
    records_file = tmp_path / "empty.json"
    some_records([]).write(records_file)
    budget_file = write_budget(tmp_path / "budget.json")

    out, err = io.StringIO(), io.StringIO()
    code = cli.main(["check", "--budget", str(budget_file), "--records", str(records_file)], out, err)
    assert code == 1, out.getvalue()
    assert "count: 0" in out.getvalue()
    assert "could not measure budget, suite-growth, repeated-red-test, skipped-job" in err.getvalue()
    assert "not a condition it passed" in err.getvalue()


def test_something_raised_outranks_something_unmeasured(tmp_path):
    """Both at once: the report shows both, and the exit code is the one that asks for action."""
    records = some_records(
        [
            a_run(1, conclusion="failure", started="2026-09-16T01:00:00Z",
                  jobs=[a_job(conclusion="failure", failing=("A.Test",))]),
            a_run(2, conclusion="failure", started="2026-09-16T02:00:00Z",
                  jobs=[a_job(conclusion="failure", failing=("A.Test",))]),
            a_run(3, conclusion="failure", started="2026-09-16T03:00:00Z",
                  jobs=[a_job(conclusion="failure", failing=("A.Test",))]),
        ]
    )
    records_file = tmp_path / "mixed.json"
    records.write(records_file)
    budget_file = write_budget(tmp_path / "budget.json")

    out, err = io.StringIO(), io.StringIO()
    code = cli.main(["check", "--budget", str(budget_file), "--records", str(records_file)], out, err)
    text = out.getvalue()
    assert code == 3, text
    assert "repeated-red-test,raised,1" in text
    assert "budget,not-measured,0" in text
