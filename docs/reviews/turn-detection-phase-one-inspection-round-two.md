DISAGREE

## Scope and positive evidence

I inspected detached HEAD `9e13e84c5` against `df1c5aea7`, including the two fix commits
`6cd3135de` and `9e838f0a5`. I read the previously shipped detector from
`origin/main` at `0330dbce5` with `git show origin/main:path`; I did not use the stale shared
checkout.

On the restored tree, the focused Core run executed all 24 selected tests from
`ContentTurnRuleTests` and `TurnDetectionShadowRetentionTests`, with 24 passed and zero skipped.
The focused Unit run executed all 78 selected tests from `TerminalContentNoveltyTests` and
`SelfDescribingRowMarkersTests`, with 78 passed and zero skipped. Both restored runs were admitted
by the mutation-proof guard at pinned head `9e13e84c56dcdd450cc1df44f7e5ff3dd0df6e03`.

The green runs do not cover the full local gate, either parked Gateway project, live terminal
bytes, the labelled corpus, or a real two-process rollover. The fault and destructive-boundary
results below came from temporary tests against throwaway storage roots. Every temporary test and
every mutation was removed, its file hash was checked against HEAD, and the restored focused runs
above were then executed.

## Findings

### 1. The retry still loses the turn on the fourth consecutive fault

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:640-665`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:748-788`
- `docs/missions/turn-detection-2026-09-15/build-report.md:410-412`

`OnContentCheckCore` removes the burst before running the check. `RestorePendingCheck` increments
the failure count and restores only while the value is at most three. The fourth failed execution
takes the `> MaxCheckFailures` branch, logs, and returns without restoring the burst. A temporary
probe used one real burst and a rule that faulted on every call. It observed four attempted checks
and the session still `WaitingForInput`: the turn was lost with no later byte to ask again.

The prose is off by one as well. The method comment and report say the burst is dropped after three
consecutive failures, and the emitted log says it failed three times, but the drop happens on the
fourth failed execution. A successful check does reset the counter at line 659, so this is not a
three-fault lifetime limit. The restore catches the disposal race around `Timer.Change`; I found no
ordinary product input that makes the restore itself throw. Neither fact repairs the fourth-fault
loss.

What would have to be true for this finding to be wrong: four consecutive faults would have to be
impossible, or another byte would have to be guaranteed after the dropped one. No fault has been
measured, and neither guarantee exists. Round-one finding 2 is not closed.

### 2. Both switch-off activation paths retain the faulted-latch hazard

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:503-512`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:843-855`
- `src/CcDirector.Core/Sessions/Session.cs:2932-2964`
- `docs/missions/turn-detection-2026-09-15/build-report.md:245-248`

`MarkActiveFromByte` and `MarkContinuousActive` still set `_active` before the state write and arm
the quiet timer only afterwards. `Session.SetActivityState` assigns `Working` before invoking its
subscribers. If an earlier subscriber throws, the outer byte callback swallows the exception, the
timer is never armed, and `_active` remains true. Later bytes take the already-active path. The
session can remain `Working` indefinitely.

I drove this after a completed settle, once through the ordinary byte path and once through the
continuous-idle path. In both temporary probes a state-change subscriber threw during the next
`Working` write. After more than the quiet threshold, both assertions expected
`WaitingForInput` and observed `Working`. These are the paths every Director runs while the switch
ships off.

What would have to be true for this finding to be wrong: every operation after the latch, including
every state-change subscriber, would have to be non-throwing, or an independent timer would always
have to remain armed. The fix report itself calls subscriber failure a real hazard, and the probes
started from a settled session with no saving timer. Round-one finding 2 is also not closed on the
shipped switch-off paths.

### 3. The interface is held, but its claimed every-path and same-measurement guarantees do not hold

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:282-286`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:690-713`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:719-745`
- `src/CcDirector.Core/Wingman/TerminalContentNovelty.cs:334-353`
- `src/CcDirector.Core.Tests/Wingman/ContentTurnRuleTests.cs:581-670`

Comparable frames now use the held row and size interfaces, and the shipped `ChangedSizeRule`
returns its threshold from the same field it compares. I found no remaining direct production call
that duplicates that threshold comparison.

The stronger claims do not hold. The field comment says both candidates are asked on every check,
but an ambiguous check sets both verdicts to true and the magnitude to zero without invoking either
interface. A probe of the first ambiguous check observed zero calls to the injected row rule.

For a comparable frame, the detector calls `GainedContent`, `Measure`, and `Threshold` separately.
The interface does not return one atomic verdict-plus-measurement result. The test stub itself holds
`opens`, `magnitude`, and `threshold` independently. A temporary normal-frame probe supplied
threshold 7, magnitude 42, and false. The emitted row said `ChangedCharacters=42`,
`SizeThreshold=7`, and `SizeRuleOpens=false`. Coming from the same object therefore does not mean
the logged magnitude is the measurement the verdict used, contrary to the interface, test, and
report comments.

What would have to be true for this finding to be wrong: "every check" would have to exclude
ambiguous checks, and every permitted size-rule implementation would have to obey an unenforced
purity and consistency rule across three calls. The current shipped implementation happens to do
so, but the seam was introduced to prevent later scoring and production from drifting rather than
to rely on that coincidence. Round-one finding 3 is not closed as the claimed seam guarantee.

### 4. The retention sweep deletes aged JSONL files it cannot prove it owns

Files and lines:

- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:176-202`
- `src/CcDirector.Core.Tests/Wingman/TurnDetectionShadowRetentionTests.cs:94-112`
- `docs/missions/turn-detection-2026-09-15/build-report.md:319-324`

The sweep enumerates every `*.jsonl` in the directory and deletes on age alone. It does not validate
either file shape the writer creates: `<32 hex>.jsonl` or `<32 hex>.1.jsonl`. The test positively
proves that one GUID-shaped aged file is deleted and one recent GUID-shaped file is kept; it does
not test the destructive boundary. A temporary probe placed aged `owner-notes.jsonl` in the
throwaway shadow directory. The next append deleted it.

A file held open at delete time leans to keep on Windows: `File.Delete` throws, the per-file catch
logs the full path, and the file remains for a later eligible sweep. A normally missing directory is
created before the append. If the directory disappears between creation and enumeration, the
enumeration escapes to the outer append catch after `_lastSweepUtc` has already advanced, so the
row may have landed but the message says append failed and no new sweep is attempted until the
interval expires.

What would have to be true for this finding to be wrong: the directory would have to enforce that
only the two writer-owned canonical name shapes can ever exist. The code does not establish that
invariant; the extension is a deny boundary, not positive ownership. Round-one finding 5 is not
closed.

### 5. Rollover failure drops the current observation, and the lock does not cover another process

Files and lines:

- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:71-73`
- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:140-157`
- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:160-173`
- `src/CcDirector.Core.Tests/Wingman/TurnDetectionShadowRetentionTests.cs:54-91`

Rotation happens before append inside one broad try. When `File.Move` fails, append is skipped; the
outer catch writes only to `FileLog` and returns success to the detector. A temporary probe made an
oversized live file, held it with `FileShare.None`, and appended one record. The file still had one
line rather than two after the handle closed. There is no retry and no gap marker in the shadow
file, so only a healthy `FileLog` sink makes the loss visible.

`Gate` is process-wide, not machine-wide. Two processes can inspect and move the same live and
predecessor paths concurrently. The source permits one process to overwrite the predecessor while
the other is moving or appending, losing a row or older history. I did not reproduce the true
two-process race, so the evidence establishes reachability from the locking boundary rather than a
measured race frequency. I found no retry path that would duplicate a row.

The size claim is also stronger than the algorithm. Rotation tests the old length before appending
the next serialized record. A live file can therefore exceed `MaxFileBytes` by one record until a
later append, and the rolled predecessor can preserve that overshoot. The selected forty-row test
happens to end with the live file below its small test bound; it does not prove the stated per-file
maximum after every append.

What would have to be true for this finding to be wrong: rollover and append would have to be
non-failing, exactly one process would have to be guaranteed for every session file, and record
size would have to be zero or explicitly allowed outside the bound. None is enforced here.
Round-one finding 5 is not closed.

### 6. The new pins cover the named constants, not the live defaults the product consumes

Files and lines:

- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:103-114`
- `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:158-177`
- `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs:42-98`
- `src/CcDirector.Core.UnitTests/Wingman/TerminalContentNoveltyTests.cs:315-323`
- `src/CcDirector.Core.Tests/Wingman/TurnDetectionShadowRetentionTests.cs:116-141`

Direct mutations of the named values do fire. I independently changed 0.80 to both 0.90 and 0.70,
200 to both 201 and 199, 400 ms, 3 s, 4 MB, 14 days, one hour, the environment resolver, the shadow
resolver, and one declared marker. Each relevant test went red. For both behavioral thresholds,
the behavioral assertion fired before the literal assertion in both directions. Removing
`Math.Min` also made the continuous-chatter test fail with no new row, so the cap behavior is live.

But the product consumes separate live fields. I left every named constant unchanged and
substituted only the production defaults: 401 ms and 4 s in the detector constructor, 5 MB, 15
days, and two hours in the live retention properties, and `Enabled=false` instead of
`InitialEnabled`. All 24 focused Core tests and all 78 focused Unit tests still passed. The behavior
tests inject their timings, the retention tests overwrite the live properties, and the shadow tests
explicitly enable the logger. The tests prove the constants and the injected surfaces, not that the
shipped constructor and static initialization consume them.

What would have to be true for this finding to be wrong: at least one test would have to exercise
the omitted production defaults and assert their observable behavior. The 102 green tests under
those substitutions demonstrate that none does. The 0.80 and 200 rule thresholds, the current
marker inventory, and the environment-to-detector rule selection are closed. Round-one finding 6
as a whole is not closed because the shipped timing and shadow defaults can still diverge under a
green focused suite; finding 5's shipped retention values have the same hole.

### 7. The longer wait masks the flake and its diagnostic still cannot distinguish late from never

Files and lines:

- `src/CcDirector.Core.Tests/Wingman/ContentTurnRuleTests.cs:762-786`
- `docs/missions/turn-detection-2026-09-15/build-report.md:428-438`

Changing five seconds to fifteen changes no production behavior and establishes no cause. It makes
the intermittent failure less likely, which is a test-tolerance change, not a diagnosis. The final
message lists the directory's files and sizes, but a target file that still contains exactly the
baseline rows is the same observation whether the new row is late or will never be written.

The `Math.Min` mutation produced that exact diagnostic: the target file existed at its baseline
size and no later row appeared. The message correctly proves the expected row was absent at the
deadline; it does not say why. Thus the report is honest at lines 436-438 that scheduling latency is
not proven, but the claim at lines 434-435 that late and never cannot be confused is false.

What would have to be true for this finding to be wrong: the directory inventory would have to
contain a positive signal unique to a scheduled-but-late callback. It contains no such signal.
This does not reopen a separate round-one finding; it is an unsupported proof claim in the fix
report.

## Round-one closure verdicts

1. Missing settled baseline: CLOSED. Both absent and stale baselines now take the conservative
   path, and the old baseline is cleared after failed settle extraction.
2. Exception windows: NOT CLOSED. The fourth consecutive check fault loses the burst, and both
   switch-off activation methods retain the faulted-latch hazard.
3. Production interface seam: NOT CLOSED as claimed. Comparable frames use the held rules, but
   ambiguous checks bypass them and the size verdict plus measurement is not atomic.
4. Observation ordering: CLOSED. The state decision and quiet-timer arm precede the append on the
   authoritative path.
5. Switch-off shadow cost: NOT CLOSED. Footer-only readable repaints no longer schedule rows, but
   the destructive sweep and rollover are unsafe and the shipped bounds are not tied to their live
   defaults by a failing test.
6. Load-bearing values: NOT CLOSED as a whole. Direct rule thresholds, markers, switch resolution,
   and named literals are pinned; production timing, retention, and shadow-enable consumption are
   still replaceable under a green focused suite.

The evidence does not cover the full parked suites, live bytes, the corpus score, or an executed
two-process rollover race.
