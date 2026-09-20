"""The four conditions, judged over run records.

Each condition answers with one of THREE states, never two:

  raised       - the condition is true and the keeper proposes something.
  clear        - the keeper measured it and it is not true. The report says what it measured over.
  not-measured - the keeper could not measure it. This is never a pass. A window with no pull
                 request runs in it says nothing about how long a pull request waits, and a failed
                 run whose log could not be read says nothing about which test was red.

That third state is the whole difference between a check and a decoration: a check whose pass
condition is an absence certifies a run that never happened (the skill 'checks-that-fail-open').
So the keeper never reports "nothing found" without also reporting what it looked at, and it ends
with an error rather than a pass when it looked at nothing.

The keeper PROPOSES. It never edits a workflow, never reruns anything, never opens or closes
anything. Every finding carries the proposal in words, for a person to act on.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from statistics import median

from budget import EVERY, Budget
from records import LOG_READ, LOG_UNAVAILABLE, RunRecord, RunRecords

RAISED = "raised"
CLEAR = "clear"
NOT_MEASURED = "not-measured"

BUDGET = "budget"
SUITE_GROWTH = "suite-growth"
REPEATED_RED_TEST = "repeated-red-test"
SKIPPED_JOB = "skipped-job"

CONDITIONS = (BUDGET, SUITE_GROWTH, REPEATED_RED_TEST, SKIPPED_JOB)


@dataclass(frozen=True)
class Finding:
    """One thing the keeper raises. `measured` carries the numbers, so the record of a finding can
    be read by someone who was not there."""

    condition: str
    subject: str
    measured: str
    threshold: str
    proposal: str


@dataclass(frozen=True)
class ConditionResult:
    condition: str
    status: str
    measured_over: str
    findings: tuple[Finding, ...] = ()

    @property
    def raised(self) -> bool:
        return self.status == RAISED


@dataclass(frozen=True)
class Report:
    records: RunRecords
    budget: Budget
    conditions: tuple[ConditionResult, ...]
    inventory: tuple[tuple[str, str], ...]

    @property
    def findings(self) -> tuple[Finding, ...]:
        return tuple(finding for condition in self.conditions for finding in condition.findings)

    @property
    def raised_any(self) -> bool:
        return any(condition.raised for condition in self.conditions)

    @property
    def unmeasured(self) -> tuple[ConditionResult, ...]:
        return tuple(condition for condition in self.conditions if condition.status == NOT_MEASURED)

    def condition(self, name: str) -> ConditionResult:
        for result in self.conditions:
            if result.condition == name:
                return result
        raise KeyError(name)


def _matches(values: tuple[str, ...], value: str) -> bool:
    return EVERY in values or value in values


def _percentile(sorted_values: list[float], share: float) -> float:
    """The nearest-rank percentile: the smallest value that at least `share` of the values are at
    or below. With one value it is that value."""
    if not sorted_values:
        raise ValueError("no values")
    rank = max(1, min(len(sorted_values), int(-(-share * len(sorted_values) // 1))))
    return sorted_values[rank - 1]


def _minutes(value: float) -> str:
    return f"{value:.1f} minutes"


def _share(part: int, whole: int) -> str:
    return f"{part} of {whole} ({(100.0 * part / whole):.0f} per cent)" if whole else f"{part} of 0"


# ---------------------------------------------------------------------------------------------
# Condition 1: the budget
# ---------------------------------------------------------------------------------------------


def check_budget(records: RunRecords, budget: Budget) -> ConditionResult:
    pull_request_runs = [
        run for run in records.runs if run.event == "pull_request" and run.finished
    ]
    unfinished = [run for run in records.runs if run.event == "pull_request" and not run.finished]
    if len(pull_request_runs) < budget.run_budget.minimum_runs:
        return ConditionResult(
            BUDGET,
            NOT_MEASURED,
            f"{len(pull_request_runs)} finished pull request run(s) of {budget.workflow} in the "
            f"window, and the budget needs at least {budget.run_budget.minimum_runs} before it "
            f"means anything ({len(unfinished)} more pull request run(s) never finished, so their "
            "length measures a cancellation rather than a wait)",
        )

    lengths = sorted(run.minutes for run in pull_request_runs)
    within = sum(1 for length in lengths if length <= budget.run_budget.minutes)
    share_within = within / len(lengths)
    green = sorted(
        run.minutes for run in pull_request_runs if run.conclusion == "success"
    )
    green_text = (
        f"; median for a green result {_minutes(median(green))} over {len(green)} run(s)"
        if green
        else "; no green pull request run in the window"
    )
    measured_over = (
        f"{len(lengths)} finished pull request run(s) of {budget.workflow}: "
        f"{_share(within, len(lengths))} inside {_minutes(budget.run_budget.minutes)}; "
        f"median {_minutes(median(lengths))}; "
        f"{budget.run_budget.share_within_budget:.0%} point {_minutes(_percentile(lengths, budget.run_budget.share_within_budget))}"
        f"{green_text}; {len(unfinished)} pull request run(s) never finished and are left out"
    )
    if share_within >= budget.run_budget.share_within_budget:
        return ConditionResult(BUDGET, CLEAR, measured_over)

    return ConditionResult(
        BUDGET,
        RAISED,
        measured_over,
        (
            Finding(
                condition=BUDGET,
                subject=f"{budget.workflow} pull request runs",
                measured=measured_over,
                threshold=(
                    f"{budget.run_budget.share_within_budget:.0%} of finished pull request runs "
                    f"within {_minutes(budget.run_budget.minutes)}"
                ),
                proposal=(
                    "Cut the length of the run, or change the budget in "
                    f"{budget.path.name} if this is the wait the repository accepts. "
                    "The keeper changes neither."
                ),
            ),
        ),
    )


# ---------------------------------------------------------------------------------------------
# Condition 2: a suite that has grown
# ---------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class _Reading:
    when: datetime
    run_id: int
    tests: int


def check_suite_growth(records: RunRecords, budget: Budget) -> ConditionResult:
    readings: dict[str, list[_Reading]] = {}
    for run in records.runs:
        if not (run.finished and run.conclusion == "success" and run.branch == records.default_branch):
            continue
        for job in run.jobs:
            if job.tests_reported is None:
                continue
            readings.setdefault(job.name, []).append(
                _Reading(when=run.started_at, run_id=run.id, tests=job.tests_reported)
            )

    pairs: list[tuple[str, _Reading, _Reading]] = []
    too_close: list[str] = []
    for name, job_readings in readings.items():
        job_readings.sort(key=lambda reading: reading.when)
        first, last = job_readings[0], job_readings[-1]
        days = (last.when - first.when).total_seconds() / 86400
        if days < budget.suite_growth.minimum_days_between_readings or first.run_id == last.run_id:
            too_close.append(name)
            continue
        pairs.append((name, first, last))

    if not pairs:
        return ConditionResult(
            SUITE_GROWTH,
            NOT_MEASURED,
            f"no job has two readings of its test count at least "
            f"{budget.suite_growth.minimum_days_between_readings} day(s) apart on "
            f"{records.default_branch}: {len(readings)} job name(s) had any reading at all"
            + (f", and these were too close together: {', '.join(sorted(too_close))}" if too_close else "")
            + ". A test count is read out of a job's own log, so a job whose runner the keeper does "
            "not recognise, or whose log it did not fetch, has no reading",
        )

    findings = []
    lines = []
    for name, first, last in sorted(pairs):
        growth = (last.tests - first.tests) / first.tests if first.tests else 0.0
        days = (last.when - first.when).total_seconds() / 86400
        text = (
            f"{name}: {first.tests} tests on {first.when:%Y-%m-%d %H:%M} (run {first.run_id}) to "
            f"{last.tests} on {last.when:%Y-%m-%d %H:%M} (run {last.run_id}), "
            f"{growth:+.1%} over {days:.1f} day(s)"
        )
        lines.append(text)
        if growth > budget.suite_growth.share:
            findings.append(
                Finding(
                    condition=SUITE_GROWTH,
                    subject=name,
                    measured=text,
                    threshold=f"growth of more than {budget.suite_growth.share:.0%} inside the window",
                    proposal=(
                        "Look at what was added and whether it belongs in the run every pull "
                        "request pays for. The keeper proposes only; it removes no test."
                    ),
                )
            )

    if too_close:
        lines.append(
            "not compared, because they have a reading at one end of the window only (a job that "
            "was added, removed or renamed inside the window reads this way): "
            + ", ".join(sorted(too_close))
        )
    measured_over = "; ".join(lines)
    return ConditionResult(
        SUITE_GROWTH, RAISED if findings else CLEAR, measured_over, tuple(findings)
    )


# ---------------------------------------------------------------------------------------------
# Condition 3: the same test red on finished runs in a row
# ---------------------------------------------------------------------------------------------


def _in_scope(run: RunRecord, budget: Budget) -> bool:
    return (
        run.finished
        and _matches(budget.repeated_red_test.branches, run.branch)
        and _matches(budget.repeated_red_test.events, run.event)
    )


def check_repeated_red_test(records: RunRecords, budget: Budget) -> ConditionResult:
    sequence = sorted(
        (run for run in records.runs if _in_scope(run, budget)),
        key=lambda run: run.finished_at or run.started_at,
    )
    unknown = [run for run in sequence if run.unread_failed_jobs()]
    scope = (
        f"branch(es) {', '.join(budget.repeated_red_test.branches)}, "
        f"event(s) {', '.join(budget.repeated_red_test.events)}"
    )

    if len(sequence) < budget.repeated_red_test.consecutive_runs:
        return ConditionResult(
            REPEATED_RED_TEST,
            NOT_MEASURED,
            f"{len(sequence)} finished run(s) in scope ({scope}), fewer than the "
            f"{budget.repeated_red_test.consecutive_runs} in a row the rule is written in",
        )

    streaks: dict[str, int] = {}
    running: dict[str, int] = {}
    for run in sequence:
        red = set(run.failing_tests())
        for name in red:
            running[name] = running.get(name, 0) + 1
            streaks[name] = max(streaks.get(name, 0), running[name])
        for name in list(running):
            if name not in red:
                running[name] = 0

    wanted = budget.repeated_red_test.consecutive_runs
    named = sorted(name for name, longest in streaks.items() if longest >= wanted)

    reds = sum(1 for run in sequence if run.conclusion == "failure")
    measured_over = (
        f"{len(sequence)} finished run(s) in scope ({scope}), in the order they finished: "
        f"{reds} red, {len(sequence) - reds} green; "
        f"{sum(len(run.failing_tests()) for run in sequence)} test name(s) read out of their logs; "
        f"{len(unknown)} run(s) had a failed job whose log the keeper could not read"
    )

    if not named:
        if unknown:
            return ConditionResult(
                REPEATED_RED_TEST,
                NOT_MEASURED,
                measured_over
                + ". No test was named on "
                f"{wanted} finished runs in a row, but a run whose log is unread could have named "
                "one, so this is an unread instrument rather than a clean week",
            )
        return ConditionResult(REPEATED_RED_TEST, CLEAR, measured_over)

    findings = []
    for name in named:
        # The streak above is in the order the runs FINISHED, because that is the order in which a
        # person watching the checks sees a result arrive. The span below is in the order the runs
        # STARTED, because "this test was red from here to here" is about when the code was tested,
        # and a long run that started early can finish after a short one that started later.
        runs_with = sorted(
            (run for run in sequence if name in run.failing_tests()),
            key=lambda run: run.started_at,
        )
        first, last = runs_with[0], runs_with[-1]
        hours = (last.started_at - first.started_at).total_seconds() / 3600
        green_between = sum(
            1
            for run in sequence
            if run.conclusion == "success" and first.started_at <= run.started_at <= last.started_at
        )
        findings.append(
            Finding(
                condition=REPEATED_RED_TEST,
                subject=name,
                measured=(
                    f"red on {streaks[name]} finished run(s) in a row, and on {len(runs_with)} of "
                    f"{len(sequence)} finished run(s) in the window; first named in run {first.id} "
                    f"started {first.started_at:%Y-%m-%d %H:%M} and last in run {last.id} started "
                    f"{last.started_at:%Y-%m-%d %H:%M} Coordinated Universal Time, {hours:.0f} hours "
                    f"apart, while {green_between} run(s) in the same span were green"
                ),
                threshold=f"the same test red on {wanted} finished run(s) in a row ({scope})",
                proposal=(
                    "Diagnose it and name it as a defect in the test or a defect in the product. "
                    "A test that is red on some runs and green on others is one or the other; the "
                    "keeper does not say which, and it changes nothing."
                ),
            )
        )
    return ConditionResult(REPEATED_RED_TEST, RAISED, measured_over, tuple(findings))


# ---------------------------------------------------------------------------------------------
# Condition 4: a job that should have run was skipped
# ---------------------------------------------------------------------------------------------


def check_skipped_job(records: RunRecords, budget: Budget) -> ConditionResult:
    pool = [
        run
        for run in records.runs
        if run.finished
        and run.conclusion == "success"
        and run.branch == budget.skipped_job.branch
        and run.jobs_read
    ]
    if len(pool) < budget.skipped_job.minimum_runs:
        return ConditionResult(
            SKIPPED_JOB,
            NOT_MEASURED,
            f"{len(pool)} finished successful run(s) on {budget.skipped_job.branch} with their "
            f"jobs read, and the rule needs at least {budget.skipped_job.minimum_runs}: the set of "
            "jobs that should have run is derived from the runs themselves, so one run cannot say "
            "what another should have done",
        )

    expected = {
        job.name
        for run in pool
        for job in run.jobs
        if job.conclusion != "skipped"
    }
    findings = []
    for run in sorted(pool, key=lambda run: run.started_at):
        for job in run.jobs:
            if job.name in expected and job.conclusion == "skipped":
                findings.append(
                    Finding(
                        condition=SKIPPED_JOB,
                        subject=f"{job.name} in run {run.id}",
                        measured=(
                            f"run {run.id} on {run.branch} started {run.started_at:%Y-%m-%d %H:%M} "
                            f"concluded success while the job {job.name} was skipped; that job ran "
                            f"in other finished successful runs on {budget.skipped_job.branch} in "
                            "the same window"
                        ),
                        threshold=(
                            f"every job seen running in a finished successful run on "
                            f"{budget.skipped_job.branch} runs in all of them"
                        ),
                        proposal=(
                            "A skipped job counts as successful when GitHub judges a required "
                            "check, so this run is green on a job nobody ran. Find out why it was "
                            "skipped before trusting the result."
                        ),
                    )
                )

    measured_over = (
        f"{len(pool)} finished successful run(s) on {budget.skipped_job.branch}; "
        f"{len(expected)} job name(s) were seen running in at least one of them, and each run was "
        "checked against that set"
    )
    return ConditionResult(SKIPPED_JOB, RAISED if findings else CLEAR, measured_over, tuple(findings))


# ---------------------------------------------------------------------------------------------
# The whole report
# ---------------------------------------------------------------------------------------------


def _inventory(records: RunRecords) -> tuple[tuple[str, str], ...]:
    runs = records.runs
    finished = [run for run in runs if run.finished]
    jobs = [job for run in runs for job in run.jobs]
    logs_read = [job for job in jobs if job.log == LOG_READ]
    return (
        ("runs in the window", str(len(runs))),
        ("finished runs", str(len(finished))),
        (
            "runs that never finished",
            str(len(runs) - len(finished)),
        ),
        ("runs whose jobs were read", str(sum(1 for run in runs if run.jobs_read))),
        ("job records", str(len(jobs))),
        ("job logs read", str(len(logs_read))),
        ("job logs asked for and refused", str(sum(1 for job in jobs if job.log == LOG_UNAVAILABLE))),
        ("failing test names read", str(sum(len(job.failing_tests) for job in jobs))),
        ("jobs that reported a test count", str(sum(1 for job in jobs if job.tests_reported is not None))),
    )


def judge(records: RunRecords, budget: Budget) -> Report:
    """Run all four conditions over the records and return the whole report."""
    conditions = (
        check_budget(records, budget),
        check_suite_growth(records, budget),
        check_repeated_red_test(records, budget),
        check_skipped_job(records, budget),
    )
    seen = tuple(result.condition for result in conditions)
    if seen != CONDITIONS:
        raise AssertionError(f"the keeper must judge every condition, in order: {CONDITIONS}, got {seen}")
    return Report(records=records, budget=budget, conditions=conditions, inventory=_inventory(records))
