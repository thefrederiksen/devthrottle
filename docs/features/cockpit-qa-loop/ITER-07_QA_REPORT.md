# Cockpit QA Loop - Iteration 07 (composer drag-drop - explicit requirement)

**App version:** 0.3.5 (build `990dba3` + uncommitted Cockpit fixes)
**Report generated:** 2026-05-31 23:56 local
**Tested against:** Cockpit `http://localhost:7471` (rebuilt) -> Gateway `127.0.0.1:7878` -> Slot-1 Director `c99a103c` (`7884`).

---

## Why this iteration

Re-reading the goal, one requirement was called out by name: *"the drag and drop **into the text box** and see that it works."* The Cockpit only accepted image drops on the **Screenshots panel** - the composer **text box** had no drop handler, so dropping an image there did nothing (the desktop, by contrast, accepts a drop directly on the prompt box). That is a real, explicitly-requested gap, so this iteration **implemented composer drag-drop** and verified it.

## Issue #9 (FEATURE GAP - explicitly requested) - composer text box now accepts image drops

**Before:** dropping an image on the composer textarea did nothing.

**Fix (desktop parity):**
- `cockpit-composer-drop.js` (new) - a document-level (event-delegated, idempotent) handler: on dragover of files over `.composer` it allows the drop + highlights; on drop it routes the image files into the composer's hidden `<InputFile>` and dispatches `change`, so they run the **same** server-side `OnDropImages` upload+inject path the Screenshots panel uses. Text drops fall through to the textarea's native behaviour; non-image files are ignored.
- `Cockpit.razor` - added the hidden `.composer-drop-input` InputFile inside `.composer` (always rendered with the composer, so it works even when the right panel is collapsed); wired `init()` once in `OnAfterRenderAsync`; updated the placeholder to "...drop an image here"; disposes the JS module ref.
- `app.css` - `.composer-drop-input{display:none}` + a `.composer.drag-over` dashed-accent highlight.

**Verified live:**

| Check | Result | Evidence |
|-------|--------|----------|
| Handler wired on load (`window.__cockpitComposerDropInit`, sink present) | PASS | `wired:true, hasComposer:true, hasSink:true` |
| Drop an image **on the composer text box** -> uploads + injects path | PASS | `img/iter7/01-composer-drop.png` - "attached 1 image to the prompt"; path `upload-20260531-235443-235.png` injected into the live prompt |
| Existing **Screenshots panel** drop still works (no regression) | PASS | "attached 1 image to the prompt" |
| Placeholder now advertises the gesture | PASS | "...Shift+Enter for newline; drop an image here)" |

(cc-playwright can't perform a native OS file-drag, so the drop was exercised with a real `File` in a dispatched `DragEvent`+`DataTransfer` - the exact path a browser drop takes into the handler.)

---

## Updated parity note

Iters 1-6 listed "composer-textarea drop" as a desktop-parity *enhancement*. It is now **implemented** - the Cockpit composer accepts image drops just like the desktop prompt box. The two screenshot entry points (composer drop + Screenshots panel) both reach Claude via the identical upload path.

---

## Loop status after Iteration 07

| Iter | Found | Fixed |
|------|-------|-------|
| 01-06 | 8 issues + 1 LOW recommendation | 7 fixed, 1 LOW deferred |
| 07 | #9 composer-drop gap (explicitly requested in the goal) | **#9 implemented + verified** |

**Total: 9 Cockpit issues/gaps found, 8 fixed/implemented + verified, 1 LOW deferred.** Files changed this loop: `Cockpit.razor`, `wwwroot/js/cockpit-dictate.js`, `wwwroot/js/cockpit-composer-drop.js` (new), `wwwroot/app.css`. Build green.
