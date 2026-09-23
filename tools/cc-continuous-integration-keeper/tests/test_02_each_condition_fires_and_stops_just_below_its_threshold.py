"""Each of the four conditions, shown FIRING, and shown staying quiet one step below its
threshold.

A check that has only ever been run against the state you hope passes has demonstrated nothing, so
every condition here is run against a known-bad input as well as a known-good one, and the pair is
one step apart: nine runs in ten inside the budget against eight, a fifth of growth against a fifth
and one test more, two red runs in a row against three, a job that ran against the same job
skipped.
"""

from __future__ import annotations

import keeper
from conftest import a_job, a_run, some_records, write_budget
from budget import load_budget


def _budget(tmp_path, **changes):
    return load_budget(write_budget(tmp_path / "budget.json", **changes))


# ---------------------------------------------------------------------------------------------
# Condition 1: the budget
# ---------------------------------------------------------------------------------------------


def _pull_request_week(lengths):
    return some_records(
        [
            a_run(1000 + number, event="pull_request", minutes=length,
                  started=f"2026-09-16T{number:02d}:00:00Z")
            for number, length in enumerate(lengths)
        ]
    )


def test_the_budget_holds_when_nine_runs_in_ten_are_inside_it(budget):
    records = _pull_request_week([10.0] * 9 + [95.0])
    result = keeper.check_budget(records, budget)
    assert result.status == keeper.CLEAR
    assert "9 of 10 (90 per cent) inside 20.0 minutes" in result.measured_over


def test_the_budget_is_raised_when_only_eight_in_ten_are_inside_it(budget):
    records = _pull_request_week([10.0] * 8 + [95.0, 96.0])
    result = keeper.check_budget(records, budget)
    assert result.status == keeper.RAISED
    assert "8 of 10 (80 per cent) inside 20.0 minutes" in result.findings[0].measured
    assert "change the budget in budget.json" in result.findings[0].proposal


def test_a_cancelled_run_is_left_out_rather_than_counted_as_a_quick_one(budget):
    """A run cancelled by a newer push was cut short. Counting its length would make the budget
    look better the more runs are thrown away, which is the wrong way round."""
    runs = [
        a_run(2000 + number, event="pull_request", conclusion="cancelled", minutes=0.5,
              started=f"2026-09-16T{number:02d}:00:00Z")
        for number in range(20)
    ]
    runs += [
        a_run(3000 + number, event="pull_request", minutes=95.0,
              started=f"2026-09-17T{number:02d}:00:00Z")
        for number in range(10)
    ]
    result = keeper.check_budget(some_records(runs), budget)
    assert result.status == keeper.RAISED
    assert "0 of 10 (0 per cent) inside 20.0 minutes" in result.findings[0].measured
    assert "20 pull request run(s) never finished and are left out" in result.findings[0].measured


# ---------------------------------------------------------------------------------------------
# Condition 2: a suite that has grown
# ---------------------------------------------------------------------------------------------


def _two_readings(first_count, last_count):
    return some_records(
        [
            a_run(10, event="push", branch="main", started="2026-09-16T00:00:00Z",
                  jobs=[a_job("Build & Test", tests=first_count)]),
            a_run(11, event="push", branch="main", started="2026-09-20T00:00:00Z",
                  jobs=[a_job("Build & Test", tests=last_count)]),
        ]
    )


def test_a_fifth_of_growth_is_allowed(budget):
    result = keeper.check_suite_growth(_two_readings(100, 120), budget)
    assert result.status == keeper.CLEAR
    assert "+20.0% over 4.0 day(s)" in result.measured_over


def test_one_test_more_than_a_fifth_is_raised(budget):
    result = keeper.check_suite_growth(_two_readings(100, 121), budget)
    assert result.status == keeper.RAISED
    assert result.findings[0].subject == "Build & Test"
    assert "100 tests on 2026-09-16 00:00" in result.findings[0].measured
    assert "121 on 2026-09-20 00:00" in result.findings[0].measured
    assert "+21.0% over 4.0 day(s)" in result.findings[0].measured


def test_a_suite_that_shrank_is_measured_and_not_raised(budget):
    result = keeper.check_suite_growth(_two_readings(500, 100), budget)
    assert result.status == keeper.CLEAR
    assert "-80.0%" in result.measured_over


def test_two_readings_too_close_together_are_not_compared(budget):
    records = some_records(
        [
            a_run(10, event="push", branch="main", started="2026-09-16T00:00:00Z",
                  jobs=[a_job("Build & Test", tests=100)]),
            a_run(11, event="push", branch="main", started="2026-09-16T06:00:00Z",
                  jobs=[a_job("Build & Test", tests=500)]),
        ]
    )
    result = keeper.check_suite_growth(records, budget)
    assert result.status == keeper.NOT_MEASURED
    assert "Build & Test" in result.measured_over


# ---------------------------------------------------------------------------------------------
# Condition 3: the same test red on finished runs in a row
# ---------------------------------------------------------------------------------------------


RED = "Some.Suite.Tests.A_case_that_keeps_failing"


def _sequence(pattern):
    """One run per character: 'r' red with RED named, 'g' green, 'o' red with another test."""
    runs = []
    for number, mark in enumerate(pattern):
        hour = f"2026-09-16T{number:02d}:00:00Z"
        if mark == "g":
            runs.append(a_run(100 + number, started=hour, jobs=[a_job(conclusion="success")]))
        else:
            named = RED if mark == "r" else "Some.Suite.Tests.A_different_case"
            runs.append(
                a_run(100 + number, conclusion="failure", started=hour,
                      jobs=[a_job(conclusion="failure", failing=(named,))])
            )
    return some_records(runs)


def test_two_red_runs_in_a_row_are_not_enough(budget):
    result = keeper.check_repeated_red_test(_sequence("grrg"), budget)
    assert result.status == keeper.CLEAR
    assert "4 finished run(s) in scope" in result.measured_over
    assert "2 red, 2 green" in result.measured_over


def test_three_red_runs_in_a_row_are_raised(budget):
    result = keeper.check_repeated_red_test(_sequence("grrrg"), budget)
    assert result.status == keeper.RAISED
    assert result.findings[0].subject == RED
    assert "red on 3 finished run(s) in a row" in result.findings[0].measured
    assert "on 3 of 5 finished run(s) in the window" in result.findings[0].measured


def test_three_red_runs_that_are_not_in_a_row_are_not_raised(budget):
    result = keeper.check_repeated_red_test(_sequence("rgrgr"), budget)
    assert result.status == keeper.CLEAR


def test_another_test_failing_in_between_does_not_break_the_row(budget):
    """The run is still red and this test is still named in it, so the row is unbroken; the other
    test simply has a row of its own, of one."""
    result = keeper.check_repeated_red_test(_sequence("rrorrr"), budget)
    assert result.status == keeper.RAISED
    subjects = [finding.subject for finding in result.findings]
    assert subjects == [RED]


def test_the_scope_can_be_narrowed_to_one_branch(tmp_path):
    """The same records, judged with the rule written as three runs in a row on main: the runs are
    on pull request branches, so there are not enough of them in scope to measure at all."""
    narrow = _budget(
        tmp_path,
        repeated_red_test={"consecutive_runs": 3, "branches": ["main"], "events": ["push"]},
    )
    result = keeper.check_repeated_red_test(_sequence("grrrg"), narrow)
    assert result.status == keeper.NOT_MEASURED
    assert "0 finished run(s) in scope" in result.measured_over


# ---------------------------------------------------------------------------------------------
# Condition 4: a job that should have run was skipped
# ---------------------------------------------------------------------------------------------


def _main_runs(second_run_jobs):
    return some_records(
        [
            a_run(20, event="push", branch="main", started="2026-09-16T00:00:00Z",
                  jobs=[a_job("Build"), a_job("Tests"), a_job("Web")]),
            a_run(21, event="push", branch="main", started="2026-09-17T00:00:00Z",
                  jobs=second_run_jobs),
        ]
    )


def test_every_job_running_everywhere_is_clear(budget):
    records = _main_runs([a_job("Build"), a_job("Tests"), a_job("Web")])
    result = keeper.check_skipped_job(records, budget)
    assert result.status == keeper.CLEAR
    assert "2 finished successful run(s) on main" in result.measured_over
    assert "3 job name(s) were seen running" in result.measured_over


def test_a_successful_run_that_skipped_a_job_is_raised(budget):
    records = _main_runs([a_job("Build"), a_job("Tests", conclusion="skipped"), a_job("Web")])
    result = keeper.check_skipped_job(records, budget)
    assert result.status == keeper.RAISED
    assert result.findings[0].subject == "Tests in run 21"
    assert "concluded success while the job Tests was skipped" in result.findings[0].measured
    assert "counts as successful when GitHub judges a required check" in result.findings[0].proposal


def test_a_job_that_is_not_in_the_run_at_all_is_not_a_skip(budget):
    """A job added or removed from the workflow inside the window is absent from the runs on the
    other side of the change. Absent is not skipped, and calling it one would raise a finding on
    every run before the job existed."""
    records = _main_runs([a_job("Build"), a_job("Web")])
    result = keeper.check_skipped_job(records, budget)
    assert result.status == keeper.CLEAR


def test_a_skip_on_a_run_that_failed_is_left_alone(budget):
    """A job GitHub skipped because the job it needed failed is the failure's shadow, not news of
    its own. The rule is about a run that reported SUCCESS with a job nobody ran."""
    records = some_records(
        [
            a_run(20, event="push", branch="main", started="2026-09-16T00:00:00Z",
                  jobs=[a_job("Build"), a_job("Tests")]),
            a_run(21, event="push", branch="main", started="2026-09-17T00:00:00Z",
                  jobs=[a_job("Build"), a_job("Tests")]),
            a_run(22, event="push", branch="main", conclusion="failure",
                  started="2026-09-18T00:00:00Z",
                  jobs=[a_job("Build", conclusion="failure"), a_job("Tests", conclusion="skipped")]),
        ]
    )
    result = keeper.check_skipped_job(records, budget)
    assert result.status == keeper.CLEAR
