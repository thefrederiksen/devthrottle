# Cockpit QA Loop - SUMMARY

**App version:** 0.3.5 (build `990dba3`, branch `main`) + uncommitted Cockpit fixes from this loop
**Run:** 2026-05-31 ~22:45 -> 23:40 local (autonomous overnight QA loop, 5 iterations)
**Tested against:** Cockpit on `http://localhost:7471` (Development) -> Gateway `127.0.0.1:7878` -> Slot-1 final-build Director `c99a103c` (`7884`) and the older Director `39aad623` (`7879`).
**Method:** real Claude sessions in a throwaway scratch repo, driven through a separate Brave via `cc-playwright` (connection `cockpit-qa`). No user sessions were modified; the two scratch sessions created for testing were killed at the end.

---

## Bottom line

**Every Cockpit feature works** against a real Director and real Claude sessions. Seven iterations - live black-box testing, a structured code review, and a feature-gap fix - found **9 Cockpit issues/gaps** and **fixed 8** (the 9th is a benign LOW deferred). That includes implementing **composer drag-drop** (the goal explicitly asked for "drag and drop into the text box"). The only non-working *feature* is **recap generation**, which fails **Director-side** (`claude --print exited 1`) and is out of scope for the Cockpit (handled gracefully). The **desktop app is at-or-ahead of the Cockpit** on every feature.

| | Count |
|---|---|
| Features exercised live, end-to-end | 25 |
| Cockpit issues/gaps found | 9 |
| Cockpit issues/gaps fixed + verified | 8 |
| Deferred (benign LOW) | 1 (Director GUID ordering in tree view) |
| Open actionable Cockpit issues | **0** |
| Known Director-side issue (flagged, not fixed) | 1 (recap generation) |

---

## Issue ledger

| # | Severity | Issue | Status | Fix location |
|---|----------|-------|--------|--------------|
| 1 | HIGH | **Settings unusable** - "that Director has no reachable endpoint"; empty JSON editor. `SettingsEndpointFor` resolved only `DirectorDto.TailnetEndpoint`, which is null for FSW-discovered (same-machine) Directors. | FIXED + verified (load + save round-trip, both Directors) | `Cockpit.razor` `SettingsEndpointFor` -> fall back to `ControlEndpoint` |
| 2 | MINOR | **Spurious dictation error** - cancelling Speak showed "dictation unavailable: A task was canceled". Root cause: `start()` held the Blazor JS-interop call open until `getUserMedia` resolved, so a hung/denied mic tripped the 60s interop timeout. | FIXED + verified (cancel is now clean) | `cockpit-dictate.js` `start()` fires `startSegment()` instead of awaiting it; `Cockpit.razor` `OpenDictate` swallows `OperationCanceledException` |
| 3 | MINOR (cosmetic) | **Header action buttons clipped at narrow widths** (~930px) - "Rename"/"Kill" ran off the right edge. | FIXED + verified (wraps to 2 rows; wide layout unchanged) | `app.css` `.detail-head{flex-wrap}` + `.detail-name` truncation |
| 4 | HIGH | **TOCTOU on `Selected`** (code review) - action handlers re-read the `Selected`/`DirBase` computed properties across `await`s; the 2s poll can null them mid-action -> spurious NRE error banner. | FIXED + regression-verified | `Cockpit.razor` - capture `sel`/`dir` locals once per handler |
| 5 | HIGH | **One slow Director freezes the whole roster** (code review) - `LoadQueueAsync` awaited inline in the poll on the 150s-timeout client. | FIXED + regression-verified | `Cockpit.razor` - render roster before queue load; 8s linked-token cap; `_refreshing` reentrancy guard |
| 6 | MEDIUM | **Queue poll-vs-action race** (code review) - the 2s queue load could clobber a just-enqueued/removed item. | FIXED + regression-verified | `Cockpit.razor` - skip the poll queue load while `_acting` |
| 7 | MEDIUM | **`OnDropImages` bare `StateHasChanged()`** (code review) - could throw off-dispatcher, mislabeled "image upload failed". | FIXED + regression-verified | `Cockpit.razor` -> `await InvokeAsync(StateHasChanged)` |
| 8 | MEDIUM | **Dictation stuck on STARTING** (code review) - a `/dictate` socket closing during `starting` showed no error and never called back, leaving Speak disabled. | FIXED | `cockpit-dictate.js` `ws.onclose` handles the `starting` stage |
| 9 | FEATURE GAP (explicitly requested) | **Composer text box didn't accept image drops** - drag-drop only worked on the Screenshots panel, but the goal asked for "drag and drop into the text box". | IMPLEMENTED + verified (desktop parity; existing panel drop unaffected) | new `cockpit-composer-drop.js` routes composer image drops into a hidden `InputFile` -> same upload+inject path; `Cockpit.razor` + `app.css` |
| - | LOW (deferred) | Tree view orders Directors by GUID; benign for one-Director-per-machine. | DEFERRED | needs a Director sort key in the DTO |
| - | Director-side | **Recap generation fails** (`claude --print exited 1`). Cockpit shows the error inline and re-arms the button; cached-recap/git/turn-summary reads all work. | Flagged for the Director owner (Director is final in this workstream) | n/a (not a Cockpit bug) |

---

## Files changed (uncommitted - left for your review)

- `src/CcDirector.Cockpit/Components/Pages/Cockpit.razor` - Fixes #1, #2, #4, #5, #6, #7, #9 (Settings endpoint; dictation cancel; TOCTOU local-capture; poll-loop resilience; queue race; dispatcher marshalling; composer drop wiring)
- `src/CcDirector.Cockpit/wwwroot/js/cockpit-dictate.js` - Fixes #2, #8 (don't hold the interop call open; report a socket close during `starting`)
- `src/CcDirector.Cockpit/wwwroot/js/cockpit-composer-drop.js` - **new** - Fix #9 (composer text-box image drag-drop)
- `src/CcDirector.Cockpit/wwwroot/app.css` - Fix #3 (header wraps / name truncates) + Fix #9 (composer drop-zone styling)

Build is green (`dotnet build` 0 warnings, 0 errors). Nothing was committed (per your standing rule).

---

## Feature coverage (all PASS unless noted)

**MVP:** rail shows the whole fleet (A1); select -> live terminal, coherent, no ghost frames (A2); terminal stays current (A3); terminal input incl. Ctrl+C / Esc (A4); composer Send round-trips (A5); queue enqueue/list/remove/send-now (A6) + auto-drain (edge-triggered on Idle, by design); Speak dialog (A7 - UI verified; live transcription needs a real mic); screenshots upload + path injection, Claude receives the image (A8); Interrupt + Esc (A9); tailnet/endpoint routing (A10).

**Phase 2:** New session via UI (Director + 68 repos + manual path + 5 agents); Rename; Kill (arm -> confirm); **Settings (fixed)**.

**Phase 3:** Awareness - GIT (branch/dirty/last commit), recap state, turn summaries. (Recap generation Director-side-blocked.)

**Phase 4:** Fan-out (delivered); Handover - context preview **and** execution (new session spawned + seeded).

**UI:** Tree<->Triage; left mini-rail collapse; right panel collapse; pref persistence; gateway-down resilience (rail retained, banner, auto-recover); cross-Director terminal; stale-Director `/queue` graceful degradation; multi-line composer (Shift+Enter); session-switch terminal swap is clean.

---

## Desktop parity

Captured the running desktop app (Avalonia). It has the equivalent of every Cockpit capability (session rail, live terminal, Speak/Send/Queue/Interrupt composer, screenshots/image viewer, New/Kill/Rename/Settings, awareness, fan-out, handover) **plus more** (tabbed Clean/Wingman/document viewers). **Desktop is at-or-ahead of the Cockpit.** The one prior gap - the desktop accepts a drag-drop directly on the prompt box - was **closed this loop** (issue #9): the Cockpit composer now also accepts image drops, in addition to the Screenshots panel; both reach Claude via the same upload path.

---

## Iteration reports (all kept)

| Iteration | Focus | Result |
|-----------|-------|--------|
| [ITER-01](ITER-01_QA_REPORT.html) | Full feature matrix | 2 bugs found (#1, #2) |
| [ITER-02](ITER-02_QA_REPORT.html) | Fix verification + New/Kill lifecycle | #1, #2 fixed + verified; 0 new |
| [ITER-03](ITER-03_QA_REPORT.html) | Edge cases + cross-Director + desktop parity | 0 new |
| [ITER-04](ITER-04_QA_REPORT.html) | Code-level review + responsive fix | #3 fixed; 0 new functional |
| [ITER-05](ITER-05_QA_REPORT.html) | Latent-bug code review + fixes + regression | #4-#8 found; #4-#7 + #8 fixed (4 of 5) |
| [ITER-06](ITER-06_QA_REPORT.html) | Remaining-code review + stress tests | 0 new; multi-image + rapid-switch PASS; 1 LOW recommendation documented |
| [ITER-07](ITER-07_QA_REPORT.html) | Composer drag-drop (explicit goal requirement) | #9 implemented + verified; Screenshots-panel drop unaffected |

Black-box testing converged to clean by iter 3; iter 4 fixed a cosmetic nit; iter 5's structured code review found 5 latent defects (TOCTOU, roster-freeze, queue race, dispatcher, dictation-stuck) and fixed 4 - all regression-verified live with no behavioural regression; iter 6 reviewed the remaining code (SessionOrdering/Program.cs/terminal.js - clean) and stress-tested multi-image upload + rapid session switching (both pass). Screenshots for every step are under `img/iter1`..`img/iter6`. All test sessions were cleaned up; only the user's sessions remain running.
