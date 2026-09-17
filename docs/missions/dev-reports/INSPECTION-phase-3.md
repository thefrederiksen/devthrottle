# Inspection - phase 3, the Reports view (Cockpit and phone)

Inspector: independent seat, different family to the Manager. Base: detached at `fd6fc3b36`, diff
`origin/main...HEAD` (phase 2 is on main, so the diff is phase 3). I read the code, not the proof's own
reports; the proof README and handoffs were treated as claims to check, not as evidence.

**Counts: 0 high, 0 medium, 6 low.** The frame trust rules are followed exactly, the isolation claims are
backed by real hostile fixtures and guard-removal runs, and I found no path by which report content reaches
the app, no client code that re-decides a Gateway verdict, and no duplication between the shells that
belongs in client-core. The findings below are edge cases, honesty gaps and hygiene.

Focused runs I made: the four devreports suites under `packages/client-core/src/devreports` (79 tests,
green) and `dotnet test src/CcDirector.Gateway.UnitTests --filter DevReportDeliveryTests` (36 tests,
green). No full suites, no code or test edits.

---

## Findings

### Low 1 - a navigation's load event can consume the host's own load flag at a republish (fail-closed, availability only)

`packages/client-core/src/devreports/frameHost.ts` lines 132-140 (`load`) and 181-189
(`handleFrameLoad`).

Scenario: the report the frame is showing contains a link or meta refresh whose navigation commits a
moment before the controller republishes. `load()` sets `expectingLoad = true` and assigns `srcdoc`; the
committed navigation's `load` event is already queued as a task and dispatches AFTER `load()` returned, so
`handleFrameLoad` consumes the flag and does nothing. The `srcdoc` document's own `load` event then arrives
with `expectingLoad` false, is treated as a navigation the host did not cause, clears the token and closes
the (already closed) port. The new report's `ready` is refused ("no ready is expected") and the page stays
unhosted - Send shows the payload instead of posting - until the next version is published.

Verified by reading the event ordering; not reproduced in a browser. It is fail-closed in the security
direction (nothing is accepted from anyone), and it needs a millisecond race between a republish poll and
an in-flight navigation. The constructor comment shows the same class of race was considered and solved for
the first append (the frame is not appended until its first document is set).

### Low 2 - a pending item can stay "waiting for the app to confirm it has this" with nothing ever resolving it

`packages/client-core/src/devreports/controller.ts` lines 209-226 (`pushGatewayWords`), with the send
path at lines 169-206.

Scenario: the owner presses Send; the page marks every item pending and saves that state; the send request
then fails at the network (the controller answers every item "refused" with the failure words) - but the
frame has reloaded in between (a republish, or an app reload). `pushStatus` returns false with no port, so
the refusal never lands. After the restore the items are pending again, and `pushGatewayWords` resolves a
pending item only when the GATEWAY holds it (line 222). For an item the Gateway never received, nothing
ever clears `pending`: the contract forbids removing it (`remove` keeps pending items,
dev-report-notes.js), so it sits in the queue as "Waiting for the app to confirm it has this" until the
owner presses Send again, which re-sends pending items and does resolve it.

So the note is not lost and the recovery is one press of Send - but the words on the screen are wrong
indefinitely, and the item cannot be removed. Verified by reading the controller, the note script's
`remove`, and the contract's send rules; not reproduced.

### Low 3 - the Gateway's collision refusal was never exercised end to end; it is proven by unit tests only

`packages/client-core/browser-tests/dev-report-viewer-proof/README.md` (results after the E9 fix) says
this itself, and I confirmed it in the evidence: in `evidence/rig-frame-e2e-2026-09-17.json` the E9 step
posts ids `n-9dffe708...` and `n-7de3f19d...` which do not collide, and the session had ended, so both
notes were refused with "This session has ended". The collision path (`DevReportDelivery.SendAsync`, the
`collided` set, `HasSameContent`) was reached by no browser run on this branch. What covers it:
`SendAsync_SameIdDifferentContent_IsRefusedNotReadAsTheStoredItem` and
`SendAsync_SameAnswerIdDifferentOption_IsRefusedAndTheStoredAnswerStands` in
`src/CcDirector.Gateway.UnitTests/DevReports/DevReportDeliveryTests.cs` (both green in my focused run).
I did not revert the refusal to watch them go red (the brief forbids editing code), so the "watched
failing" claim for THESE two tests is the Manager's testimony, not my observation.

I did verify the substance by reading: the refusal answers with
`DevReportItemStates.IdCollisionState` (status `refused`, label "Not sent: a different note already has
this id. Remove this one and write it again"), the page keeps refused items queued and removable, and the
label's instruction (remove and write again) genuinely produces a fresh random id. So the refusal does not
lose a note. One wrinkle: for an ANSWER refused this way, queueing a revised answer replaces the refused
one in place and KEEPS the colliding id, so it is refused again until the owner removes the answer and
answers afresh; the label does say exactly that, and no state is lost.

### Low 4 - private machine paths in committed evidence files

Every viewer-proof evidence JSON records `"rigRoot": "C:\\Users\\soren\\AppData\\Local\\dev-report-proof-rig"`
and the report keys under `c:\\users\\soren\\appdata\\...`, and `evidence/rig-2026-09-17.json` lists the
rig processes' full image paths. The username-qualified profile path is now in the committed record of
this repository. No evidence file on origin/main contains a machine path (checked the existing
browser-proof evidence JSONs on main). Credentials ARE redacted: `run-proof.mjs` lines 104-115 replace the
rig token and the session key with `<rig token>` / `<session key>`, and I found no raw key, token or
authorisation header anywhere under the evidence directories. Severity low: it is the owner's own machine
name in his own repository, but it is newly introduced and the brief asks for it.

### Low 5 - the one component that wires everything, `DevReportViewer`, has no unit test, and the only proof of its wiring is a manual rig

Question 7 of the brief: where could a constant or a stub be substituted and every automated test stay
green? Answer: `packages/client-core/src/devreports/DevReportViewer.tsx`. The unit suites cover
`frameHost`, `controller`, `DevReportList`, `DevReportConversation` and the note script with fakes by
design; the viewer itself - which joins `gatewayDevReportApi`, `DEV_REPORT_NOTES_SCRIPT`,
`new DevReportStateStore(window.localStorage)` and the polling - is exercised only by the browser rig,
which is not part of `.\scripts\test-local.ps1` and runs no web tests. Replacing the real api with a stub,
or passing the wrong script string, keeps the local gate and all unit suites green; only a manual rig run
would notice. The same is true, to a lesser degree, of the shells' mount code (`ReportsTab.tsx`,
`ReportView.tsx`) - covered only by `wingmanTabMount.test.tsx` asserting the tab exists.

### Low 6 - the dry-run evidence duplicates the final evidence

`evidence/dry-run-viewer-4be0f5aee/` commits a second, near-complete copy of the frame and e2e evidence
(about 2 MB of JSON and PNGs, largely the same runs as `evidence/` proper). The README says it is kept
deliberately to show the same E9 failure on the viewer branch alone, and it does show that; it is repo
weight, not a defect. Flagged because the brief asks for hygiene in the diff.

---

## The eight questions, answered

**1. The frame.** Every host follows CONTRACT.md section 4 exactly. Rule 1: the host creates the frame
itself with `sandbox="allow-scripts"` and never `allow-same-origin` (frameHost.ts constructor); no markup
outside the host decides the sandbox. Rule 2: `buildFrameDocument` (lines 63-82) concatenates the host's
own closed head and THEN the report's bytes untouched - it never searches the report for an insertion
point; a `<head>` inside a comment cannot move the policy (the first unit test proves the comment case).
Rule 3: the policy is written with a nonce freshly drawn per `load()`; nonce and token are validated
hexadecimal, the injected script is refused if it contains `</script`, and the theme is attribute-escaped
so it cannot end the script element. Rule 4: on the window only a well-formed `ready` from the frame's own
`contentWindow`, with the CURRENT token and exactly one port, is accepted, and the token is then forgotten
- one ready per load; every other shape is refused and reported. Rule 5: everything after that travels on
the port from that ready, in both directions; the host never posts to the frame's window. Rule 6: a load
the host did not cause clears the token and closes the port. The browser proof checked each of these
against real hostile fixtures, and each guard removal (`mutations.mjs`) turned its claims red in both apps
(`RED CONFIRMED` in all five evidence files; I read `frame-2026-09-17-no-policy.json` and
`...load-keeps-port.json` myself - with the policy removed the hostile script RUNS but still reads
nothing, and the token is not seen, because the note script removes its token attribute synchronously
during head parsing, before any report script can observe it). The one weakness I found is the load-event
race in Low 1, which fails closed. I found no path where report content lands before or outside the
host's head, no reused nonce or token, and no message accepted without the token.

**2. Can the report reach the app?** No, on every axis I could think of. Storage and cookies: the frame's
origin is opaque (sandbox without allow-same-origin), so its own and the parent's storage and cookies all
throw SecurityError - proven live in the frame evidence ("blocked: SecurityError" on all five reads) and
by the proof script's `inside` probe. The Gateway with the owner's credentials: `default-src 'none'`
gives `connect-src 'none'`, so no fetch from the frame at all; the device key lives in the app's
localStorage, which the frame cannot read; the frame's origin is not the app's, so cookies would not
travel anyway. The parent DOM: cross-origin, blocked (proven). Nested frames: `frame-src` falls back to
`default-src 'none'`, so a report cannot even nest a frame (F5's fixture tries). Popups and top
navigation: no `allow-popups`, sandbox blocks them (F6, with a beacon watching for success). A
`javascript:` link needs the nonce it cannot have. Network exfiltration by styles or images:
`img-src data:; font-src data:` only, and F5 watched the recording server for anything the published
hostile report tried to load - zero hits.

**3. Client is dumb.** No client code decides what an item state means. The list renders title, status,
version and counts verbatim; the conversation renders `statusLabel` verbatim with only a `data-status`
attribute for tests; the tray shows the host's words verbatim; the controller forwards the Gateway's
status and statusLabel untouched and pushes back only the Gateway's own words for items that changed.
The unit tests deliberately use absurd labels ("zz-held-QX", "Gateway words #1 (not a client label)"),
so any client-authored replacement would fail them. The words a CLIENT does write are the contract's
own: the failure label when a send REQUEST fails (the Gateway ruled on nothing; the contract's word for
that is "refused" and the label says why), and the page's own "waiting for the app to confirm" for
pending. No conditional in any view branches on what a status means; `devReports.css` and both shells'
CSS key nothing off a status value.

**4. One implementation.** Nothing is duplicated between the shells that belongs in client-core. The
list, the viewer, the frame host, the controller, the state store, the note script and the conversation
all live once in `packages/client-core/src/devreports`, and both shells mount them
(`ReportsTab.tsx`, `Reports.tsx`, `ReportView.tsx`); each shell contributes only its frame, its layout
CSS (verified: both CSS diffs are layout only) and what opening a report does. The note script is bundled
from its one file via `?raw` (`notesScript.ts`), not copied. (Two copies of a tiny `formatTime` helper
exist inside client-core itself, in `DevReportList.tsx` and `DevReportConversation.tsx` - same package,
not a shell duplication; not worth a finding.)

**5. E9.** Item ids are now `n-`/`a-` plus 32 hex digits from 16 bytes of
`crypto.getRandomValues` (dev-report-notes.js `nextId`), unguessable and unique across browsers; when
`crypto` is missing the note is not queued - no fallback to a counter. The unit tests prove two fresh
models never share an id. The Gateway (`DevReportDelivery.SendAsync`) refuses an id it already holds
with different content (`HasSameContent` compares every field the page sends - kind, text, anchor JSON,
questionId, question, optionValue, optionLabel, comment - I checked the field list against
`protocol.ts`; it is complete), and an identical resend stays idempotent (known id, same content: not
re-stored, answered with its CURRENT state). The refusal does not lose a note: the page keeps it queued,
non-pending, with the Gateway's reason, and it can be removed and re-sent. Caveats: Low 3 (the end to
end run never reached the refusal; the session had ended) and the answer-revision wrinkle in Low 3.
Edge case, documented and unreachable from the real clients: within ONE batch a duplicate id with
different content is answered with the first occurrence's state ("within one batch the first occurrence
counts") - the page never sends the same id twice in one send.

**6. State held by the host.** I could not construct a loss. The store is keyed per report AND version;
`save` keeps only the current version; `load` seeds a new version from the newest earlier one, so a
republish carries the queue, the draft, the answer drafts and the scroll across (proven end to end as
E6: scroll 900 kept, "half typed on the phone" kept, frame reloaded in place with a fresh token).
An app reload restores from the same storage (unit-proven with a second controller on the same
storage). Concurrent version loads are guarded (`loadingVersion`) and a stale lower-version load is
discarded by the `page.version <= current` check. State cannot land on the wrong report: keys carry the
report id, and both shells key the viewer (`key={reportId}`) and reset the open report on a session
change (`ReportsTab`'s effect). What CAN happen is Low 2 (a pending item that no push ever resolves) and
the sub-keystroke race inherent to any reload (a `state-changed` posted in the same instant the port
closes is dropped; every input event saves immediately, so at worst the last keystroke's save is lost).

**7. Constants and stubs.** The proofs are unusually hard to fake: the mutations must match the built
bundle text or the run fails ("a removal that did not happen must never read as a red"), each removal
must go red in BOTH apps, R1 pins the running Gateway to this tree's commits, R4 reads the composed
prompt out of the Gateway's own database, and the E9 check reads the posted ids out of the app's own
request. The weak seam is Low 5: `DevReportViewer`'s wiring is untested by anything automated. A
constant substituted there (a stub api, a wrong script string) keeps everything green that runs in the
local gate, because the local gate runs no web tests at all.

**8. Hygiene.** Non-ASCII: none in the whole code diff (the one byte found is the pre-existing UTF-8 BOM
on `DevReportDelivery.cs`, already at origin/main). Attribution: none - no tool or assistant names in
the new code, comments, fixtures, README or the 60 branch commit messages (the "Claude Code" mentions
that exist are product references - the rig genuinely starts a Claude Code session - and `CLAUDE.md`
rule references; the commit that matched my grep matched the ordinary word "regenerated"). Tokens and
session keys: redacted on every printed line and evidence file, and I found none raw. Private paths: Low
4. Machine names: none beyond the path in Low 4 (`SOREN_NORTH` etc. appear nowhere).

---

## What I did NOT check

- I did not run the browser rig myself (`rig.ps1 build/up/session` plus `run-proof.mjs`); it starts a
  Gateway, a launcher and a Director and the machine is short of memory. I read the harness
  (`run-proof.mjs` all stages, `mutations.mjs`, the fixtures) and cross-read the committed evidence
  JSONs against the claims (34/34 pass, all five removals `RED CONFIRMED`, E9's ids not colliding). The
  screenshots I did not open.
- I did not revert the E9 fixes to watch the new Gateway tests go red; the brief forbids editing code,
  and that check is the Manager's testimony (Low 3).
- I did not run the parked Gateway suites, the web tests of either shell, or any Python suite; the
  viewer touches no Python, but `Gateway.UnitTests` beyond the 36 delivery tests I ran is unverified by
  me (the delivery change is additive and its blast radius is inside `DevReportDelivery`).
- I did not verify that `PHASE-3-REPORT.md` exists - it is not in this diff; the Manager's handoff
  requires it, and the Architect should check the branch carries it before landing.
- I did not test the phone app on a real phone or a real mobile browser; the proof records this same
  limit itself (Chromium headless only).
- The Director-side prompt path (what the session does with a delivered note) is phase 2 territory and
  was assumed, not re-inspected, except where the proof reads the composed prompt out of the database.
