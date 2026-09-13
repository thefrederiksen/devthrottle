# Code review — issue 2818

**Verdict: CHANGES NEEDED**

Reviewed commit `b8012657d75e8fba9c13421fab97b1fa6ca891d2` in this worktree on 2026-09-13 against [issue 2818](https://github.com/thefrederiksen/devthrottle/issues/2818) and `DESIGN-2818.md`, including its overriding revision. The findings below concern that commit, not later work.

## 1. High priority: a later multiline paste bypasses retained-prompt recovery

**Location:** `src/CcDirector.Core/Drivers/TerminalSubmit.cs:221`, with the bypass at lines 115–120 and the writes at lines 356–368.

The retained-text check is inside `EchoVerifiedInlineSubmitAsync`. After an echo failure under pressure leaves the first prompt in the composer, a subsequent multiline message with bracketed paste enabled goes directly from `SharedSubmitAsync` to `BracketedPasteSubmitAsync`. That route types and presses Enter without consuming or clearing the retained text. Both messages can therefore be submitted as one instruction, the exact second-send corruption the revised design requires this change to prevent. `Session.SendTextAsync` supplies the session's `BracketedPasteEnabled` setting to this dispatcher, so the bypass is reachable from the production send path.

**Reproduced:** using the committed `RecordingSessionBackend`, a Critical reading, and a withheld echo, the first send failed with `ComposerText == "first prompt"`. After enabling immediate echo, sending `"second\nline"` with bracketed paste enabled produced `nextPasteEscapes=0`; the submitted recording contained the first prompt followed by the second payload. The recording also includes the bracketed-paste delimiters because this test backend does not interpret them; the decisive evidence is that the production route writes the new payload and Enter without clearing the retained first prompt.

**Required change:** resolve retained text before choosing any route that writes new text, and clear its mark on successful submission through every route. Add a behavioral test that follows a retained inline failure with a multiline paste and asserts the submitted payload contains only the intended new message.

## 2. High priority: pressure beginning during the echo wait still deletes the prompt

**Location:** `src/CcDirector.Core/Drivers/TerminalSubmit.cs:205` and `:289`.

The memory probe is read once before typing. The same local `pressure` value controls every later screen observation, recovery decision, and final failure reason. If the machine starts with room but becomes short of memory during the four-second echo wait, the timeout still uses the old Normal reading to send Escape and retype. The second failed attempt clears it again and reports that the machine has memory to spare. This leaves the original prompt-loss defect in the transition into memory pressure, even though the probe could now observe the shortage.

**Reproduced:** an injected probe returned Normal at send start, then was changed to Critical after the initial write while echo was withheld. The production submit method made exactly **one** probe read, wrote **two** Escape bytes, and left `ComposerText == ""`. Its exception included `the machine has memory to spare`. Short explicit deadlines accelerated the experiment; they do not alter the recovery branch being exercised.

**Required change:** read current pressure again when the echo deadline expires and before destructive recovery. Preserve the prompt and use the finite observation path when pressure has appeared during the attempt. Test a Normal-to-Critical transition, including actual writes and the delivery reason.

## 3. Medium priority: the lateness diagnostic falsely says no action was refused

**Location:** `src/CcDirector.Core/Machine/TimerLateness.cs:39` and `:54`.

`Of` subtracts the ten-second cadence from the interval between callbacks. `MattersToTheReader` then compares that remainder with the full twenty-second freshness window. For a snapshot accepted at the previous callback and the next callback arriving 25 seconds later, the snapshot is already stale, but lateness is only 15 seconds. `Describe` consequently says `within the staleness window, so no action was refused`. The Gateway's `PushedSessionStore` checks elapsed time since receipt, not lateness after subtracting a cadence.

**Reproduced:** `Of(10s, t, t + 25s)` returned 15 seconds, and `Describe(15s, 10s, 20s)` emitted that incorrect sentence. The existing tests test the same mistaken comparison rather than comparing the result with actual snapshot age.

**Required change:** report lateness independently and derive freshness from the last accepted snapshot's age. Where acceptance history is unavailable, report that uncertainty. Do not assert that actions actually were or were not refused merely from callback timing. Cover the 25-second gap against a twenty-second freshness window.

## 4. Medium priority: skipped and slow pushes still omit the requested memory reading

**Location:** `src/CcDirector.ControlApi/GatewayStreamClient.cs:195`–`:218` and `:282`–`:291`.

The only added memory read in this class is inside the positive-lateness logging branch. The existing disconnected skip, previous-push-in-flight skip, incomplete-push, and slow-push messages carry no memory reading or pressure level. A callback can arrive on time and skip because a previous snapshot build is stalled under pressure; that path still records only the elapsed wait. Similarly, an on-time callback can begin a slow build without producing the new lateness message. This does not meet the issue's requirement for missed re-push ticks to name measured memory pressure, or the design's requirement to add that reading to skipped and slow push reports.

**Required change:** include a current reading and pressure level in those outcome messages, with its timestamp or age. Keep measured pressure distinct from an assertion that it caused the stall. Add a test for an on-time callback that skips an in-flight push under an injected shortage.

## Evidence and limits

- Inspected the changed production files and changed tests, the revised design, the issue, the shared submit dispatch and session delivery boundary, and the Gateway freshness checks. The absolute memory thresholds, Unknown classification, and startup placement of `ThreadPoolFloor.Apply` follow the revised design in the inspected code.
- Ran an external reproduction harness against this worktree's Core project and its committed recording backend. It completed with exit code 0 and produced the observations quoted above. The harness is at `C:\Users\soren\AppData\Local\Temp\review-2818-65a55270\Review.csproj`; run it with `dotnet run --project` followed by that path. These are production-method observations with a simulated terminal and injected memory readings, not a live terminal under paging.
- The harness executed the real Windows probe: `CouldRead=True`, total bytes `16859594752`, available bytes `814649344`, pressure `Critical`. This establishes a successful native call on this Windows machine; it does not establish numerical agreement with an independent memory monitor or validate the other platforms.
- Attempted `powershell -NoProfile -File scripts/test-local.ps1`. It exited **1**, reporting `RESULT: BUILD FAILED - no tests were run.` The build reported 30 warnings and six errors, including `MSB3027` and `MSB3021`, because existing `testhost` process 23472 locked the Core test output assemblies. No existing process was terminated. The parked behavioral suite was not independently executed through the gate in this review.
- Native Linux and macOS reads, native failure injection, cache expiration behavior, live heartbeat timing under worker starvation, and heartbeat timing under actual paging were not measured. The revised design itself acknowledges that a raised worker minimum does not establish survival under paging. This review does not certify those surfaces.

Only this review file was added to the worktree; production code was not changed.
