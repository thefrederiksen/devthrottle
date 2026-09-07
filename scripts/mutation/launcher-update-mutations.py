"""
A PHASE 0 PROOF ARTIFACT, NOT THE FLEET'S MUTATION RUNNER. For a runner, use
scripts/mutation/mutate.py, which takes a spec and works in any worktree.

This file hardcodes REPO to one worktree path that exists on one machine, and carries phase 0's own
mutation list baked in. It is kept because it is the evidence behind the mutation claims on pull
request #2728 and re-running it is how those claims are checked - but it cannot be pointed at
anything else, and reading its name as "the mutation harness" has already sent two people to it.
Furniture that lies is worse than no furniture.
"""

"""
THE MUTATION PROOF FOR ISSUE #2719 PHASE 0, WITH A COMPILE GATE.

Run it from the repository root:  python scripts/mutation/launcher-update-mutations.py

WHY THE COMPILE GATE EXISTS, AND WHY IT IS THE POINT OF THIS FILE.

The first harness used for this branch decided "survived" from the ABSENCE of failure lines in the
test output. A mutant that does not COMPILE produces no failing test - which is indistinguishable
from a mutant no test caught. It duly reported a SURVIVOR on a mutation that had never run at all,
and the mutation in question was `if (p.Pid <= 0)` changed to `if (false)`, which the compiler
rejects as unreachable code.

That is the same defect this mission hit at two other altitudes on the same day: could-not-read
reported as nothing-is-there, and did-not-run reported as came-back-clean. A pass condition that is
an ABSENCE is satisfied by a broken instrument.

So a mutant counts here only if the product project BUILDS, and there are THREE outcomes, never two:

    KILLED   - it built, and at least one test failed
    SURVIVED - it built, every test passed. A REAL DEFECT IN THE TESTS.
    BROKEN   - it did not build, or no test summary line was observed. Neither killed nor survived;
               the mutation must be rewritten before it means anything.

The gate has been run against a known-bad input rather than trusted: with the non-compiling mutant
above, this harness reports BROKEN and the old absence-based reading reports SURVIVED, on the same
mutant, in the same run.

Every claim of the form "each new test dies to the defect it names" on pull request #2728 was
produced by this file. Re-run it before trusting that claim again - and if you add a test to this
area, add the mutation that should kill it, so the two lists stay reconciled in both directions.
"""

"""
Mutation harness with a COMPILE GATE.

The previous harness decided "survived" from the ABSENCE of failure lines in the test output.
A mutant that does not COMPILE produces no failing test, which is indistinguishable from a mutant
no test caught - so it reported a survivor on a mutation that had never run at all.

Here a mutant counts only if the product project BUILDS. Three outcomes, never two:
    KILLED  - it built, and at least one test failed
    SURVIVED- it built, and every test passed          <- a real defect in the tests
    BROKEN  - it did not build; neither killed nor survived, and the mutation must be rewritten
"""
import io, subprocess, os, sys, json

REPO = r"D:\ReposFred\devthrottle-launcher-update"
ENGINE_SRC = r"tools\cc-director-setup-engine\CcDirector.Setup.Engine.csproj"
ENGINE_TESTS = r"tools\cc-director-setup-engine.Tests\CcDirector.Setup.Engine.Tests.csproj"
LAUNCHER_SRC = r"src\CcDirector.Launcher\CcDirector.Launcher.csproj"
LAUNCHER_TESTS = r"src\CcDirector.Launcher.Tests\CcDirector.Launcher.Tests.csproj"

OWNER = r"tools\cc-director-setup-engine\LauncherUpdateOwner.cs"
WITNESS = r"tools\cc-director-setup-engine\LauncherWitness.cs"
PROCS = r"tools\cc-director-setup-engine\InstalledLauncherProcesses.cs"
STOP = r"tools\cc-director-setup-engine\SingleProcessStop.cs"
LOCK = r"tools\cc-director-setup-engine\BinarySwapLock.cs"
DIRUPD = r"src\CcDirector.Launcher\DirectorUpdateOwner.cs"

E = (ENGINE_SRC, ENGINE_TESTS)
L = (LAUNCHER_SRC, LAUNCHER_TESTS)

# (label, (src_proj, test_proj), file, find, replace, expected_test_substring)
MUTATIONS = [
 ("the shortcut accepts a merely-alive launcher", E, OWNER,
  "var witnessedTheInstalledLauncher =\n            reading.Witnessed && ours.Count == 1 && ours[0].Pid == reading.Pid;",
  "var witnessedTheInstalledLauncher =\n            reading.ProcessAlive;",
  "AlreadyTheStagedVersionButCANNOTBeCommanded"),

 ("the shortcut ignores WHICH process was witnessed", E, OWNER,
  "reading.Witnessed && ours.Count == 1 && ours[0].Pid == reading.Pid;",
  "reading.Witnessed;",
  "AWITNESSEDLauncherThatIsNotTheINSTALLEDOne"),

 ("the shortcut never fires at all", E, OWNER,
  "if (witnessedTheInstalledLauncher && VersionsMatch(staged.Version, reading.Version))",
  "if (!witnessedTheInstalledLauncher && VersionsMatch(staged.Version, reading.Version))",
  "AND_COMMANDABLE_InstallsNothing"),

 ("a not-observable platform is no longer refused", E, OWNER,
  "if (reading.CommandSurface == LauncherCommandSurface.NotObservable)",
  "if (reading.CommandSurface == LauncherCommandSurface.Absent)",
  "WhereTheCommandSurfaceCannotBeObserved"),

 ("the swap takes a lock nobody else takes", E, OWNER,
  'who: "launcher-update",\n            name: SwapLockName);',
  'who: "launcher-update",\n            name: SwapLockName + "-unshared");',
  "WhileAnotherBinarySwapHoldsTheMachineWideLock"),

 ("the orphaned Director is a plain success again", E, OWNER,
  "SelfUpdateOutcome.Updated when !stillOurs => LauncherUpdateDecision.AppliedButThisDirectorLostItsInstance,\n            SelfUpdateOutcome.Updated => LauncherUpdateDecision.Applied,",
  "SelfUpdateOutcome.Updated => LauncherUpdateDecision.Applied,",
  "ThatIsItsOwnDecision"),

 ("the after-the-swap reading is not reported", E, OWNER,
  'steps.Add($"after the swap: {_witness.Read().Detail}");',
  'steps.Add("after the swap: done");',
  "AfterARollback"),

 ("the started launcher is not told which root to serve", E, OWNER,
  'psi.Environment["CC_DIRECTOR_ROOT"] = layout.LocalRoot;',
  'psi.Environment.Remove("CC_DIRECTOR_ROOT");',
  "IsTOLDWhichRootToServe"),

 ("the unreadable sentinel is handed to the stop again", E, OWNER,
  "if (p.Pid <= 0)",
  "if (p.Pid < 0)",
  "IsNeverHandedToTheStop"),

 ("an unreadable list is reported as an empty one", E, OWNER,
  'return [new LauncherProcess(0, "the process list is unreadable")];',
  "return [];",
  "IsNotAnEmptyOne"),

 ("the witness accepts a not-observable surface", E, WITNESS,
  "public bool Witnessed => Registered && ProcessAlive && CommandSurface == LauncherCommandSurface.Present;",
  "public bool Witnessed => Registered && ProcessAlive && CommandSurface != LauncherCommandSurface.Absent;",
  "ThatIsNOTAWITNESS"),

 ("an unreadable process is dropped instead of refused", E, PROCS,
  "if (unreadable.Count > 0)\n            throw new InvalidOperationException(",
  "if (unreadable.Count > 99)\n            throw new InvalidOperationException(",
  "MakesTheMachineUNDECIDABLE"),

 ("a null main module is fail-open again", E, PROCS,
  "if (hasExited == true) return UnreadableVerdict.CannotBeOurs;",
  "if (hasExited != true) return UnreadableVerdict.CannotBeOurs;",
  "IsUndecidableOnlyWhenItCouldBeOurs"),

 ("an unreadable session is assumed not ours", E, PROCS,
  "if (sessionId is null) return UnreadableVerdict.CouldBeOurs;",
  "if (sessionId is null) return UnreadableVerdict.CannotBeOurs;",
  "IsUndecidableOnlyWhenItCouldBeOurs"),

 ("every unreadable process is undecidable regardless of session", E, PROCS,
  "return sessionId == ourSession ? UnreadableVerdict.CouldBeOurs : UnreadableVerdict.CannotBeOurs;",
  "return UnreadableVerdict.CouldBeOurs;",
  "IsUndecidableOnlyWhenItCouldBeOurs"),

 ("the single kill becomes a tree kill", E, STOP,
  "process.Kill(entireProcessTree: false);",
  "process.Kill(entireProcessTree: true);",
  "KillsOneProcess_NeverATree"),

 ("a kill is put back into the swap's own file", E, OWNER,
  "    private List<LauncherProcess> OursNow()",
  "    private static bool StopHard(System.Diagnostics.Process p) { p.Kill(entireProcessTree: true); return true; }\n\n    private List<LauncherProcess> OursNow()",
  "ExpressesNoProcessKillAtAll"),

 ("the two owners take different locks", L, DIRUPD,
  "internal string SwapLockName { get; init; } = BinarySwapLock.Name;",
  'internal string SwapLockName { get; init; } = @"Global\\a-different-lock";',
  "TheTwoOwnersDefaultToTheSameMachineWideLock"),

 ("the lock stops being machine-wide", L, LOCK,
  'public const string Name = @"Global\\cc-director-binary-swap";',
  'public const string Name = @"Local\\cc-director-binary-swap";',
  "TheLockIsMachineWide"),
]

def run(cmd, timeout=1200):
    return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, timeout=timeout)

results = []
for label, (src_proj, test_proj), relpath, find, replace, expect in MUTATIONS:
    path = os.path.join(REPO, relpath)
    original = io.open(path, encoding="utf-8").read()

    if find not in original:
        results.append((label, "ANCHOR-MISSING", "", ""))
        print(f"[ANCHOR-MISSING] {label}"); sys.stdout.flush(); continue

    io.open(path, "w", encoding="utf-8", newline="").write(original.replace(find, replace, 1))
    try:
        # ---- THE COMPILE GATE. A mutant that does not build is a broken instrument. ----
        build = run(["dotnet", "build", src_proj, "--nologo", "-v", "q"])
        if build.returncode != 0:
            err = [l for l in (build.stdout + build.stderr).splitlines() if "error CS" in l]
            results.append((label, "BROKEN", "did not compile", err[0][:150] if err else ""))
            print(f"[BROKEN - DID NOT COMPILE] {label}\n    {err[0][:150] if err else ''}")
            sys.stdout.flush(); continue

        test = run(["dotnet", "test", test_proj, "--nologo", "-v", "q"])
        text = test.stdout + test.stderr
        # If the TEST project itself failed to build, that is also a broken instrument.
        if "error CS" in text:
            err = [l for l in text.splitlines() if "error CS" in l]
            results.append((label, "BROKEN", "test project did not compile", err[0][:150]))
            print(f"[BROKEN - TEST PROJECT DID NOT COMPILE] {label}\n    {err[0][:150]}")
            sys.stdout.flush(); continue

        failed = sorted({ln.split("[FAIL]")[0].strip().split(".")[-1] for ln in text.splitlines() if "[FAIL]" in ln})
        # A positive presence check: a summary line must exist, or we did not observe a run.
        ran = "Passed!" in text or "Failed!" in text
        if not ran:
            results.append((label, "BROKEN", "no test summary line - the run was not observed", ""))
            print(f"[BROKEN - NO RUN OBSERVED] {label}"); sys.stdout.flush(); continue

        if failed:
            hit = any(expect in f for f in failed)
            results.append((label, "KILLED" if hit else "KILLED-BY-OTHER", "; ".join(failed)[:200], expect))
            print(f"[{'KILLED' if hit else 'KILLED BUT NOT BY THE EXPECTED TEST'}] {label}\n    {failed}")
        else:
            results.append((label, "SURVIVED", "", expect))
            print(f"[*** SURVIVED ***] {label}  (expected {expect} to die)")
        sys.stdout.flush()
    finally:
        io.open(path, "w", encoding="utf-8", newline="").write(original)

print("\n================ SUMMARY ================")
for label, verdict, detail, expect in results:
    print(f"{verdict:<22} {label}")
counts = {}
for _, v, _, _ in results:
    counts[v] = counts.get(v, 0) + 1
print("\ncounts:", json.dumps(counts, indent=None))
io.open(os.path.join(r"C:\Users\soren\AppData\Local\Temp\claude\D--ReposFred-devthrottle\b24f87d3-e91b-434f-bf16-d00e58b25903\scratchpad", "mutation-results.json"), "w", encoding="utf-8").write(
    json.dumps([{"label": a, "verdict": b, "detail": c, "expected": d} for a, b, c, d in results], indent=2))
