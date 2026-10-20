# Mail Desk, phase 7: review of the Mail Desk page (the client half)

Reviewer seat, Mail Desk mission (issue #4213), phase 7. Written 19 October 2026 for the phase 7 Tech Lead.

## Scope

**What was reviewed:** the diff `origin/mail-desk-phase-7...HEAD` in the worktree `devthrottle-md7-pagecheck`, detached at
`origin/mail-desk-p7-page` (`438b7d78e`, confirmed equal to the remote branch after a fetch). 23 files: the shared page in
`packages/client-core/src/maildesk/`, the Cockpit's frame, route, rail entry and icon, the phone's frame, route, drawer
entry, styles and the not-found refactor in `StaleShellRecovery.tsx`, and all their tests.

**Read against:** `docs/qa/mail-desk/phase-7/CONTRACT.md` sections 1 to 3; repository rules 1, 3, 7 and 8; the precedent
`apps/cockpit/src/mentor/useMentorEntry.ts`; the sibling reads `teams/mentorClient.ts`, `teams/teamsClient.ts` and
`skills/skillsClient.ts`; the shells' error banner (`apps/cockpit/src/components/ErrorBanner.tsx`) and the error display
scans (`errorDisplaySites.test.ts` in both shells and client-core).

**Gateway code read to answer the questions (not under review):** `Api/MailDeskGate.cs`, the Mail Desk mapping in
`GatewayHost.cs` (around line 5600), `Cockpit/CockpitReactApp.cs` (the page fallback and the browser-navigation
middleware), and the Gateway half on `origin/mail-desk-p7-fold` (`MailDeskChannelFold.cs`, `MailDeskEndpoints.cs`
`ListChannels`) to confirm the envelope and row the client reads are the shape the Gateway sends.

**What was run, all green:**
- client-core: `src/maildesk` and `src/errors` - 7 files, 125 tests passed.
- Cockpit: `src/maildesk`, `AppShell.test.tsx`, `routes.test.tsx`, `errorDisplaySites.test.ts` - 4 files, 51 tests passed.
- phone: `pages/MailDesk.test.tsx`, `src/components`, `errorDisplaySites.test.ts` - 17 files, 67 tests passed.
- `npm run typecheck` across the workspaces: clean.

**What was not reached:** the full suites of the three packages (the Tech Lead's own run is the only evidence for those
counts); a browser against a running Gateway, so nothing here proves how the page looks at 1440 or 390 wide - that is the
section 4 QA report's job; the Gateway half itself, beyond confirming the shape.

## Verdict

**No findings.** Within the scope above, nothing breaks and nothing needs to change before merge. The answers to the five
questions follow, so the next seat can check the reasoning rather than trust the verdict.

## The five questions

**1. "404 or the app's HTML shell" counted as switched off.** A real case, not a fallback that hides a defect.
- On a Gateway that has the Mail Desk, the routes are mapped unconditionally into `MailDeskGate.Group` (no `if` around the
  mapping), so an account that is off gets the gate's 404 and the HTML shell cannot reach this read. The
  browser-navigation middleware cannot serve it either: it needs `Accept: text/html`, and the client sends
  `application/json`.
- A 200 HTML answer reaches this read only from a Gateway built before phase 1 (18 October 2026), whose page fallback
  (`CockpitReactApp.Map`, `MapFallback("{*path}")`) answers every unmapped GET with `index.html`. Client and Gateway ship
  in one image, so the realistic route there is a rollback (`rollback-hosted-gateway.yml`) while a phone's service worker
  still holds the newer shell. That Gateway truly has no Mail Desk, so "off" is the true answer: the owner sees no entry
  and, at `/mail-desk`, the ordinary "Page not found". Throwing instead would draw the entry, show an error banner about
  `text/html`, and file a client error every 15 to 60 seconds for a Gateway that is working exactly as built.
- Any other unexpected content type is still thrown, and a non-JSON body labelled JSON is still thrown, so a broken answer
  cannot look switched off. The same ruling is already in `mentorClient.ts` and `teamsClient.ts`.
- One note, not a finding: contract section 1 names only the 404. The code comment explains the second case well; the
  contract could say it too, so the next reader of the contract alone is not surprised.

**2. Rule 7 and the client-written captions.** Harmless layout. "Mode", "Machines", "Signed in" and "Last check" are fixed
names of the four slots and never change with state; every word that does change (the sentence, the tone) is the
Gateway's, rendered verbatim, and the tone reaches the stylesheet as a data attribute only. No field is hidden for looking
empty, nothing is branched on a provider, mode or decision word, and the client's only own sentences are its loading line
and its error sentences for an answer that breaks the contract, which is the client's job. The row and envelope the client
validates match the Gateway's `MailDeskChannel` record and `ListChannels` envelope field for field.

**3. The phone's PageNotFound.** An unknown address behaves exactly as before: `RouteRecoveryBoundary` still computes
`willSelfHeal`, still purges the service worker and caches and reloads once, still shows "Updating..." during it, and the
message after is the same text through the new `RouteMessage`. The refactor only moved the markup. `PageNotFound` does not
reload, which is right: the Mail Desk being off is the Gateway's answer, and no fresh shell can change it. The only visible
difference from an unknown address is that transient "Updating..." frame (and, on both shells, a brief "Loading..." line
before the Gateway answers); neither tells an account whether it is on or off, so it does not weaken the gate's purpose.

**4. The banner drawn from a class name, and reporting.** Every shown error is reported once. The page's one error site is
`setError(describeAndReport(SURFACE, "read the Mail Desk", err))`, which the scanner counts as reported, and the phone's
scan walks `packages/client-core/src`, so the site is in a gated scan. An aborted read (unmount, or the double effect in
development strict mode) returns before reporting. Drawing the class rather than the Cockpit's `ErrorBanner` loses only the
optional Retry button, which this page does not use, and keeps `role="alert"`. The entry question files its own
`user_visible: false` report for the same failure under a different surface - that is the Mentor entry's design, and it is
not a second report of a shown error.

**5. The entry.** Hidden only on the "off" answer, offered and asked again on every other failure on `retryDelayMs`,
aborted and its timer cleared on unmount, and a settled answer kept for the page load (account switching reloads the app,
and the Mail Desk switch belongs to the account the device key is bound to, which a team switch does not change). The
Cockpit inserts the entry directly after Factories (a constant list, so the position cannot drift); the phone drawer draws
"Mail Desk" at `/mail-desk`; both routes mount the same client-core page, which reads the same route, so rail, drawer and
page cannot disagree about what the Gateway answered within one page load.
