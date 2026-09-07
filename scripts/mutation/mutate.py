"""THE FLEET'S MUTATION RUNNER. Reusable: point it at a spec.

Mutation harness with a COMPILE GATE, and a GATE THAT ACTUALLY FAILS.

A spec names the repository (relative to the repository root), the test project, the test filter,
and a list of mutants - so this runner is not pinned to one branch, one worktree or one mission.

Usage: python scripts/mutation/mutate.py <spec.json>

--------------------------------------------------------------------------------------------------
WHY THE COMPILE GATE EXISTS

A mutant that does not build produces no failing test line, which looks identical to a mutant no
test caught. A harness that decided "survived" from the ABSENCE of failure lines duly reported a
survivor on a mutation that had never run at all. That is the same defect at three altitudes on one
day: could-not-read reported as nothing-is-there, did-not-run reported as came-back-clean, and
did-not-compile reported as survived.

So a mutant counts only once the build has exited 0 AND the test run has produced a count line it
can be read from. Three outcomes, never two:

    KILLED   - it built, tests ran, at least one failed
    SURVIVED - it built, tests ran, none failed
    BROKEN   - it did not build, or the run produced no readable result. NEITHER of the other two,
               and never silently one of them. An instrument fault, not a verdict.

--------------------------------------------------------------------------------------------------
THREE THINGS THIS GETS RIGHT THAT AN EARLIER VERSION OF IT DID NOT

All three were found by an independent review of the harness this one replaced, and all three were
present here too - which is the point worth keeping: a harness that checks other people's evidence
is itself unchecked by anything, so it has to be reviewed like the code it judges.

1. THE TEST PROCESS'S EXIT CODE IS READ. It was not. The verdict came entirely from a regular
   expression over standard output, so a run that exited NONZERO for a reason the count line does
   not describe - a crashed test host, a failing non-C# build step, one target of a multi-target
   project blowing up - was still recorded as a verdict. A nonzero exit that no counted failure
   explains is now BROKEN.

2. EVERY COUNT LINE IS AGGREGATED, NOT THE FIRST. This repository's test projects are MULTI-TARGET:
   one `dotnet test` prints one summary per target framework. Reading the first match meant reading
   one target's line and ignoring the other, so a mutant killed only on the second target would have
   been recorded as SURVIVED. Not hypothetical - the launcher tests are exactly that shape.

3. A BAD RUN FAILS THE PROCESS. It did not: the table was printed and the script fell off the end,
   so a run that was entirely SURVIVED and BROKEN exited 0. A gate whose failure states do not fail
   is a report, not a gate, and any caller reading the process status got success for an invalid run.

--------------------------------------------------------------------------------------------------
EXPECTED OUTCOMES, AND WHY THE GATE IS NOT SIMPLY "ALL KILLED"

A mutant may declare `"expect": "SURVIVED"` or `"expect": "BROKEN"`; the default is `"KILLED"`. The
run fails when an outcome differs from what the spec expected.

That is not a loophole, it is the same three-state discipline applied to the harness's own verdict.
A survivor somebody has understood and written down - a deliberately redundant guard whose real
guard is killed by its own mutant - is a RESULT. A survivor nobody knew about is a DEFECT IN THE
TESTS. Failing on both, or on neither, folds those two together, which is the exact mistake this
file exists to stop making. Deliberate BROKEN entries are how a spec proves its own gates fire: a
mutant that cannot compile, and one whose anchor text is absent, must both come back BROKEN rather
than being read as either verdict.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

spec = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
repo = (Path(sys.argv[1]).parent.parent.parent / spec["repo"]).resolve()
project = spec["project"]
test_filter = spec["filter"]

# One backup per file touched, taken before anything is mutated and restored after every mutant.
files = sorted({m["file"] for m in spec["mutants"]})
backups = {f: (repo / f).read_text(encoding="utf-8") for f in files}


def restore():
    for f, text in backups.items():
        (repo / f).write_text(text, encoding="utf-8", newline="")


def run(args, timeout):
    # NO SHELL. It used to pass shell=True with an argument LIST, which on Windows joins the list into
    # one command line and lets the shell reinterpret it - so a test filter containing "|", the ordinary
    # way to name two test classes, was read as a pipe and the whole invocation collapsed. Every mutant
    # in that run came back exit 255 with no output.
    #
    # WORTH RECORDING: the gate caught it. All seventeen reported BROKEN - "no test-count line" - rather
    # than SURVIVED, so a harness fault produced a refusal to answer instead of seventeen clean bills of
    # health. That is the third instrument fault this gate has caught in this file, and the reason the
    # BROKEN state exists at all.
    return subprocess.run(args, cwd=repo, capture_output=True, text=True, timeout=timeout)


COUNT = re.compile(r"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)")


def read_run(result):
    """(outcome, detail) for one test invocation. Never guesses from an absence."""
    counts = COUNT.findall(result.stdout)
    if not counts:
        # No readable result at all. Whether it exited 0 or not, there is nothing to read a verdict
        # from - and an exit code alone cannot tell "every test passed" from "nothing ran".
        return "BROKEN", f"no test-count line (exit {result.returncode}) - the run produced no result"

    failed = sum(int(c[0]) for c in counts)
    passed = sum(int(c[1]) for c in counts)
    total = sum(int(c[3]) for c in counts)
    summaries = len(counts)

    if failed > 0:
        return "KILLED", f"{failed} failed of {total} ({passed} passed, {summaries} target summary/ies)"

    # Zero counted failures. Believe that ONLY if the process agreed - a nonzero exit with nothing
    # failing is something the count line does not describe, and reading it as SURVIVED is exactly
    # the absence-shaped mistake.
    if result.returncode != 0:
        return "BROKEN", (f"exit {result.returncode} with no counted failure across {summaries} "
                          f"summary/ies - something failed that the count line does not describe")

    return "SURVIVED", f"0 failed of {total} ({summaries} target summary/ies)"


def sweep():
    """Every mutant, in order. Wrapped by the caller so a fault cannot leave source mutated."""
    results = []
    for mutant in spec["mutants"]:
        expected = mutant.get("expect", "KILLED")
        restore()
        path = repo / mutant["file"]
        text = path.read_text(encoding="utf-8")
        if mutant["find"] not in text:
            results.append((mutant["name"], "BROKEN", expected, "the text to mutate was not found in the file"))
            continue
        path.write_text(text.replace(mutant["find"], mutant["replace"], 1), encoding="utf-8", newline="")

        # GATE ONE: it must build. A non-building mutant is an instrument fault, not a survivor.
        build = run(["dotnet", "build", project, "-v", "q", "--nologo"], timeout=900)
        if build.returncode != 0:
            first = next((l.strip() for l in build.stdout.splitlines() if "error" in l.lower()), "")
            results.append((mutant["name"], "BROKEN", expected, f"did not compile: {first[:150]}"))
            continue

        # GATE TWO: the run must produce a readable result AND agree with its own exit code.
        outcome, detail = read_run(run(["dotnet", "test", project, "--no-build", "--filter", test_filter, "--nologo"],
                                       timeout=900))
        results.append((mutant["name"], outcome, expected, detail))

    return results

# RESTORATION IS IN A finally, AND THAT IS NOT TIDINESS. Both reviews caught it: a timeout, a Ctrl+C
# or any unexpected fault after a mutation was applied left the PRODUCT SOURCE MUTATED on disk. The next
# person to build would be building a deliberately broken tree, with nothing saying so - and a mutation
# harness is exactly the tool somebody runs and walks away from.
try:
    results = sweep()
finally:
    restore()

print("")
print(f"{'MUTANT':<58} {'OUTCOME':<10} {'EXPECTED':<10} DETAIL")
print("-" * 120)
unexpected = 0
for name, outcome, expected, detail in results:
    flag = "" if outcome == expected else "   <-- NOT WHAT THE SPEC EXPECTED"
    if outcome != expected:
        unexpected += 1
    print(f"{name:<58} {outcome:<10} {expected:<10} {detail}{flag}")
print("-" * 120)
for outcome in ("KILLED", "SURVIVED", "BROKEN"):
    print(f"{outcome}: {sum(1 for _, o, _, _ in results if o == outcome)}")

# THE GATE. A bad run fails the process, so a caller reading the exit status learns what the table
# says rather than "it ran".
if unexpected:
    print(f"\nFAIL: {unexpected} mutant(s) did not match the outcome the spec expected.")
    sys.exit(1)
print("\nPASS: every mutant matched the outcome the spec expected.")
