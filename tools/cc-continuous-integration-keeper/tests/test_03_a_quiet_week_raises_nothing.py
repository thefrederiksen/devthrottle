"""A week with nothing wrong in it: every condition MEASURED, and every condition clear.

This is the other half of test_02. A keeper that raised something on a quiet week would be noise
and would be turned off within a fortnight. What this test will not accept is a quiet answer that
comes from having looked at nothing: each condition must come back "clear" with the size of what
it looked at beside it, never "not-measured".
"""

from __future__ import annotations

import io

import cli
import keeper
from conftest import a_job, a_run, some_records, write_budget


def quiet_week():
    runs = []
    # Twelve pull request runs, all well inside the budget, each green.
    for number in range(12):
        runs.append(
            a_run(
                500 + number,
                event="pull_request",
                branch=f"a-change-{number}",
                started=f"2026-09-16T{number:02d}:00:00Z",
                minutes=11.0,
                jobs=[a_job("Build & Test", tests=4000), a_job("Web", tests=600)],
            )
        )
    # Two runs on main at the two ends of the window, with the suite barely moving.
    runs.append(
        a_run(600, event="push", branch="main", started="2026-09-16T12:00:00Z", minutes=12.0,
              jobs=[a_job("Build & Test", tests=4000), a_job("Web", tests=600)])
    )
    runs.append(
        a_run(601, event="push", branch="main", started="2026-09-20T12:00:00Z", minutes=12.5,
              jobs=[a_job("Build & Test", tests=4040), a_job("Web", tests=612)])
    )
    return some_records(runs)


def test_every_condition_is_measured_and_clear(budget):
    report = keeper.judge(quiet_week(), budget)
    assert report.findings == ()
    assert report.unmeasured == ()
    assert [condition.status for condition in report.conditions] == [keeper.CLEAR] * 4


def test_each_condition_says_what_it_looked_at(budget):
    report = keeper.judge(quiet_week(), budget)
    measured = {condition.condition: condition.measured_over for condition in report.conditions}
    assert "12 finished pull request run(s) of CI" in measured[keeper.BUDGET]
    assert "12 of 12 (100 per cent) inside 20.0 minutes" in measured[keeper.BUDGET]
    assert "+1.0% over 4.0 day(s)" in measured[keeper.SUITE_GROWTH]
    assert "14 finished run(s) in scope" in measured[keeper.REPEATED_RED_TEST]
    assert "0 run(s) had a failed job whose log the keeper could not read" in measured[keeper.REPEATED_RED_TEST]
    assert "2 finished successful run(s) on main" in measured[keeper.SKIPPED_JOB]


def test_the_command_ends_with_success_and_says_the_count_is_zero(tmp_path):
    records_file = tmp_path / "quiet.json"
    quiet_week().write(records_file)
    budget_file = write_budget(tmp_path / "budget.json")

    out, err = io.StringIO(), io.StringIO()
    code = cli.main(["check", "--budget", str(budget_file), "--records", str(records_file)], out, err)
    text = out.getvalue()
    assert code == 0, text + err.getvalue()
    # An empty result says so. It is never blank output.
    assert "count: 0 (budget 0, suite-growth 0, repeated-red-test 0, skipped-job 0)" in text
    assert "findings[0]{condition,subject,measured}:" in text
    assert "readings[9]{what,value}:" in text
    assert err.getvalue() == ""
