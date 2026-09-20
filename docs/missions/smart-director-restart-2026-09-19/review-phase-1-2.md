# Review - Smart Director Restart, phase 1, task 2: the restart cycle over the real drain (defect #3169)

Reviewed commit `f83dffc72` on branch `smart-restart/p1-cycle-real-drain`, cut from `2092941b6` on
`origin/main`. The branch's merge base with the current `origin/main` is still `2092941b6`; the two
commits `origin/main` has gained since are Gateway-side and touch none of these files, so the
three-sided diff read for this review is the right basis.

## Scope

What I read, in full:

- The whole diff `git diff origin/main...HEAD`: `DirectorDrainRestartStep.cs` (new),
  `ControlApiHost.cs`, `DirectorRestartCycle.cs`, `DirectorRestartCycleMessages.cs`,
  `DrainPaths.cs`, `DrainDirectorDialog.axaml.cs`, `DirectorDrainRestartStepTests.cs` (new),
  `DirectorGatesCollection.cs` (new), `DirectorRestartCycleTests.cs`, `DirectorDrainTests.cs`.
- Around the diff, to check the claims: `DirectorRestartCycle.cs` in full, `DirectorDrain.cs`
  (the gate, the constructor, `RunAsync`, `RunCoreAsync` through the finish, `Preflight`,
  `DrainOptions`), `IDrainSessionControl.cs` and `IDrainWorkspaceSink.cs` (the two real seams the
  factory builds), `DrainTestRig.cs` in full (both fakes), the two Gateway-side test files that still
  carry the old sentence, issue #3169, the mission document, the Developer's mandate and the
  Developer's proof. The mission, mandate and proof were read from the sibling checkout as this
  mandate directs.
- A search of the whole tree for the swept claim ("carries no drain", "NoDrainOnThisBuild", the old
  issue reference) and for every test class that takes a drain gate, a cycle gate or builds a
  `ControlApiHost`.

What I ran, all in the foreground in this worktree:

- The mission's check: `dotnet test src/CcDirector.Gateway.UnitTests --filter
  "FullyQualifiedName~Drain|FullyQualifiedName~Restart"` - **428 passed, 0 failed**, which matches
  the proof and the Tech Lead's own run.
- `dotnet build src/CcDirector.Avalonia` and `dotnet build src/CcDirector.Gateway.Tests` - both
  succeed with no warnings and no errors, as the proof claims. The check itself builds neither, so
  these two were run because the diff touches the first and the proof names the second.

What I could not reach:

- **The mutation proof was not re-run.** Every mutation changes tracked files and this mandate
  forbids changing any tracked file in this worktree. The mutation runs were read as testimony and
  checked for coherence against the test code (below), not re-executed.
- **The baseline of 418 on untouched `origin/main` was not re-run**, for the same reason.
- **A started host with a Gateway client.** As the proof says, a Gateway client only exists on a
  host that dials out, so no test shows a live host answering "can drain: yes", and
  `StartRestartCycle` end to end was never executed. The wiring it depends on was read.
- **Nothing ran against a real Director, a real session or a real Gateway.** Tests and builds only,
  as the mandate allows.
- The parked suites and the full default gate were not run; only the mission's check plus the two
  builds named above.

## What I looked hardest at, and what I found on each

**1. The verdict mapping, and any path that asks the launcher without a ready drain.** The step maps
exactly three outcomes: `ReadyToRestart` becomes `Drained` with the record name; not ready becomes
`Blocked` with the record name and the drain's own reason; `DrainAlreadyRunningException` becomes
`Blocked` with the other drain's sentence and no record name, because this one touched nothing. A
factory that builds no drain becomes `Unavailable`. The cycle asks the launcher only after `Drained`
AND a non-empty record name AND a second capability check; `Drained` with no record name is itself
treated as blocked, and a verdict the cycle does not know also stops it. I found no path where the
launcher is asked over a drain that did not end ready. The drain's own readiness rule (every seat
terminal and every flagged seat verified gone) is unchanged and its tests still run.

**2. A drain that throws or is cancelled part way.** Both process-wide gates are always released:
the cycle releases its claim in a `finally` in `RunAsync`, and the drain releases its claim in a
`finally` in its own `RunAsync` - including on the preflight refusal, which throws after taking the
gate, and on cancellation. A throw reaches the cycle's one entry-point catch, which reports the
request as abandoned with the error's own words. One nuance, examined and left to the seat that
built the work (see finding candidate A below): on the throw path the abandoned request names no
record even when the drain stored one.

**3. Do the tests watch the real cycle and the real drain, or a hand-built outcome?** Tests 1 to 9
run the real cycle over the real step over the real drain on the `DrainTestRig`; only the two ends
(sessions, Gateway) are faked, with the same fakes the drain's own tests use. The order test reads
one list written by both fakes, asks the launcher inside the fake at the moment it is called, and
asserts the launcher event is last, single, and that zero sessions were running at that moment - so a
reverted wiring cannot keep it green through the cycle. The two host tests watch the wiring on a
real `ControlApiHost`, which no test of the step alone can do. The mutation proof is coherent with
this reading: mutation A (the stand-in put back on the host) reddens exactly the two host tests,
mutation B (the step refusing like the stand-in) reddens the seven tests that reach a verdict
through the cycle, and mutation C turns the availability answer upside down and reddens the two
availability tests plus the host eligibility test. Every one of the eleven new tests is accounted
for by at least one mutation. I could not re-run the mutations myself (see scope), but the claims
match the code I read line by line.

**4. The new xUnit collection.** It is needed and it hides nothing. The drain gate and the cycle
gate are static per process, which is right for the product (one Director per process) and wrong for
a test runner that runs classes side by side; the new tests take both gates, so the three classes
that ever take either gate are now serialized. I searched the whole test project: no other class
constructs a `DirectorDrain`, a `DirectorRestartCycle` or calls `StartRestartCycle`, and the restore
tests use a different gate of their own. The one real interaction in the product - a desktop drain
holding the Director while a cycle starts - is not hidden by the collection; it is tested head-on by
test 8, and the fix in `7aa760fa0` (the parked drain is released in a `finally`) is present in the
code.

**5. The `DrainDirectorDialog.axaml.cs` and `DrainPaths.cs` changes.** They were needed. The cycle
must mint a record name; moving the desktop's existing rule into `DrainPaths.WorkspaceIdFor` and
having both doors call it is the minimal way to keep one rule, which the mission's design (section
5.3, item 9) asks for. The moved rule is byte-for-byte the old one, and the desktop dialog now
delegates. The Avalonia project builds clean.

**6. Comments that still claim this build carries no drain.** Swept. The seam comment, the verdict
comment, the availability comments and the `DrainAvailable` contract comment now say what is true.
The two remaining sentences that carry the old words are in `DirectorRestartRequestServiceTests` and
`DirectorRestartRequestRouteTests`, and I read both: they hand the Gateway a fake Director's answer
worded as an OLDER Director in the fleet would word it, and assert the Gateway refuses it. They
claim nothing about this build and they must keep the old sentence to keep proving that case.

## Findings

None. Within the scope stated above, I found no defect that must change.

Two candidates were weighed and rejected, recorded here with the reasoning so the Developer and the
Tech Lead can answer them rather than rediscover them:

- **Candidate A - a drain that throws names no record in the abandoned request.** The proof raises
  this itself. The only throw path that stores a record and then throws is the preflight refusal
  (the drain writes the refusal into the record, then rethrows), and on that path nothing was
  messaged and nothing was closed, the record itself carries the same refusal in its integrity
  block, and the request carries the error's own words. The paths where seats WERE already closed
  and a throw can still happen are save failures, which mean the Gateway is unreachable - and then
  the abandoned report cannot land either, record name or no record name. So the missing pointer is
  confined to a path where there is nothing urgent to find. My reading: not a defect; naming the
  record there would be a small improvement, and the choice belongs to the Developer.
- **Candidate B - the record name is stamped to the minute.** Two drains started inside one minute
  mint the same name. The rule is the desktop's existing rule, moved not changed, and the mission
  wants both doors on one rule. The drain already refuses loudly in exactly this window - its
  preflight refuses a document directory from an earlier run with a sentence that names the
  collision - and a name clash on the Gateway is a loud refusal too, not a silent overwrite. A
  second cycle inside the same minute also has to get past the one-at-a-time gates first. My
  reading: an inherited, deliberate narrowness, honestly recorded in the proof; not a defect in this
  change.

## Verdict

Within the stated scope - the diff, the code around it, the mission's check run by me (428 passed,
0 failed), and the two extra builds - the change does what issue #3169 asks: the restart cycle
drains through the real drain before the launcher is asked, the stand-in and its claim are gone,
the order is proved on the real pieces, and the gates are safe on every path I could reach. Nothing
found.
