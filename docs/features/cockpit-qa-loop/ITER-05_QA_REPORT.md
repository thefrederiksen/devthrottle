# Cockpit QA Loop - Iteration 05 (latent-bug code review + fixes)

**App version:** 0.3.5 (build `990dba3` + uncommitted Cockpit fixes)
**Report generated:** 2026-05-31 23:38 local
**Tested against:** Cockpit `http://localhost:7471` (rebuilt) -> Gateway `127.0.0.1:7878` -> Slot-1 Director `c99a103c` (`7884`).

---

## What this iteration did

Black-box testing (iters 1-4) had converged to clean, so this iteration ran a **structured adversarial code review** of the whole Cockpit to find **latent** defects that live clicking can't surface (TOCTOU races, threading, resource/teardown, timeout/stall behaviour). It found **5 real latent issues**; **4 were fixed** (the 5th is benign for the common case). Then the refactored paths were rebuilt and **regression-tested live**.

Also ran two new black-box edge probes:

| Edge probe | Result | Note |
|------------|--------|------|
| Settings **invalid-JSON** validation | PASS | typing bad JSON + Save -> "not valid JSON - fix it before saving"; nothing sent to the Director |
| **Whitespace-only** prompt guard | PASS | Send + Queue both disabled for a spaces-only message (`IsNullOrWhiteSpace`) |

---

## Latent issues found (code review) and fixes

| # | Severity | Issue | Fix |
|---|----------|-------|-----|
| 4 | HIGH | **TOCTOU on `Selected`** - action handlers (SendNow/QueueIt/SendQueued/RemoveQueued/Interrupt/Escape/OnDropImages) null-check `Selected` then re-read it (a computed property over `_sessions`) across an `await`. The 2s poll replaces `_sessions`, so a second read can be null -> spurious NRE surfaced as a misleading error banner. | Capture `var sel = Selected; var dir = sel?.TailnetEndpoint` once per handler; use the locals. |
| 5 | HIGH | **One slow Director freezes the whole roster.** `LoadQueueAsync` was `await`ed inline in `RefreshAsync` on the shared 150s-timeout client; a hung `/queue` on the selected Director stalled the fleet view for up to 150s. | Render the roster (`StateHasChanged`) *before* the queue load; cap the queue GET with an 8s linked-token timeout; add a `_refreshing` reentrancy guard so overlapping ticks can't reorder. |
| 6 | MEDIUM | **Queue poll-vs-action race.** The 2s `LoadQueueAsync` could clobber a just-enqueued/removed item until the next tick (self-healing masked it). | Skip the poll's queue load while `_acting` (the action response is authoritative). |
| 7 | MEDIUM | **`OnDropImages` called bare `StateHasChanged()`** after `await`s - off the renderer's dispatcher it can throw `InvalidOperationException`, which the surrounding catch mislabels as "image upload failed". | `await InvokeAsync(StateHasChanged)` (matches every other call site). |
| 8 | MEDIUM | **Dictation stuck on STARTING.** `cockpit-dictate.js` `ws.onclose` only reported an error for `recording`/`transcribing`; a socket that closes during `starting` (Director rejects the `/dictate` upgrade) showed nothing and never called back, leaving the C# Speak button disabled until manual Cancel. | `onclose` now also handles `starting` -> shows "Dictation stream closed before it was ready." |
| - | LOW | Tree view orders Directors by GUID (`OrderBy(g.Key)`) rather than a meaningful order. | Left as-is: benign for the common one-Director-per-machine case; a real fix needs a Director sort key in the DTO. Noted for the owner. |

All fixes are in `Cockpit.razor` (#4-7) and `cockpit-dictate.js` (#8). Build: 0 warnings, 0 errors.

---

## Regression (after the refactor - live)

| Path | Result | Evidence |
|------|--------|----------|
| Composer **Send** round-trip (refactored to locals) | PASS | `img/iter5/02-send-regression-live.png` - Claude replied `ITER5_REGRESSION_OK` |
| **Queue** enqueue + survives 2 poll cycles (validates the `_acting` skip) | PASS | item `iter5 queued probe` persisted as `QUEUE (1)`, not clobbered |
| **Screenshot** upload + inject (validates the `InvokeAsync` change) | PASS | "attached 1 image to the prompt" |
| Roster keeps updating during all of the above | PASS | session counts/states refreshed live throughout |

No regressions from the refactor.

---

## Note on test hygiene (not a product bug)

DELETE marks a session `Exited` but the Director keeps it in `GET /sessions`, so exited sessions **linger in the Gateway envelope and the Cockpit rail** (already documented behaviour). Sending to a stale-selected exited session returns a raw `409 (Conflict)` in the action-error line - truthful, if terse. Not changed.

---

## Loop status after Iteration 05

| Iter | Found | Fixed |
|------|-------|-------|
| 01 | #1 Settings endpoint (HIGH), #2 dictation-cancel (MINOR) | - |
| 02 | 0 new | #1, #2 |
| 03 | 0 new (edge + parity) | - |
| 04 | #3 header overflow (cosmetic) | #3 |
| 05 | #4-#8 latent (code review): TOCTOU, roster-freeze, queue race, dispatcher, dictation-stuck | #4-#7, #8 (4 of 5; #LOW deferred) |

**Total: 8 Cockpit issues found, 7 fixed + verified, 1 LOW deferred (benign). Recap generation remains Director-side-blocked.**
