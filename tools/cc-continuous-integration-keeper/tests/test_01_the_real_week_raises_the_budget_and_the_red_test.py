"""THE PROOF THE MISSION ASKED FOR.

Fed this repository's own run records for 16 to 19 September 2026 - frozen in
tests/fixtures/run-records-2026-09-16-to-19.json, collected with the keeper's own collect command
and not edited afterwards - the keeper raises both of the events the mission brief names, by name
and by number:

  - the test that stayed red for the best part of a day,
    CcDirector.Gateway.Tests.FleetManagerRoutesHostTests.The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner;
  - the median wait of 76.5 minutes for a green result on a pull request, which the brief states as
    76 minutes.

The fixture depends on no network and on no date: every time in it is a time in September 2026.

A test that only showed the keeper running and finding nothing would prove nothing, so this file
also pins what the keeper measured over, and test_02 shows each condition firing and not firing
one step below its threshold.
"""

from __future__ import annotations

import statistics

import keeper
from records import load_records

from conftest import REAL_WEEK

RED_TEST = (
    "CcDirector.Gateway.Tests.FleetManagerRoutesHostTests."
    "The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner"
)


def test_the_frozen_week_is_this_repository_s_own_records(real_week):
    assert real_week.repository == "thefrederiksen/devthrottle"
    assert real_week.workflow == "CI"
    assert real_week.default_branch == "main"
    assert real_week.window_from.isoformat() == "2026-09-16T00:00:00+00:00"
    assert real_week.window_to.isoformat() == "2026-09-20T00:00:00+00:00"
    assert len(real_week.runs) == 485
    assert sum(1 for run in real_week.runs if run.finished) == 231
    # The logs are where the failing test names come from. If none had been read, every statement
    # about a red test below would be a statement about an empty instrument.
    assert sum(1 for run in real_week.runs for job in run.jobs if job.log == "read") == 184


def test_the_budget_is_raised_with_the_76_minute_median(real_week, repository_budget):
    result = keeper.check_budget(real_week, repository_budget)
    assert result.status == keeper.RAISED
    assert len(result.findings) == 1

    green = [
        run.minutes
        for run in real_week.runs
        if run.event == "pull_request" and run.finished and run.conclusion == "success"
    ]
    # Computed here from the records, not taken from the keeper: 99 green pull request runs, and
    # the middle one took 76.5 minutes. The brief states this as "a median of 76 minutes".
    assert len(green) == 99
    assert round(statistics.median(green), 1) == 76.5

    measured = result.findings[0].measured
    assert "180 finished pull request run(s)" in measured
    assert "28 of 180 (16 per cent) inside 20.0 minutes" in measured
    assert "median 83.8 minutes" in measured
    assert "90% point 99.7 minutes" in measured
    assert "median for a green result 76.5 minutes over 99 run(s)" in measured
    assert "170 pull request run(s) never finished" in measured
    assert result.findings[0].threshold == (
        "90% of finished pull request runs within 20.0 minutes"
    )


def test_the_eighteen_hour_red_test_is_raised_by_name_and_by_number(real_week, repository_budget):
    result = keeper.check_repeated_red_test(real_week, repository_budget)
    assert result.status == keeper.RAISED

    named = {finding.subject: finding for finding in result.findings}
    assert RED_TEST in named, sorted(named)

    measured = named[RED_TEST].measured
    assert "red on 9 finished run(s) in a row" in measured
    assert "on 18 of 231 finished run(s) in the window" in measured
    assert "first named in run 35401849157 started 2026-09-18 22:30" in measured
    assert "last in run 35459682083 started 2026-09-19 17:57" in measured
    assert "19 hours apart" in measured
    assert "5 run(s) in the same span were green" in measured
    # The keeper names the defect as needing a diagnosis; it never decides what it is.
    assert "defect in the test or a defect in the product" in named[RED_TEST].proposal


def test_the_runs_that_named_that_test_are_really_in_the_records(real_week):
    """The finding above rests on the records saying so, so this reads them straight."""
    runs = [run for run in real_week.runs if RED_TEST in run.failing_tests()]
    assert len(runs) == 18
    runs.sort(key=lambda run: run.started_at)
    assert runs[0].id == 35401849157
    assert runs[-1].id == 35459682083
    hours = (runs[-1].started_at - runs[0].started_at).total_seconds() / 3600
    assert 19 < hours < 20
    # Only one of the eighteen is a run on main: a rule written as "three finished runs in a row on
    # main" would not have caught this, which is why the scope is a setting in the budget file.
    on_main = [run for run in runs if run.branch == "main" and run.event == "push"]
    assert len(on_main) == 1


def test_the_growing_suite_is_raised_with_its_two_readings(real_week, repository_budget):
    result = keeper.check_suite_growth(real_week, repository_budget)
    assert result.status == keeper.RAISED
    subjects = [finding.subject for finding in result.findings]
    assert subjects == ["Build & Test (web)"]
    assert (
        "1579 tests on 2026-09-16 00:42 (run 35041120670) to 2011 on 2026-09-19 18:29 "
        "(run 35461379953), +27.4% over 3.7 day(s)" in result.findings[0].measured
    )
    # The .NET job grew too, by less than the fifth the budget allows, so it is measured and
    # reported and NOT raised.
    assert "Build & Test (.NET)" in result.measured_over
    assert "+17.0%" in result.measured_over


def test_no_job_was_skipped_and_the_report_says_what_that_was_measured_over(real_week, repository_budget):
    result = keeper.check_skipped_job(real_week, repository_budget)
    assert result.status == keeper.CLEAR
    assert result.findings == ()
    # "clear" only means something with the size of the thing it looked at beside it.
    assert "34 finished successful run(s) on main" in result.measured_over
    assert "14 job name(s) were seen running in at least one of them" in result.measured_over


def test_the_whole_report_raises_ten_findings_over_three_conditions(real_week, repository_budget):
    report = keeper.judge(real_week, repository_budget)
    assert report.raised_any
    assert report.unmeasured == ()
    raised = {condition.condition: len(condition.findings) for condition in report.conditions}
    assert raised == {
        keeper.BUDGET: 1,
        keeper.SUITE_GROWTH: 1,
        keeper.REPEATED_RED_TEST: 8,
        keeper.SKIPPED_JOB: 0,
    }
    assert dict(report.inventory)["runs in the window"] == "485"


def test_the_fixture_still_reads_back_exactly(tmp_path, real_week):
    """A frozen record file is evidence, and evidence that cannot be read back unchanged is not
    evidence. Written again from what was loaded, it is the same file."""
    again = tmp_path / "again.json"
    real_week.write(again)
    assert again.read_text(encoding="utf-8") == REAL_WEEK.read_text(encoding="utf-8")
