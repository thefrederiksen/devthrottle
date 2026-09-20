"""The collector, fed the payloads GitHub really answers with.

The frozen week that test_01 judges was produced by this code, so the shape it reads has to be the
shape GitHub sends. The payloads in tests/fixtures were captured from the live interface on
19 September 2026 and kept field for field, apart from the blocks the keeper never reads (the
person who pushed, the commit, the repository copy inside each run), which were removed rather
than published.

The command the collector runs is the seam. Nothing here touches the network.
"""

from __future__ import annotations

import pytest

from collect import GitHubRecordSource, collect
from conftest import FIXTURES, moment
from errors import KeeperError

REPOSITORY = "thefrederiksen/devthrottle"
FAILED_JOB = 105831465475  # Build & Test (.NET) of run 35418417268, which named a red test
WINDOW_FROM = moment("2026-09-16T00:00:00Z")
WINDOW_TO = moment("2026-09-20T00:00:00Z")


def _fixture(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8", errors="replace")


class FakeGitHub:
    """Answers exactly what the live interface answered, and records what it was asked."""

    def __init__(self, refuse_logs_for: set[int] | None = None) -> None:
        self.asked: list[str] = []
        self.refuse_logs_for = refuse_logs_for or set()

    def __call__(self, arguments):
        path = arguments[1]
        self.asked.append(path)
        if path == f"repos/{REPOSITORY}":
            return 0, _fixture("github-repository.json"), ""
        if path.startswith(f"repos/{REPOSITORY}/actions/runs?"):
            return 0, _fixture("github-run-list-page.json"), ""
        if path.startswith(f"repos/{REPOSITORY}/actions/runs/35418417268/jobs"):
            return 0, _fixture("github-jobs-for-run-35418417268.json"), ""
        if path.startswith(f"repos/{REPOSITORY}/actions/runs/35461379953/jobs"):
            return 0, '{"total_count":1,"jobs":[{"id":999,"name":"Build & Test (.NET)",' \
                      '"status":"completed","conclusion":"success",' \
                      '"started_at":"2026-09-19T18:30:00Z","completed_at":"2026-09-19T19:50:00Z"}]}', ""
        if "/actions/jobs/" in path and path.endswith("/logs"):
            job = int(path.split("/actions/jobs/")[1].split("/")[0])
            if job in self.refuse_logs_for:
                return 1, "", "gh: Not Found (HTTP 404)"
            return 0, _fixture("job-log-dotnet-excerpt.txt"), ""
        raise AssertionError(f"the collector asked for something unexpected: {path}")


def _collect(fake: FakeGitHub):
    source = GitHubRecordSource(REPOSITORY, runner=fake)
    records = collect(source, "CI", WINDOW_FROM, WINDOW_TO, now=WINDOW_TO)
    return source, records


def test_only_the_named_workflow_is_kept():
    fake = FakeGitHub()
    source, records = _collect(fake)
    assert source.progress.runs_listed == 4
    assert source.progress.runs_in_window == 3
    assert {run.id for run in records.runs} == {35418417268, 35461379953, 35038604026}
    assert all(run.workflow == "CI" for run in records.runs)


def test_the_default_branch_comes_from_the_repository_itself():
    _, records = _collect(FakeGitHub())
    assert records.default_branch == "main"


def test_a_run_that_was_cancelled_keeps_its_length_but_is_not_finished():
    _, records = _collect(FakeGitHub())
    cancelled = next(run for run in records.runs if run.id == 35038604026)
    assert cancelled.conclusion == "cancelled"
    assert cancelled.finished is False
    assert cancelled.jobs == ()
    assert cancelled.jobs_read is False


def test_a_failed_run_has_its_jobs_and_the_red_test_out_of_the_log():
    fake = FakeGitHub()
    _, records = _collect(fake)
    run = next(run for run in records.runs if run.id == 35418417268)
    assert run.finished and run.conclusion == "failure"
    assert run.jobs_read is True
    assert len(run.jobs) == 10
    assert round(run.minutes, 1) == 82.2

    failed = [job for job in run.jobs if job.conclusion == "failure"]
    assert [job.name for job in failed] == ["Build & Test (.NET)", "CI result"]
    assert any(
        name.endswith("The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner")
        for name in run.failing_tests()
    )
    # Only the failed jobs' logs were asked for, and the successful ones say so rather than
    # pretending to be empty.
    successful = [job for job in run.jobs if job.conclusion == "success"]
    assert {job.log for job in successful} == {"not-fetched"}
    assert all(job.tests_reported is None for job in successful)


def test_a_log_the_interface_refuses_is_recorded_as_unavailable_not_as_empty():
    fake = FakeGitHub(refuse_logs_for={FAILED_JOB})
    source, records = _collect(fake)
    run = next(run for run in records.runs if run.id == 35418417268)
    job = next(job for job in run.jobs if job.name == "Build & Test (.NET)")
    assert job.log == "unavailable"
    assert job.failing_tests == ()
    assert run.unread_failed_jobs() == ("Build & Test (.NET)",)
    assert source.progress.logs_unavailable == 1


def test_the_green_run_on_main_has_every_log_read_so_its_tests_can_be_counted():
    fake = FakeGitHub()
    _, records = _collect(fake)
    run = next(run for run in records.runs if run.id == 35461379953)
    assert [job.log for job in run.jobs] == ["read"]
    assert run.jobs[0].tests_reported == 2678


def test_the_collector_says_what_it_did():
    fake = FakeGitHub()
    source, _ = _collect(fake)
    assert source.progress.jobs_read_for_runs == 2
    assert source.progress.logs_read == 3
    assert source.progress.logs_unavailable == 0


def test_a_repository_written_the_wrong_way_is_refused_before_anything_is_asked():
    with pytest.raises(KeeperError, match="must be written owner/name"):
        GitHubRecordSource("devthrottle", runner=FakeGitHub())


def test_a_failing_command_is_an_error_rather_than_an_empty_answer():
    def refuse(arguments):
        return 1, "", "gh: Bad credentials (HTTP 401)"

    source = GitHubRecordSource(REPOSITORY, runner=refuse)
    with pytest.raises(KeeperError, match="Bad credentials"):
        collect(source, "CI", WINDOW_FROM, WINDOW_TO, now=WINDOW_TO)
