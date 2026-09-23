"""Reading a job's log: which tests were named as failing, and how many tests were reported.

A run record from GitHub says a job failed. It does not say which test failed, and it does not say
how many tests ran, so both are read out of the job's own log.

Three test runners are recognised, and they are the ones any repository is likely to use rather
than anything particular to this one:

  - the .NET test runner (VSTest), which prints "  Failed <name> [12 ms]" per failing test and
    "Total tests: 2678" once per test project;
  - pytest, which prints "FAILED tests/test_x.py::test_y - ..." per failing test and a summary
    line such as "1 failed, 2158 passed in 73.38s";
  - vitest, which prints "FAIL  src/x.test.ts > name" per failing file and "Tests  1453 passed
    (1453)" per workspace.

A log whose runner is NOT recognised yields no readings, and the keeper reports that as "no
reading" rather than as "nothing wrong". That distinction is the whole point: a job the keeper
cannot read is an unmeasured job, never a clean one.

Two shapes the log itself carries, both stripped before any pattern is applied:
  - every line begins with the timestamp GitHub prefixes, "2026-09-19T03:24:09.8443400Z ";
  - a job that runs with colour on carries ANSI escape sequences inside the line, because a test
    that only fails when colour is on is a real defect this repository has already paid for, so
    its jobs set FORCE_COLOR deliberately.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

# "2026-09-19T03:24:09.8443400Z " - the prefix GitHub puts on every line of a downloaded job log.
_TIMESTAMP = re.compile(r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d+Z ")

# ANSI escape sequences: the colour codes a runner writes when it believes it has a terminal.
_ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

# VSTest: "  Failed CcDirector.Gateway.Tests.SomeTests.Some_case [24 ms]". The name is taken up to
# the duration in square brackets, NOT up to the first space: a parameterised test's name carries
# spaces inside it ("Half_a_cursor_is_refused(half: \"after=2026-09-15\")") and cutting at the
# first space would report a different, shorter name each time the parameters changed.
_DOTNET_FAILED = re.compile(r"^ {2,}Failed\s+(\S.*?)\s+\[[^\]]*\]$")
_DOTNET_TOTAL = re.compile(r"^Total tests:\s+([0-9]+)$")

# pytest: "FAILED tests/test_x.py::test_y - AssertionError: ..." and the summary line
# "1 failed, 2158 passed in 73.38s (0:01:13)" or "40 passed in 0.07s".
_PYTEST_FAILED = re.compile(r"^FAILED\s+(\S+)")
_PYTEST_SUMMARY = re.compile(r"^(?:=+\s*)?((?:[0-9]+ [a-z]+(?:, )?)+)\s*in\s+[0-9.]+s")
_PYTEST_PART = re.compile(r"([0-9]+) ([a-z]+)")
# The outcomes pytest counts that are one test each. "warnings" and "errors" are not tests.
_PYTEST_TEST_OUTCOMES = {"passed", "failed", "skipped", "xfailed", "xpassed", "deselected"}

# vitest: " FAIL  src/x.test.ts > a case" and "      Tests  1 failed | 1452 passed (1453)".
#
# The first token after FAIL must look like a test file, because "FAIL" on its own is a word other
# runners print inside their own failure messages: a .NET assertion message in this repository's
# own logs reads "  FAIL (1) - the C# fold and the shared answers differ.", and a looser pattern
# reported that sentence as the name of a failing test.
_VITEST_FAILED = re.compile(r"^\s{0,2}FAIL\s+(\S+\.[cm]?[jt]sx?(?:\s+>\s+\S.*)?)$")
_VITEST_TOTAL = re.compile(r"^\s*Tests\s+.*\(([0-9]+)\)$")


@dataclass(frozen=True)
class LogReading:
    """What one job's log said about tests.

    `tests_reported` is the number of tests the runners in this job reported in total, summed over
    every summary line found, or None when no summary line was recognised at all. None means "no
    reading", and never zero: a job that reported nothing and a job that ran nothing must not look
    the same.
    """

    failing_tests: tuple[str, ...]
    tests_reported: int | None
    recognised_runners: tuple[str, ...]


def clean_line(line: str) -> str:
    """Strip the log timestamp and any ANSI colour sequences, and drop the line ending."""
    return _ANSI.sub("", _TIMESTAMP.sub("", line)).rstrip("\r\n").rstrip()


def read_log(text: str) -> LogReading:
    """Read one job log. Returns the failing test names and the number of tests reported."""
    failing: list[str] = []
    seen: set[str] = set()
    total = 0
    counted = False
    runners: set[str] = set()

    for raw in text.splitlines():
        line = clean_line(raw)
        if not line:
            continue

        match = _DOTNET_FAILED.match(line)
        if match:
            runners.add("dotnet")
            name = match.group(1)
            if name not in seen:
                seen.add(name)
                failing.append(name)
            continue

        match = _DOTNET_TOTAL.match(line)
        if match:
            runners.add("dotnet")
            total += int(match.group(1))
            counted = True
            continue

        match = _PYTEST_FAILED.match(line)
        if match:
            runners.add("pytest")
            name = match.group(1)
            if name not in seen:
                seen.add(name)
                failing.append(name)
            continue

        match = _PYTEST_SUMMARY.match(line)
        if match:
            parts = _PYTEST_PART.findall(match.group(1))
            tests = sum(int(number) for number, word in parts if word in _PYTEST_TEST_OUTCOMES)
            if tests:
                runners.add("pytest")
                total += tests
                counted = True
            continue

        match = _VITEST_TOTAL.match(line)
        if match:
            runners.add("vitest")
            total += int(match.group(1))
            counted = True
            continue

        match = _VITEST_FAILED.match(line)
        if match:
            runners.add("vitest")
            name = match.group(1)
            if name not in seen:
                seen.add(name)
                failing.append(name)
            continue

    return LogReading(
        failing_tests=tuple(failing),
        tests_reported=total if counted else None,
        recognised_runners=tuple(sorted(runners)),
    )
