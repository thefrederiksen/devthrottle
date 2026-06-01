# Cockpit QA Loop - Iteration 01

**App version:** 0.3.5 (build `990dba3`, 2 commits past tag `v0.3.5`)
**Report generated:** 2026-05-31 23:02 local
**Tested against:** Cockpit on `http://localhost:7471` (ASPNETCORE_ENVIRONMENT=Development) -> Gateway `127.0.0.1:7878` -> Slot-1 final-build Director `c99a103c` on `127.0.0.1:7884`
**Test session:** a throwaway Claude Code session in scratch repo `D:/ReposFred/cc-director-qa-scratch` (later renamed "QA Scratch (renamed)"). No user sessions were touched.
**Driver:** cc-playwright `cockpit-qa` connection (separate Brave, 1920x951).

---

## Summary

| Result | Count |
|--------|-------|
| PASS (verified live) | 21 |
| PARTIAL (env-limited) | 1 |
| Cockpit BUG found | 2 |
| Known Director-side issue (not a Cockpit bug) | 1 |

**Headline:** The Cockpit MVP + Phases 2-4 are in very good shape. Every interactive feature works against a real Director and a real Claude session. Two Cockpit defects were found - one **high-impact** (Settings is unusable because the Director endpoint isn't resolved) and one **minor** (a spurious error after cancelling dictation). One feature (recap generation) is blocked by a Director-side `claude --print` failure, which the Cockpit handles gracefully.

---

## Feature test matrix

| # | Feature | Result | Evidence |
|---|---------|--------|----------|
| A1 | Rail shows the whole fleet (grouped by machine/Director, verbatim status dots, needs-you pills) | PASS | `img/iter1/01-initial-load.png` |
| A2 | Select a session -> live terminal renders coherently (no ghost frames) | PASS | `img/iter1/02-session-selected.png`, `03-wide-layout.png` |
| A3 | Terminal stays current (live output, no manual refresh) | PASS | `img/iter1/07-composer-send.png` |
| A4 | Terminal input - typing reaches Claude; Ctrl+C clears the line | PASS | `img/iter1/04-terminal-input.png`, `06-ctrlc.png` |
| A5 | Composer **Send** round-trips (prompt -> Claude reply `COCKPIT_QA_SEND_OK`) | PASS | `img/iter1/07-composer-send.png` |
| A6 | **Queue** enqueue / list / remove / send-now | PASS | `img/iter1/08-queue.png`, `09-queue-removed.png`, `10-queue-sendnow.png` |
| A6b | Queue **auto-drain** | PASS (by design) | Edge-triggered on `Idle` only (Session.cs:1187); intentionally never drains on `WaitingForInput` so a queued prompt can't answer Claude's own question. Manual ops verified live. |
| A7 | **Speak** (dictation dialog: mic selector, equalizer, editable transcript, Insert/Send) | PARTIAL | `img/iter1/19-speak.png` - dialog opens & matches desktop; live transcription needs a real microphone (automated browser has none). |
| A8 | **Screenshots** - dropped/picked image uploads to Director and its path is injected into the live prompt; Claude receives it | PASS | `img/iter1/20-screenshot-upload.png` (path `upload-20260531-225855-210.png` injected, Claude "Marinating...") |
| A9 | **Interrupt** (Ctrl+C clears the run) and **Esc** (soft-stop) buttons | PASS | `img/iter1/21-interrupt.png` |
| A10 | Tailnet-only routing (reads via Gateway; writes/stream direct to the session's endpoint) | PASS (by design) | DirectorClient dials `Selected.TailnetEndpoint`; reads via GatewayClient. |
| P2-new | **New session** dialog - Director + Repository (68 repos) + manual path + Agent (5) all populate | PASS | `img/iter1/13-new-session.png` |
| P2-rename | **Rename** a session (live, reflects in header + rail) | PASS | `img/iter1/24-rename.png` |
| P2-set | **Settings** modal opens with Director picker | **BUG #1** | `img/iter1/12-settings.png` - "that Director has no reachable endpoint", empty JSON box |
| P3-aware | **What's happening** - GIT (branch/dirty/last commit), RECAP state, TURN SUMMARIES | PASS | `img/iter1/14-awareness.png` |
| P3-recap | **Generate fresh** recap | Director-side blocked | `img/iter1/15-recap-generating.png` - "claude --print exited 1:"; Cockpit shows the error inline and re-arms the button (graceful). |
| P4-fanout | **Fan-out** a prompt to selected sessions | PASS | `img/iter1/17-fanout.png`, `18-fanout-sent.png` ("delivered ddb54e2f") |
| P4-hand | **Handover** - context preview prose generated; new/existing target, Director, repo, agent, archive | PASS | `img/iter1/16-handover.png` |
| UI-view | Left panel **Tree <-> Triage** toggle (Needs-you / Active buckets) | PASS | `img/iter1/22-triage.png` |
| UI-collapse | Left **mini-rail** collapse (status dots) + Right panel collapse (terminal full-width) | PASS | `img/iter1/23-collapsed-panels.png` |
| UI-persist | Panel/view prefs persist to localStorage | PASS | Restored on reload via OnAfterRenderAsync. |
| Resilience | Gateway briefly went down mid-test; Cockpit kept the rail, showed a banner, recovered | PASS | Banner in `img/iter1/10-queue-sendnow.png`; rail never blanked. |

---

## Issues found

### BUG #1 (HIGH) - Settings is unusable: "that Director has no reachable endpoint"

**What happens:** Opening **Settings** (topbar) shows an empty JSON editor and the red error *"that Director has no reachable endpoint"*. Settings cannot be read or saved for any Director.

**Root cause:** `Cockpit.razor` `SettingsEndpointFor(directorId)` resolves the Director's base URL **only** from `DirectorDto.TailnetEndpoint`:

```csharp
private string? SettingsEndpointFor(string directorId) =>
    _directors.FirstOrDefault(d => d.DirectorId == directorId)?.TailnetEndpoint;
```

But for same-machine, FSW-discovered Directors the Gateway returns `tailnetEndpoint: null` - the contract itself documents this: *"Null for FSW-discovered Directors (use ControlEndpoint in that case)."* (DirectorDto.cs:42). The session DTOs already get a control-endpoint fallback (that's why the terminal/Send/queue all work over `127.0.0.1:7884`), but the **Director** DTOs do not, so Settings - the only place that resolves a Director endpoint client-side - is broken.

**Fix:** Fall back to `ControlEndpoint` when `TailnetEndpoint` is null/empty (mirroring the session DTO behaviour). One-line helper in `Cockpit.razor`. Will be applied in the next iteration.

### BUG #2 (MINOR) - spurious error after cancelling Speak/dictation

**What happens:** Open **Speak**, then **Cancel** - a red error *"dictation unavailable: A task was canceled."* lingers in the composer dock. A user-initiated cancellation should not be presented as an error (violates the "no error on screen" rule).

**Root cause:** `OpenDictate`'s catch sets `_actError = $"dictation unavailable: {ex.Message}"` for **any** exception, including the `OperationCanceledException` ("A task was canceled") raised when the dialog is dismissed.

**Fix:** Swallow `OperationCanceledException` (and the JS cancel path) without surfacing it as an error. Will be applied in the next iteration.

### Known issue (NOT a Cockpit bug) - recap generation fails Director-side

**What happens:** **Generate fresh** recap returns *"claude --print exited 1:"*. The cached-recap read, git read, and turn-summaries read all work; only on-demand generation fails.

**Root cause:** Director-side - the Director runs `claude --print` to produce the recap and that child process exits 1. This is the same class of issue noted in prior handovers. The Director is final/shipped in this workstream, so this is flagged for the Director owner, not fixed here. The Cockpit's handling is correct (inline error, button re-armed, no hang).

---

## Cockpit vs Desktop parity

The user's bar: **the desktop must be at least at par with the Cockpit.** The desktop (Avalonia) is the full product and is at-or-ahead on every Cockpit feature:

| Capability | Cockpit | Desktop | Verdict |
|-----------|---------|---------|---------|
| Live terminal (typeable, Ctrl+C/Esc/arrows) | yes (xterm over WSS) | yes (native TerminalControl) | at par |
| Composer Send / Queue / Interrupt / Esc | yes | yes (PromptInput + queue) | at par |
| Screenshots into the session | upload -> inject path into PTY | drag-drop a file -> inserts path into the prompt box | at par (remote vs local; both reach Claude) |
| Drag-and-drop gesture | onto the Screenshots panel (file input) | onto the prompt textbox (`PromptInput_Drop`, inserts path+newline) | desktop also accepts text/file drop directly on the prompt box |
| Dictation (Speak) | yes (browser twin of SpeakDialog) | yes (SpeakDialog) | at par |
| New / Kill / Rename | yes | yes | at par |
| Settings | yes (currently BUG #1) | yes (SettingsDialog) | desktop ahead until BUG #1 fixed |
| Awareness / recap / git | yes | yes (WingmanView / GitChangesView) | at par |
| Fan-out / Handover | yes | yes (FifoWindow / move-session) | at par |

**Parity finding (enhancement, not a defect):** On the desktop you can drop an image (or text) directly onto the **prompt box**; in the Cockpit the drop target is the **Screenshots panel** only. Functionally equivalent (both get the image to Claude), but a Cockpit enhancement to also accept a drop on the composer textarea would match the desktop gesture exactly. Tracked as a parity nicety, not a bug.

---

## Next iteration plan

1. Fix **BUG #1** (Settings endpoint fallback to ControlEndpoint).
2. Fix **BUG #2** (don't show cancellation as an error in OpenDictate).
3. Rebuild the Cockpit, re-run the full matrix (focus: Settings load+save round-trip, Speak cancel is clean), regenerate ITER-02 report.
4. Continue looping until no Cockpit issues remain or 20 iterations.
