#!/usr/bin/env python3
"""Decide whether the hosted image contract test actually RAN and PASSED.

Why this exists at all, rather than trusting the exit code of `dotnet test`.
------------------------------------------------------------------------
The hosted deploy is gated on this test. A gate whose pass condition is "nothing reported a
failure" certifies a run that never happened: a filter that matched no test, a project that
built no tests, a test marked as skipped, a result file that was never written - every one of
those leaves a green step and an unproven hosted image, and a gate that can be satisfied by
doing nothing is not a gate.

So the pass condition here is a PRESENCE, and it is reconciled against an inventory the run
did not get to choose: the list of tests the built assembly itself declares, read out of it by
`dotnet test --list-tests`. Every declared test must appear in the result file, and every one
of those results must say Passed.

Three outcomes, all three stated:
  * every declared test present and Passed                     -> pass, and the names are printed
  * a result that is not Passed, or a declared test with none  -> the defect this gate exists for
  * an empty declared list, or a missing or empty result file  -> a BROKEN INSTRUMENT, and it
                                                                  fails. It is never read as a
                                                                  clean run.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

# The heading `dotnet test --list-tests` prints before the names.
DECLARED_HEADING = "The following Tests are available:"

# TRX is namespaced; the namespace is part of every element name when ElementTree parses it.
TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# A theory case is reported as `Namespace.Class.Method(arg: 1)`. Comparing at the method level lets
# the declared list and the result file agree however each of them chooses to spell the arguments.
ARGUMENTS = re.compile(r"\(.*\)$", re.DOTALL)


def fail(message: str) -> None:
    print()
    print("REFUSED: " + message)
    print()
    print("The hosted image contract is NOT proven on this commit, so the deploy must not happen.")
    sys.exit(1)


def method_of(test_name: str) -> str:
    return ARGUMENTS.sub("", test_name).strip()


def read_declared(path: Path) -> list[str]:
    """The tests the built assembly declares, read off the assembly itself."""
    if not path.is_file():
        fail(f"the declared-test list '{path}' does not exist. The step that writes it did not run, "
             "so there is no inventory to reconcile the run against.")

    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    try:
        heading_at = next(i for i, line in enumerate(lines) if DECLARED_HEADING in line)
    except StopIteration:
        fail(f"'{path}' does not contain the heading '{DECLARED_HEADING}'. Either no test was "
             "discovered in the project, or the tool changed how it reports the list. Both mean this "
             "gate cannot see what it is supposed to reconcile against - which is a broken "
             "instrument, not a clean run.")

    declared: list[str] = []
    for line in lines[heading_at + 1:]:
        if not line.strip():
            continue
        # The names are indented under the heading; anything unindented has moved on to other output.
        if not line.startswith((" ", "\t")):
            break
        declared.append(line.strip())
    return declared


def read_results(directory: Path) -> list[tuple[str, str]]:
    """Every test result in every result file under `directory`, as (name, outcome)."""
    if not directory.is_dir():
        fail(f"the results directory '{directory}' does not exist. The test run produced no record, "
             "so nothing here can say whether it ran.")

    trx_files = sorted(directory.rglob("*.trx"))
    if not trx_files:
        fail(f"no .trx result file was written under '{directory}'. A test run that leaves no record "
             "is indistinguishable from one that never started.")

    results: list[tuple[str, str]] = []
    for trx in trx_files:
        print(f"Reading result file: {trx}")
        root = ElementTree.parse(trx).getroot()
        for result in root.iter(f"{TRX_NAMESPACE}UnitTestResult"):
            name = result.get("testName")
            outcome = result.get("outcome")
            if name is None or outcome is None:
                fail(f"a result in '{trx}' carries no test name or no outcome, so it cannot be "
                     "reconciled. The result file is malformed.")
            results.append((name, outcome))
    return results


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--declared", required=True, type=Path,
                        help="the file holding the output of `dotnet test --list-tests`")
    parser.add_argument("--results", required=True, type=Path,
                        help="the directory holding the .trx result file(s)")
    args = parser.parse_args()

    declared = read_declared(args.declared)
    print(f"Tests the built assembly DECLARES: {len(declared)}")
    for name in declared:
        print(f"  declared: {name}")
    if not declared:
        fail("the built assembly declares NO tests. An empty inventory would make every check below "
             "vacuously true, which is exactly how a gate comes to certify nothing.")

    results = read_results(args.results)
    print(f"Test results RECORDED: {len(results)}")
    for name, outcome in results:
        print(f"  {outcome}: {name}")
    if not results:
        fail("the result file holds no test results at all. Nothing ran.")

    declared_methods = {method_of(name) for name in declared}
    result_methods = {method_of(name) for name, _ in results}

    missing = sorted(declared_methods - result_methods)
    if missing:
        fail("these tests are declared by the assembly and have NO result - they did not run:\n"
             + "\n".join(f"  {name}" for name in missing))

    unexpected = sorted(result_methods - declared_methods)
    if unexpected:
        fail("these results name tests the assembly does not declare, so the run and the inventory "
             "are not describing the same thing:\n" + "\n".join(f"  {name}" for name in unexpected))

    not_passed = sorted((name, outcome) for name, outcome in results if outcome != "Passed")
    if not_passed:
        fail("these tests did not pass. A skipped or not-executed test is a refusal here, not a "
             "pass - the hosted image is only proven by a test that actually ran:\n"
             + "\n".join(f"  {outcome}: {name}" for name, outcome in not_passed))

    print()
    print(f"PASSED: all {len(results)} test result(s) covering all {len(declared_methods)} declared "
          "test(s) say Passed.")
    print("The published hosted image is proven to fail closed without the hosted contract, on this "
          "commit. The deploy may proceed.")


if __name__ == "__main__":
    main()
