# Worker brief - phase 3b, the signed-out landing: the printed address must work when nobody is signed in

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-landing`, cut from `mission/dev-reports-p3b`
AFTER the other four Workers' branches were merged into it. Your worktree is yours alone. Commit and push
as you go. NEVER merge to main and never touch `mission/dev-reports-p3b` itself.

Read first, and read it whole - it is short and it is the entire reason you exist:
`docs/missions/dev-reports/RULING-phase-3b-the-one-address.md`.

## THE WHY

`cc-dev-reports open` now prints one address, `<gateway>/r/<report id>`. Signed in, it works: a phone lands
on the phone's report screen, a desktop lands on the Cockpit's Reports tab, both inside the report. **Signed
out, it lands nowhere**, on either surface, and the owner asked for exactly this case. The two Workers who
built the halves never got the ruling that says how - the fleet message channel refused every send - so the
code on the branch is built to a design that cannot work. That is what you are fixing.

## WHAT IS BROKEN, PRECISELY - verify each one yourself before you change anything

1. `DeviceCallback` ends with `navigate(takeEnrollNext() ?? profile.defaultLanding)` - a ROUTER navigation.
   So a `next` of `/r/{id}` is resolved inside the shell: the Cockpit has no `/r/:id` route (it lands on
   Not found) and the phone's router has a `/mobile` basename (it asks for `/mobile/r/{id}`, also nothing).
   The Gateway route is never requested a second time.
2. `MobileRedirect` sends every phone HTML navigation not already under `/mobile` to `/mobile/` **with no
   query string**, so a phone bounced to `/signin?next=...` loses `next` before it can be remembered.
3. `DevReportLinkRoute` is authenticated and looks the report up, so a signed-out request is bounced by
   `AuthMiddleware` into (1) and (2) rather than reaching the app at all.

## THE WORK

**A. `GET /r/{reportId}` becomes a public, tenant-free shell picker.** Rewrite
`src/CcDirector.Gateway/Api/DevReportLinkRoute.cs`: no `ResolveReadTenant`, no `DevReportStore`, no 404 and
no 403. It decides ONE thing - which app opens this - and 302s:

- a phone User-Agent (`MobileRedirect.IsPhoneUserAgent`) to `/mobile/report/{reportId}`
- anything else to `/report/{reportId}`

Register it before the AUTHENTICATION middleware as well as before the mobile front door, and prove BOTH
orderings with a test, because either reorder silently breaks the address. It needs no account because it
authorises nothing: it echoes back an identifier the caller already held. Keep the existing tests that still
mean something (device routing, the ordering, the path shape, the already-decoded path, nosniff); delete the
ones about tenant and 404 and SAY SO in your report, with why.

**B. `/report/:reportId` in both shells.** The one landing that needs only a report id. It reads the report
through the API - it is inside the device-key gate, so the call is authenticated - takes `sessionId` off the
record, and lands in that report: the Cockpit by going to `/session/{sid}?tab=reports&report={rid}`, the
phone to `/session/{sid}/reports/{rid}`. A report id that is not in this account shows the viewer's existing
"this report does not appear" state - never a redirect to a guess. Both shells already have everything you
need; do not invent a second viewer.

Signed out then works by itself through each shell's own gate, because `next` is now an in-shell route the
router navigation at the end of the round trip can actually resolve. Check that it does.

**C. The viewer proof's claim E9 is broken by phase 3b and will fail on its next run.**
`packages/client-core/browser-tests/dev-report-viewer-proof/run-proof.mjs` reads `[data-drn=queued]` and
`[data-drn=sent]` INSIDE a hosted frame (around line 755). The hosted page no longer draws those - that is
the whole point of this phase. What E9 means to prove is still true and still worth proving: a note either
reaches the Gateway or stays visibly queued, and never reads as delivered. Point it at the APP's queued and
sent lists (`T.queuedItem` / `T.sentItem`, which it already uses two lines earlier).

**D. Do NOT fix `MobileRedirect` dropping the query string.** It is a real defect and it loses `next` for
every signed-out phone deep link, not only reports - but it is outside this phase and the design above
routes around it. Leave it, and write one line in your report saying it is still there.

## PROOF - the owner asked for proof, so this is the point

- The route: phone goes to `/mobile/report/{id}`, anything else to `/report/{id}`; it answers with NO
  account bound to the request (that is the change - a test that fails if anyone re-adds the tenant gate);
  it answers before the authentication middleware and before the mobile front door.
- The shells: mounting `/report/{id}` lands in that report in each shell; signed out, mounting it sends the
  browser to that shell's sign-in with `next=/report/{id}`; and a `next` of `/report/{id}` resolves to the
  report after the callback - drive the REAL route tables, not one written for the test.
- **Revert each one and watch it go red** with the reported symptom, then restore and watch it green.
  Commit before any revert (`git checkout --` restores HEAD, not your uncommitted fix), and never run the
  restore leg with `--no-build`.
- Run `.\scripts\test-local.ps1`, then
  `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"` - the Gateway suites are PARKED
  out of the default run, so a green default says nothing about them. That suite takes a machine-wide lock
  and may WAIT behind another run; that is by design, not a hang. Also the client-core, cockpit and mobile
  test commands.

## DONE MEANS

Pushed on `mission/dev-reports-p3b-landing`, with `docs/missions/dev-reports/WORKER-phase-3b-landing.md`
holding what you changed, which tests you deleted and why, the revert evidence per test (the line, the red
message), and what you did NOT prove. Tell your Manager in ONE line - fleet messages truncate at the first
newline, and in this phase they mostly do not arrive at all, so the file is the real report.

Do not guess. If something is genuinely undecidable, say so in your report rather than inventing a product
decision.
