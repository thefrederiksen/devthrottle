"""Reading a job's log, against excerpts taken verbatim out of this repository's own runs.

The excerpts in tests/fixtures are real lines from real job logs, timestamps, colour codes and
all. They are the reason this reader can be trusted on a log nobody wrote for it.
"""

from __future__ import annotations

import runnerlogs
from conftest import FIXTURES

FLEET_MANAGER_TEST = (
    "CcDirector.Gateway.Tests.FleetManagerRoutesHostTests."
    "The_session_list_pins_the_Fleet_Manager_and_offers_each_row_its_change_of_owner"
)


def _excerpt(name: str) -> str:
    return (FIXTURES / f"job-log-{name}-excerpt.txt").read_text(encoding="utf-8", errors="replace")


def test_the_dotnet_runner_is_read_whole():
    reading = runnerlogs.read_log(_excerpt("dotnet"))
    assert reading.recognised_runners == ("dotnet",)
    assert reading.tests_reported == 2678
    assert FLEET_MANAGER_TEST in reading.failing_tests


def test_a_parameterised_test_keeps_its_whole_name():
    """The name of a test run with arguments carries spaces and quotation marks inside it. Cut at
    the first space it becomes a different name on every run, and no two runs would ever look like
    the same test being red twice."""
    reading = runnerlogs.read_log(_excerpt("dotnet"))
    assert (
        'CcDirector.Gateway.Tests.Api.AdminTurnVerdictFeedbackEndpointScopeTests.'
        'Half_a_cursor_is_refused(half: "after_verdict=tv-1")' in reading.failing_tests
    )
    assert (
        'CcDirector.Gateway.Tests.Api.AdminTurnVerdictFeedbackEndpointScopeTests.'
        'Half_a_cursor_is_refused(half: "after=2026-09-15T20:00:00Z")' in reading.failing_tests
    )


def test_the_word_fail_inside_a_test_s_own_output_is_not_a_test_name():
    """This repository's own logs print lines such as "FAIL (1) - the C# fold and the shared
    answers differ." inside a failing test's message. A looser pattern read that sentence as the
    name of a failing test, and a sentence cannot be the same test twice."""
    reading = runnerlogs.read_log(_excerpt("dotnet"))
    assert all(not name.startswith("(1)") for name in reading.failing_tests), reading.failing_tests
    assert all("the C# fold" not in name for name in reading.failing_tests)
    assert len(reading.failing_tests) == 3


def test_pytest_is_read_through_the_colour_it_prints():
    """These jobs set FORCE_COLOR on purpose, because a test that only fails when colour is on is
    a defect this repository has already paid for. So the log carries escape codes inside the
    line, and the reader strips them before it matches anything."""
    reading = runnerlogs.read_log(_excerpt("pytest"))
    assert reading.recognised_runners == ("pytest",)
    assert reading.failing_tests == (
        "tests/test_session_report.py::test_a_refused_delivery_is_a_failure_not_a_shrug",
    )
    # 40 + 84 + (1 failed + 2158 passed)
    assert reading.tests_reported == 2283


def test_vitest_totals_are_summed_across_the_workspaces():
    reading = runnerlogs.read_log(_excerpt("vitest"))
    assert reading.recognised_runners == ("vitest",)
    assert reading.tests_reported == 1453 + 457 + 101
    assert reading.failing_tests == ()


def test_a_vitest_failure_names_the_file_and_the_case():
    reading = runnerlogs.read_log(
        " FAIL  src/session/VoiceCard.test.tsx > VoiceCard > offers no button without a reason\n"
        "      Tests  1 failed | 4 passed (5)\n"
    )
    assert reading.failing_tests == (
        "src/session/VoiceCard.test.tsx > VoiceCard > offers no button without a reason",
    )
    assert reading.tests_reported == 5


def test_a_log_from_a_runner_nobody_recognises_reports_no_reading_rather_than_zero():
    """None and 0 are different answers. Zero tests would say the suite is empty; no reading says
    the keeper cannot see it, and the keeper reports that rather than judging on it."""
    reading = runnerlogs.read_log(
        "2026-09-19T03:24:09.1234567Z some other test runner: everything is fine\n"
    )
    assert reading.tests_reported is None
    assert reading.failing_tests == ()
    assert reading.recognised_runners == ()


def test_a_test_named_twice_in_one_log_is_one_test():
    text = (
        "  Failed Some.Suite.A_case [1 ms]\n"
        "  Failed Some.Suite.A_case [1 ms]\n"
        "Total tests: 2\n"
    )
    assert runnerlogs.read_log(text).failing_tests == ("Some.Suite.A_case",)
