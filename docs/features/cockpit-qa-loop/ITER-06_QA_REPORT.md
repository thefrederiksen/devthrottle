# Cockpit QA Loop - Iteration 06 (remaining-code review + stress tests)

**App version:** 0.3.5 (build `990dba3` + uncommitted Cockpit fixes)
**Report generated:** 2026-05-31 23:48 local
**Tested against:** Cockpit `http://localhost:7471` -> Gateway `127.0.0.1:7878` -> Slot-1 Director `c99a103c` (`7884`).

---

## What this iteration did

Reviewed the Cockpit code not covered in Iter 05, and ran interactive stress tests that the earlier happy-path runs didn't cover.

### Code review of the remaining files

| File | Verdict |
|------|---------|
| `SessionOrdering.cs` | **Clean.** `InDesktopOrder` = `SortOrder` then `CreatedAt` tiebreak; triage buckets put OnHold last with documented precedence over red. No off-by-one, matches its tests. |
| `Program.cs` (DI / HttpClient) | One **LOW** observation (below). Otherwise correct: Gateway client 30s, Director client 150s (deliberate, for recap). |
| `cockpit-terminal.js` | Disposal is clean (wantOpen guard, clearTimeout, ro.disconnect, term.dispose); reconnect at 1200ms; mirrors PTY cols. No defect. |

**LOW (documented, not fixed):** interactive one-shot Director calls (Send/Queue/Interrupt/Escape/Rename/Kill) inherit the 150s client timeout, so a hung Director could keep one composer button disabled (`_acting`) for up to 150s. The *roster* freeze (the impactful case) was fixed in Iter 05; this interactive case is lower impact (one button, rare) and capping it cleanly would mean threading a timeout token through every handler - deferred to avoid churning already-verified code. Recommended future fix: run `Act(...)` under a linked-token timeout (recap doesn't go through `Act`, so it's unaffected).

### Interactive stress tests (all PASS)

| Test | Result | Evidence |
|------|--------|----------|
| **Multi-image upload** (2 files in one drop) | PASS | `img/iter6/01-multi-image.png` - "attached 2 images to the prompt"; BOTH saved paths injected on the prompt line, space-separated |
| **Rapid session switching** (QA-ITER6 -> exited session -> QA-ITER6 -> cross-Director -> QA-ITER6, fast) | PASS | `img/iter6/02-rapid-switch.png` - terminal pane disposes/reconnects cleanly each switch; no ghost frames, no stale content, no crash; composer resets |
| Switching to an **exited** session and back | PASS | shows "[stream closed: session exited]" for the dead one, live terminal for the live one - correct |

**New Cockpit issues found this iteration: 0** (one LOW documented as a future recommendation).

---

## Loop status after Iteration 06

| Iter | Found | Fixed |
|------|-------|-------|
| 01-05 | 8 issues (3 black-box + 5 code-review) | 7 fixed, 1 LOW deferred |
| 06 | 0 new (remaining code review + stress tests) | - 1 new LOW documented as a recommendation |

The Cockpit continues to hold up: every interactive path is solid, the code-review surface is now substantially covered, and the only outstanding items are LOW/by-design or Director-side. See `SUMMARY.html` for the consolidated ledger.
