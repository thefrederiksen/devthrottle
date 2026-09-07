"""THE FLEET'S MUTATION RUNNER. Reusable: point it at a spec.

Mutation harness with a COMPILE GATE.

A spec names the repository (relative to the repository root), the test project, the test filter,
and a list of mutants - so this runner is not pinned to one branch, one worktree or one mission.


Why the gate exists: a mutant that does not build produces no failing test line, which looks
identical to a mutant no test caught. Phase 0's harness read that absence as SURVIVED. So here a
mutant only COUNTS once the build has exited 0, and the run is only read once a full test-count
line has been printed. Three outcomes, never two:

  KILLED          built, tests ran, at least one failed
  SURVIVED        built, tests ran, none failed
  BROKEN          did not build, or produced no test-count line - NOT a result, an instrument fault

Usage: python mutate.py <spec.json>
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
    return subprocess.run(args, cwd=repo, capture_output=True, text=True, timeout=timeout, shell=True)


COUNT = re.compile(r"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)")

results = []
for mutant in spec["mutants"]:
    restore()
    path = repo / mutant["file"]
    text = path.read_text(encoding="utf-8")
    if mutant["find"] not in text:
        results.append((mutant["name"], "BROKEN", "the text to mutate was not found in the file"))
        continue
    path.write_text(text.replace(mutant["find"], mutant["replace"], 1), encoding="utf-8", newline="")

    # GATE ONE: it must build. A non-building mutant is an instrument fault, not a survivor.
    build = run(["dotnet", "build", project, "-v", "q", "--nologo"], timeout=900)
    if build.returncode != 0:
        first = next((l.strip() for l in build.stdout.splitlines() if "error" in l.lower()), "")
        results.append((mutant["name"], "BROKEN", f"did not compile: {first[:160]}"))
        continue

    # GATE TWO: the tests must actually have RUN. No count line = no result.
    test = run(["dotnet", "test", project, "--no-build", "--filter", test_filter, "--nologo"], timeout=900)
    counts = COUNT.search(test.stdout)
    if not counts:
        results.append((mutant["name"], "BROKEN", "no test-count line - the run produced no result"))
        continue

    failed, passed, _, total = (int(g) for g in counts.groups())
    verdict = "KILLED" if failed > 0 else "SURVIVED"
    results.append((mutant["name"], verdict, f"{failed} failed of {total} ({passed} passed)"))

restore()

print("")
print(f"{'MUTANT':<58} {'OUTCOME':<10} DETAIL")
print("-" * 110)
for name, verdict, detail in results:
    print(f"{name:<58} {verdict:<10} {detail}")
print("-" * 110)
for outcome in ("KILLED", "SURVIVED", "BROKEN"):
    n = sum(1 for _, v, _ in results if v == outcome)
    print(f"{outcome}: {n}")
