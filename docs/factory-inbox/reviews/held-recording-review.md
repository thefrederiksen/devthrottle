# Review: a held factory inbox recording is never dropped silently (phase 4 finding 2)

Reviewed on 6 November 2026 by a separate review session (fleet session 0dc2d18d, "Factory Inbox - Reviewer - held
recording never lost silently (4630)"), on machine devlinux. Pull request 4630, worktree `devthrottle-fi-review-4630`,
detached at commit `68497428a`. The branch is cut from pull request 4603's branch, so this review covers ONLY the last
commit, `68497428a` (nineteen files; `git show 68497428a`). The earlier commits were reviewed in
`phase-4-factory-inbox-screens-review.md`.

**Verdict: APPROVE.**

## What the change must do

`MISSION-FINISH.md` section 5, "Phase 4 finding 2": a spoken message whose recording is held (the transcription failed
and a retry is pending) must never be lost while the screen says it was left; the 24-hour sweep that abandons held
recordings must not drop a factory inbox recording without the thread saying so in the Gateway's words. The acceptance
line in section 7: "A factory-inbox recording held past the sweep is shown on the thread in the Gateway's words, never
dropped silently."

## Scope

- The commit in full, read against the code it relies on: `VoiceUploadStore` (the three sweeps, `OpenPending`,
  `Abandon`, `MarkAbandoned`, `EnsureFreshStaging`, the pending-record enumeration), `GatewayDictationEndpoint` (the
  abandon route the client's discard now calls), `DictationTenantGate` and the `GatewayHost` wiring (which partition the
  thread reads and which partition the spoken send writes), `HeldDeliveryDriver` and `VoiceUploadStore.HeldDelivery`
  (whether the Gateway's own delivery driver could pick up a factory recording), `FactoryInboxEndpoints` (the thread
  route), and on the client `factoryInboxVoice.ts`, `FactoryComposer.tsx` and `FactoryInboxThreadView.tsx`.
- Every path I could find that reads, resolves or deletes a PENDING dictation record, checked for whether a factory
  inbox recording can still be resolved or deleted without the thread saying so.
- Whether the named tests fail without the change, by reverting the product lines and keeping the tests.
- Whether anything new broke: the four Gateway test classes the change touches, the touched web test files on all three
  packages, and the type check on all three packages.

Not in scope: the earlier commits of the branch; the second half of the review's finding 2 (resuming saved recordings
at app start on both shells and the list driving every factory's recordings), which the author's answer in the
phase 4 review placed in its own pull request and which `MISSION-FINISH.md` section 5 does not include in this one.
It is still outstanding; see the observations.

## Runs made by this review

All in the foreground, in this worktree, with `~/.dotnet/dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1` and
`-p:EnableWindowsTargeting=true`; web tests with the repository's own vitest from `node_modules/.bin`,
`--minWorkers=1 --maxWorkers=2`.

| Run | Result |
| --- | --- |
| Gateway.UnitTests: `FactoryInboxVoiceEndpointTests`, `FactoryInboxEndpointTests`, `FactoryInboxUndefinedFactoryTests`, `FactoryInboxScreensFoldTests` | 79 of 79 passed |
| client-core `src/factoryinbox` (two files) | 25 of 25 passed |
| Cockpit `src/inbox/InboxView.test.tsx` | 3 of 3 passed |
| phone `src/pages/Inbox.test.tsx` | 4 of 4 passed |
| `tsc --noEmit` in client-core, Cockpit and mobile | all three clean |
| RED PROOF, Gateway: `FactoryInboxVoiceEndpointTests` with exactly two product lines reverted - the sweep's `if (IsFactoryInboxTarget(record.SessionId)) return;` in `VoiceUploadStore.ExpireStalePending` and `uploads.MarkAbandoned(uid, PermanentReason);` in the permanent-error arm of `FactoryInboxVoiceEndpoints.CompleteAsync` - tests kept | 3 failed of 23, exactly the three new ones: `Sweep_HeldRecordingOlderThanADay_IsNotDroppedAndIsLeftOnTheNextRetry` ("Assert.Single() Failure: The collection was empty" - the inbox is empty, the message was lost), `Thread_HeldRecording_IsShownInTheGatewaysWordsUntilItIsLeft` (held is empty after the sweep), `Complete_TranscriptionRefusesForGood_IsResolvedAndNotShownAsHeld`. The other twenty pass. Files restored with `git checkout` afterwards; the tree is clean. |
| RED PROOF, client: `factoryInboxScreens.test.tsx` with `FactoryInboxThreadView.tsx` at the parent commit `178a25730` | 1 failed of 15: `FactoryInboxThreadView_HeldSpokenMessage_PrintsTheGatewaysWordsVerbatim`. |
| RED PROOF, client: `factoryInboxVoice.test.ts` with `factoryInboxVoice.ts` at the parent commit | 2 failed of 10: both `discardFactoryRecording` tests. The new `sendFactoryRecording_HeldPastTheGatewaysSweep_IsReopenedAndLeft` PASSES at the parent too; see the observations. |

## What I checked, and what I found

### Root fix, no fallback

The sweep's one reason to exist is releasing a session lock a dead dictation holds. A factory inbox recording holds no
session lock, so the fix excludes it by what its record names as its target, inside the same per-upload gate, after the
same tenant check. That is the cause removed, not a longer limit papered over it. The thread's `held` list is computed
from the Gateway's own PENDING records on disk (`VoiceUploadStore.PendingFor`), in the same per-account partition the
spoken send registers in (`GatewayHost` opens both through `DictationTenantGate`, which resolves the tenant the same way
`ResolveReadTenant` does), so the thread sees exactly what the voice route holds, never a client's memory of it.

The permanent-refusal arm now resolves the record with `MarkAbandoned` before answering 422, so a recording that can
never be transcribed is not shown as held forever; a repeated register or complete is then answered as a final
"nothing was left", which the client reads correctly since the finding 1 fix.

The client's discard tells the Gateway first (`POST /dictation/{id}/abandon`) and throws when the Gateway refuses;
`FactoryComposer.discard` catches at the event handler and prints the Gateway's sentence through `describeAndReport`.
Nothing catches and hides. One pre-existing client-written sentence remains in `finishTerminal`
(`answer.line ?? "This recording was already dealt with..."`); it is from the finding 1 commit, not this one.

### Can a held recording still be lost silently? Every path I found

- **The 24-hour sweep** (`ExpireStalePending`): skipped for a factory target. Proven red and green above.
- **The tombstone sweep** (`SweepResolvedTombstones`, 30 days): admits only DELIVERED, ABANDONED and ACKNOWLEDGED by an
  explicit allow-list; PENDING is never aged out.
- **The age-only sweep** (`SweepAbandoned`, which deletes any canonical directory past its age without reading the
  state): runs only on `_voiceTurnUploads`, a different store; the dictation staging never sees it (`GatewayHost`
  lines 7236 to 7247).
- **The legacy quarantine** (`QuarantineLegacyUploads`): hosted only, moves aside directories directly under the shared
  base root; a factory recording lives in the account's partition and is never a candidate.
- **The Gateway's held-delivery driver** (`HeldDeliveryDriver`, `VoiceUploadStore.HeldDeliveries`): picks only PENDING
  records whose `Owned` is set. `OpenPending` writes a factory record with `Owned` null, and ownership is taken only on
  the session dictation complete route, so the driver never sees a factory recording and can never resolve one as
  "session exited" or "too old".
- **The abandon route**: user-initiated with the upload id, now the client's discard. On a record already ABANDONED by
  the permanent-refusal arm it writes ABANDONED again and answers 200, so the device copy is deleted only after the
  Gateway agreed. On a DELIVERED record (nothing was heard, or the message was left) it answers 200 with
  `already_delivered`, and deleting the device copy is right. On a refusal (unreadable record) it answers 409, the
  client throws, and the copy is kept.
- **The client**: a register answered terminal is read by `delivered` (finding 1); a complete answered 503, 402 or a
  network error is "held" and kept; a 422 permanent refusal is "refused", kept on the device with the Gateway's
  sentence and a Discard button, and never sent again on its own.

I found no path on which a factory inbox recording is resolved or deleted while the thread says nothing.

### Does anything linger forever that should end?

- A permanent refusal: ABANDONED at once; the 30-day tombstone sweep retires the marker. Ends.
- A held recording that is later left or discarded: resolved by the complete or the abandon. Ends.
- A held recording whose device copy is gone: see observation 1. It is not lost and not silent, but it has no end.

### The client prints only Gateway words (the project instructions, rule 7: the client is dumb)

`HeldMessage` prints `held.line`, `held.atLabel` and `held.tone` exactly as the DTO carries them; `formatInboxTime`
formats the UTC time for the screen, which is layout, not a verdict. `FactoryInboxFold.Held` decides the line, the
label and the tone on the Gateway, from `FactoryInboxWords`. The client test proves it with a line and a label the
Gateway would never send ("A recording is parked behind the quokka (gateway).", "Last poked (gateway)"), printed
verbatim with the tone on `data-tone`. The CSS colours a held line with a fixed dashed warning border and does not vary
by the tone value; that is layout and the Gateway still owns the tone.

### Do the named tests fail without the change?

Yes, both named tests and the permanent-refusal test, with exactly the two product lines reverted and nothing else
(the red proof row above). The failure of the sweep test is the mission's own harm: "Assert.Single() Failure: The
collection was empty" on the inbox after the retry - the message was lost.

## Findings

None that prove harm. The change meets the acceptance line in `MISSION-FINISH.md` section 7.

## Observations (no harm proven; for the Delivery Lead)

1. **A held recording whose device copy is gone never ends.** The held line promises "it is left here when the phone
   or Cockpit that recorded it sends it again." A phone reinstalled, or a browser whose storage was cleared, will
   never send it again. The record stays PENDING for good: no sweep touches it (by design now), the thread shows it as
   held for good, the thread offers no action on a held entry, and the Gateway never retries the transcription itself
   even though, for a recording that reached complete, it holds the whole audio in staging. This is honest (nothing is
   lost, nothing is silent) and it is the consequence of the mission's choice of "never dropped" over "ends", so it is
   not a defect of this change. It is the one case against the brief's "nothing lingers forever that should end". Two
   ways to give it an end, for a product decision: a Gateway-side retry of the transcription for a held recording whose
   audio is whole (the audio is already there), or a Discard on the thread's held entry that calls the same abandon
   route the device's Discard calls.
2. **A refusal the owner can remedy has no retry.** A register refused with `factory_archived` (409) is marked refused
   on the device and never retried, even after the factory is restored; the Gateway holds the record PENDING and the
   thread shows it held; the only exit is Discard. Pre-existing client behaviour (`classify` marks every non-retryable
   refusal), not introduced here, and both surfaces say what happened.
3. **The second half of the review's finding 2 is still outstanding.** Resuming every saved recording at app start on
   both shells, and the list driving every factory's recordings, are not in this commit; the author's answer placed
   them in their own pull request before phase 6. This commit helps meanwhile: the held entry on the thread tells the
   owner where the recording is, and opening that thread mounts the composer, which drives it.
4. **`sendFactoryRecording_HeldPastTheGatewaysSweep_IsReopenedAndLeft` passes without the fix.** It runs against a
   stand-in Gateway and so cannot see the sweep; it only re-proves that a plain register answer leads to a send, which
   the existing tests already cover. Harmless, but its name promises a Gateway behaviour the test cannot check. The
   Gateway test `Sweep_HeldRecordingOlderThanADay_IsNotDroppedAndIsLeftOnTheNextRetry` is the real proof.
5. **`PendingFor` walks the whole per-account staging partition on every thread read**, reading every record file
   under its lock, including every delivered or abandoned tombstone kept for up to 30 days. Its cost grows with the
   account's dictation volume, not with its held recordings. Fine today; worth an index if thread reads become hot.
6. **Small and cosmetic.** A discard of a recording the permanent-refusal arm already abandoned overwrites the reason
   `transcription_permanent` with `user_abandoned`. A discard of a recording whose first register was refused before
   `OpenPending` writes an ABANDONED tombstone for an id the Gateway never held; the 30-day sweep retires it. The
   factory target appears in the session-lock projection as a "locked session id" that matches no session; pre-existing
   since phase 4. The `Completing` semaphore dictionary still grows per message, as the phase 4 review noted.

## Verdict

**APPROVE.** The sweep no longer drops a factory inbox recording; the thread shows every held recording in the
Gateway's words, printed verbatim; a permanent refusal ends the record; a discard reaches the Gateway before the device
forgets its copy; the named tests fail without the change and pass with it; nothing else broke in the suites this change
reaches. Observation 1 is the one open design question and belongs to the Delivery Lead, not to this pull request.
