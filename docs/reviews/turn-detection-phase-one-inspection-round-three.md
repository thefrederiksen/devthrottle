AGREE

## Scope and positive evidence

I inspected HEAD `86306a038` against the pre-fix point `505a5a7cf`, reading every product and test file the
three fix commits (`5226f0f99`, `3b14d4255`, `e48d4c7f8`) touch. I read the shipped detector from
`origin/main` at `74da12d36` with `git show`; I did not use the stale shared checkout. The shipped file
still carries the latch-then-write shape at its `MarkActiveFromByte` and `MarkContinuousActive`
equivalents (lines 289 and 345 of the shipped file), so the premise of round two's finding 2 holds on
the tree that ships today.

On the restored tree, the focused Core run executed 70 tests across `ContentTurnRuleTests`,
`TurnDetectionShadowRetentionTests`, `TerminalSettledCaptureTests`, `ContinuousIdleStateTests`,
`TerminalStateDetectorTests` and `StateChangeLogTests`, with 70 passed and zero skipped. The focused
Unit run executed 79 tests across `TerminalContentNoveltyTests` and `SelfDescribingRowMarkersTests`,
with 79 passed. Both runs were made after the mutations below were removed, and the two mutated
product files were hash-checked against HEAD (`c5af44e01`, `c89a5a6da`) before and after.

I ran probes rather than reasoning from the code, matching round two's standard. Every probe was a
temporary test file in `CcDirector.Core.Tests` against a throwaway `CC_DIRECTOR_ROOT`; it was deleted
afterwards, the tree was verified clean, and the restored focused runs above were then executed.
Every mutation was applied to the committed tree and restored with `git checkout --`, and the
working tree was checked clean after each one.

## The seven findings, each closed or not

### 1. The fourth consecutive fault - CLOSED

The closing code is `RestorePendingCheck` at `src/CcDirector.Core/Wingman/TerminalStateDetector.cs:884-899`:
past the bound it calls `MarkActiveFromContent(bytes, ...)` at line 892, which writes `Working` through
`Session.ApplyTerminalActivityState` at line 955 when the detector drives state. That is an actual
state write, not a log line.

I reproduced round two's probe against this version: one real burst, a rule that faults on every call,
nothing afterwards. The turn opened (the `Working` transition was observed), the rule was asked exactly
four times after the settle, the session settled back to `WaitingForInput` one quiet window later, and
no check remained armed. Round two observed the session stuck `WaitingForInput` with the turn lost;
this version opens it. The committed test
`A_check_that_faults_past_the_retry_bound_opens_the_turn_rather_than_losing_it`
(`ContentTurnRuleTests.cs:612`) drives the same sequence and also asserts the rule was asked more
than once, so the fallback stays the last resort.

The off-by-one is gone in all three places round two named. `MaxCheckRetries` is 3 at
`TerminalStateDetector.cs:343` and the comparison at line 889 is greater-than, so faults one, two and
three restore the burst and fault four opens - exactly what the constant's comment now says. The log
line at 891 prints the count it observed rather than the constant.

I re-applied the report's mutation (the `MarkActiveFromContent` call removed, leaving the bare
return): the committed test went red with the report's exact symptom.

What would have to be true for this closure to be wrong: the fallback write would have to fail to
reach `ApplyTerminalActivityState` on the rule-on path, or four consecutive faults would have to be
unreachable. The probe demonstrates both halves.

### 2. The faulted latch on the shipped activation paths - CLOSED

The closing shape is threefold: the quiet-timer arm sits in a `finally` on all three activation
methods (`TerminalStateDetector.cs:585-588` byte path, `963-967` content path, `1011-1015` body
path), and the latch goes back through `ReleaseLatchIfNothingWasWritten` at line 610, which releases
only when the session is not already `Working`.

I attacked the condition itself, hardest first, and every construction I could build recovers:

- A subscriber that faults on EVERY `Working` write, content path, one burst: the write landed
  (the faulting subscriber itself observed `Working`), the latch was kept, the countdown ran, and the
  session reached `Working` then settled to `WaitingForInput`. The retry completed because the kept
  latch makes its `MarkActiveFromContent` early-return, and it wrote the shadow row - the row count
  went from 1 to 2, which is exactly what the `RunContentCheck` comment at line 789 claims ("the
  retry writes one").
- The same persistent fault on the switch-off byte path and on the continuous-idle agent's body
  path: `Working` then `WaitingForInput` on both. These are the paths every Director runs while the
  switch ships off.
- The double fault - the rule faulting on every call AND the state write faulting on every
  `Working` transition, so the fallback's own write faults too: the assignment still lands before
  the subscriber throws, the latch is kept, the finally arms, and the session recovered to
  `WaitingForInput`.
- The same double fault with a subscriber that also throws on the `WaitingForInput` write, so the
  settle's own write faults: still `Working` then `WaitingForInput`. `OnQuietCore` assigns the state
  before its subscribers, so even that fault cannot strand the session.

On the two questions the brief posed: `Session.SetActivityState`
(`src/CcDirector.Core/Sessions/Session.cs:2932-2964`) assigns `ActivityState` and only then invokes
its subscribers, so on the load-bearing path - a subscriber throwing during the write - the read in
`ReleaseLatchIfNothingWasWritten` happens on the same thread that performed the assignment, and a
same-thread read cannot miss its own write. `ActivityState` is a plain non-volatile auto-property
(`Session.cs:584`), so a cross-thread read can in principle be stale - but that read is only reached
when the fault did NOT come from the state write, and in those cases both latch outcomes recover: a
kept latch always has a countdown, because the `finally` arms regardless, and a released latch
re-enters the settled path on the next byte. I could not construct a session that ends up neither
counting down nor able to re-enter the settled path.

On "the state write landed but the session was already Working for another reason":
`SetActivityState` returns before assigning or invoking subscribers when the old state equals the
new one, so a `Working` write into an already-`Working` session is a no-op that cannot fault, and a
kept latch there is correct anyway - the session genuinely owes a settle and the armed countdown
delivers one. The two cases the condition exists to separate are the only two that reach it.

I re-applied the report's mutations: the byte-path `finally` emptied - the byte-path test red; the
body-path `finally` emptied - the body-path test red; both with the report's exact symptom.

What would have to be true for this closure to be wrong: some fault would have to leave the latch
set with no countdown armed, or cleared with the session `Working` and nothing able to settle it.
Every construction above, including faults far harsher than the committed tests drive, recovered.

### 3. The seam's guarantees - CLOSED

`ITerminalSizeRule.Evaluate` returns one `SizeVerdict` carrying the verdict, the magnitude and the
threshold (`TerminalContentNovelty.cs:356-382`), and the detector takes all three from that one call
at `TerminalStateDetector.cs:821-824`. `Measure` is gone from the interface, so a second measurement
cannot be asked for. The committed test `The_size_verdict_and_its_numbers_come_from_one_call`
(`ContentTurnRuleTests.cs:720`) drives a rule whose magnitude alternates above and below its own
threshold and asserts the logged triple is self-consistent and the rule was asked exactly once - the
one-call assertion is what structurally holds this, and I verified it rather than re-applying the
report's two-call mutation.

The ambiguous-frame claim is corrected in place and is true of the code: an ambiguous frame short-
circuits at `TerminalStateDetector.cs:796-811` without invoking either rule, and the comment at
lines 302-317 now says exactly that rather than claiming both candidates are asked on every check.
The `Threshold` property has exactly one production caller, the ambiguous row at line 809, which is
what its interface comment claims.

What would have to be true for this closure to be wrong: a production path would have to gather the
size verdict and its numbers from separate calls, or ask a rule on an ambiguous frame. The interface
no longer has a second measurement method to call, and the ambiguous branch is a short-circuit.

### 4. The retention sweep - CLOSED

The sweep now positively matches the two name shapes this writer composes - `OwnedFileName` at
`TurnDetectionShadowLog.cs:221-229`, anchored at both ends, thirty-two lower-case hexadecimal
characters optionally followed by `.1`, then `.jsonl` - and anything it does not recognise is left
alone and counted once per sweep (lines 256-279). The age check is unchanged; the ownership check is
the new gate.

I reproduced round two's probe and extended it: I placed six aged stranger files in the throwaway
shadow directory - `owner-notes.jsonl` (round two's exact file), an upper-case-hexagonal name, a
`.jsonl.bak` suffix, a prefixed owned-looking name, a `.2.jsonl`, and a `.txt` - beside an aged
owned file and a fresh owned file, then appended. All six strangers survived; the aged owned file
was deleted; the fresh owned file was kept. Round two's probe deleted the stranger on the next
append; this version leaves every one of them alone while still ageing out what it wrote.

I re-applied the report's mutation (the ownership check removed and the `*.jsonl` filter restored):
the committed test `The_sweep_leaves_alone_an_aged_file_this_log_did_not_write`
(`TurnDetectionShadowRetentionTests.cs:169`) went red with the report's exact symptom.

What would have to be true for this closure to be wrong: a name shape the writer produces that the
regex does not match (then a file this log wrote could accumulate past the bound), or a stranger
shape the regex does match. The writer composes names in exactly two places - `Append` and
`RotateIfOversized` - and both shapes are in the pattern; the nine-case table in the committed test
pins the near-misses.

### 5. The rollover - CLOSED as claimed, with one residual stated

Rotation has its own catch at `TurnDetectionShadowLog.cs:179-186` and the append goes ahead
regardless. I drove the failure through the real detector rather than a direct `Append` call: eight
real checks filled a live file, a directory was placed where the rolled predecessor must go, and the
next real check's append rolled past the failure - the row landed (nine rows became ten) while the
predecessor path was still a blocked directory. I re-applied the report's mutation (rotation put
back inside the outer try): the committed test went red with the report's exact symptom, two lines
expected and one found.

The residual, demonstrated rather than inferred: round two also probed an oversized live file held
open with `FileShare.None`, and I replayed that probe. The row is still lost - one line before, one
line after. That is an APPEND failure, not a rollover failure: the rotation fault is caught, the
append is then attempted and fails on the locked file, and the outer catch logs and swallows. There
is no retry and no gap marker in the shadow file, exactly as round two observed. The fix report
claims only that "a failed rollover" no longer costs the row, and that claim is true and is now
pinned by a test; the locked-live-file case was never the claim. I record the residual because the
round-two demonstration used that exact probe, and a reader of the fix report alone would not know
the observation can still be lost when the live file itself cannot be opened.

What would have to be true for this closure to be wrong: a rollover failure that also makes the
live file unwritable would have to be counted as a rollover failure the fix promised to cover. It is
an append failure, and no test or comment claims otherwise.

### 6. The shipped defaults, not just the named constants - CLOSED

I replayed round two's substitution exactly: I changed only the production defaults and left every
named constant alone - 401 milliseconds and four seconds in the detector constructor's fallbacks,
five megabytes, fifteen days and two hours in the live retention properties, and a bare `false` for
the shadow `Enabled` initialiser. Three tests went red, each naming a substituted default:
`A_detector_built_the_way_the_product_builds_one_runs_the_shipped_timings`
(`ContentTurnRuleTests.cs:754`), `The_bounds_are_the_numbers_this_log_ships_with`
(`TurnDetectionShadowRetentionTests.cs:116`) and `The_shadow_log_ships_ON`
(`TurnDetectionShadowRetentionTests.cs:139`). The other 38 focused Core tests and all 79 focused
Unit tests stayed green - which is the point: round two's finding was that all 102 stayed green,
and the fix is that three now do not.

I also checked one live value nobody had pinned: the 500 millisecond body-check throttle in the
watcher. Substituting 5000 milliseconds turned `A_footer_that_repaints_for_ever_costs_no_check_and_no_row`
red, so it is behaviourally held after all.

What would have to be true for this closure to be wrong: a live production default that diverges
from its named constant while every focused test stays green. The substitution above is the exact
test of that, and it now fails three tests.

### 7. The late-versus-never claim - CLOSED

The false sentence is withdrawn in place, with a correction blockquote that states why the
directory listing could never separate the two. The replacement is a positive signal:
`HasPendingCheck` (`TerminalStateDetector.cs:208-218`) exposes whether a check is still armed, the
wait helper reports it when the detector is handed in, and a caller that does not hand one in gets
the honest sentence saying the two cannot be told apart, never a guess
(`ContentTurnRuleTests.cs:1024-1035`). Every call site in the file passes the detector; I checked
each one in the diff.

What would have to be true for this closure to be wrong: the armed signal would have to be
ambiguous - a session with no row ever coming that still reports a check armed. A check is armed
exactly when the callback is scheduled and has not run, and no row can be written for a burst
without a check, so the signal separates the two facts as claimed.

## Sentences claiming something is proven, checked against the code

Round two's two false claims are corrected in place, not softened: the SUPERSEDED note on the
retry bound states the drop happened on the fourth fault and that the burst now opens, and the
CORRECTED note on the flake states the directory listing cannot separate late from never. Both
match the code beneath them.

I re-applied five of the report's own "mutation watched red" claims independently - the fallback's
state write removed, both `finally` arms emptied, the sweep's ownership check removed, rotation put
back inside the outer try - and each turned the named test red with the report's stated symptom.
The report's mutation prose is honest. The one I verified structurally rather than by re-application
is the two-call size verdict (finding 3 above), where the one-call assertion in the committed test
is stronger evidence than the mutation would be.

I looked for new false claims introduced by the fixes and found none. The claims most at risk -
"the retry writes one" for a state-write fault, "the switch-off path stays byte for byte", "the
failure count is deliberately not reset", "the arm is the part that closes it", "Reading the
session's state is the only signal" - each match the code beneath them, and the first and fourth are
demonstrated by my probes, including under persistent faults the committed tests do not drive.

## Residuals that do not reopen a finding

- A persistently faulting rule produces NO shadow rows for the bursts it faults on, and no gap
  marker in the shadow file says the observations are missing - my probe observed the row count
  unchanged while the turn opened. The state harm is fixed; the observation harm remains, and the
  fault is visible only in the file log. No comment claims otherwise.
- A locked live file still costs the row (finding 5 above), as does a directory that cannot be
  created. Both are append failures outside the rollover claim.
- The byte-and-settle race in `OnQuietCore` - the latch is cleared before the `WaitingForInput`
  write completes, so a byte landing inside that write takes the settled path against a session
  that is still `Working` - predates this mission, is unchanged by these fixes, and is on
  `origin/main`. I did not probe it and name it only so the next reader does not rediscover it as
  new.

## Verification boundary

I did not run the full parked Core suite, either Gateway suite, the default gate, live terminal
bytes, or the labelled corpus. My runs are the focused classes round two used plus the mutation and
probe work above. `CcDirector.Gateway.Tests` still has no verdict on this change, which is consistent
with the handoff's instruction to run it immediately before landing - after these inspection fixes
were in, which they now are.
