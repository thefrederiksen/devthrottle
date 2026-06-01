# Cockpit QA Loop - Iteration 02 (fix verification)

**App version:** 0.3.5 (build `990dba3` + uncommitted Cockpit fixes)
**Report generated:** 2026-05-31 23:12 local
**Tested against:** Cockpit `http://localhost:7471` (rebuilt with fixes) -> Gateway `127.0.0.1:7878` -> Slot-1 Director `c99a103c` (`127.0.0.1:7884`)
**Driver:** cc-playwright `cockpit-qa` (Brave, 1920x951).

---

## What this iteration did

Applied and **live-verified** the two Cockpit defects found in Iteration 01, then exercised the New-session and Kill lifecycle end-to-end (which had only been partially covered in Iter 01).

| Item | Iter-01 status | Iter-02 result | Evidence |
|------|---------------|----------------|----------|
| BUG #1 - Settings "no reachable endpoint" | BUG | **FIXED** - Settings now loads the full Director config JSON; Save round-trips ("saved; the Director re-applied its settings"); rail intact | `img/iter2/01-settings-fixed.png`, `02-settings-saved.png` |
| BUG #2 - spurious "dictation unavailable: A task was canceled" after cancelling Speak | BUG | **FIXED** - cancelling Speak leaves a clean composer, no error banner | `img/iter2/03-dictation-cancel-clean.png` |
| P2-new - create a session via the New-session UI (manual repo path) | partially tested | **PASS** - new session created + auto-selected with live welcome terminal | `img/iter2/04-new-session-created.png` |
| P2-kill - Kill (arm -> Confirm) | not tested | **PASS** - two-step arm guard; session went `Exited`, dropped from rail, selection cleared to "Select a session" | `img/iter2/05-kill-armed.png`, `06-killed.png`, `07-killed-after.png` |

**New Cockpit bugs found this iteration: 0.**

---

## The fixes (what changed, and why)

### Fix #1 - Settings endpoint resolution (HIGH)

`Cockpit.razor` `SettingsEndpointFor` now falls back to `ControlEndpoint` when `TailnetEndpoint` is null/empty:

```csharp
private string? SettingsEndpointFor(string directorId)
{
    var d = _directors.FirstOrDefault(x => x.DirectorId == directorId);
    if (d is null) return null;
    return !string.IsNullOrWhiteSpace(d.TailnetEndpoint) ? d.TailnetEndpoint : d.ControlEndpoint;
}
```

This mirrors the behaviour the session DTOs already had (which is why the terminal/Send/queue worked all along over `127.0.0.1:7884`) and matches the `DirectorDto.TailnetEndpoint` contract note: *"Null for FSW-discovered Directors (use ControlEndpoint in that case)."*

### Fix #2 - dictation cancel is not an error (MINOR + a latent timeout)

Two changes:

1. **`cockpit-dictate.js`** - `start()` no longer `await`s the mic/socket boot before returning. The dialog is fully event-driven, so the Blazor JS-interop call now completes the moment the UI is wired:

   ```js
   // was: await startSegment();
   startSegment().catch(() => {});
   ```

   Holding the interop call open until `getUserMedia` resolved meant a hung or denied microphone (no device, or a user ignoring the permission prompt) tripped Blazor's **60-second interop timeout**, which surfaced a bogus "A task was canceled" error on a dialog that was actually working. This is the real root cause - it would bite real users whose mic permission stalls, not just the automated test browser.

2. **`Cockpit.razor`** `OpenDictate` - defensively swallows `OperationCanceledException` so a teardown/cancel never renders as a red error banner.

---

## Full regression snapshot (re-confirmed still green)

Rail (tree/triage/collapse), select->terminal, terminal input (type/Ctrl+C/Esc), composer Send/Queue/Interrupt/Esc, screenshots upload+inject, New session, Rename, **Settings (now fixed)**, Kill, awareness (git + recap-state + turn-summaries), Fan-out, Handover preview, panel persistence - all PASS. The only non-working capability remains **recap generation**, which fails Director-side (`claude --print exited 1`) and is outside the Cockpit (handled gracefully).

---

## Next iteration plan (ITER-03)

Adversarial edge-case sweep to confirm there are no remaining Cockpit issues: multi-line composer (Shift+Enter), rapid session switching, terminal reconnect after a stream drop, empty/last-session states, and the "Director has no tailnet endpoint" branch. Fix anything found; otherwise declare the Cockpit clean.
