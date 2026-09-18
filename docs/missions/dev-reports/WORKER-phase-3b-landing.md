# Worker report - phase 3b, the signed-out landing: the printed address with nobody signed in

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-landing`, cut from `mission/dev-reports-p3b`
after the other four Workers' branches were merged into it. Everything below is committed and pushed on that
branch. Nothing was merged anywhere.

Built to `RULING-phase-3b-the-one-address.md`.

## What was broken, confirmed in the code before anything changed

All three of the brief's findings were read in the branch as it stood, not taken on trust:

1. `DeviceCallback.tsx` ends the sign-in round trip with `navigate(takeEnrollNext() ?? profile.defaultLanding)` -
   a ROUTER navigation. A `next` of `/r/{id}` is therefore resolved inside the shell. The Cockpit's route
   table had no `/r/:id`, so it landed on Not found; the phone's router has a basename of `/mobile`, so it
   asked for `/mobile/r/{id}`, which matched nothing at all.
2. `MobileRedirect.UseMobileRedirect` redirects to the constant `MobileRoot` (`/mobile/`) with no query
   string, so a phone bounced to `/signin?next=...` loses `next`.
3. `DevReportLinkRoute` called `GatewayEndpoints.ResolveReadTenant` and `DevReportStore.Get`, and was
   registered AFTER `AuthMiddleware.Run` in `GatewayHost.cs`, so a signed-out request never reached it.

One more thing was broken and is not in the brief: **every test in `DevReportLinkRouteTests` was already
failing on this branch**, and had nothing to do with the signed-out case. Its `Question` fixture declares one
radio option, and the shape check refuses a question with fewer than two
(`DevReportShapeCheck.cs:368`), so every `PublishAsync` in the class answered 422 and nine tests failed on
that alone. Fixed by giving the fixture a second option, exactly as `DevReportRoutesHostedTests` has. This is
a separate commit so it reads as what it is.

## A - `GET /r/{reportId}` is a public, tenant-free shell picker

`src/CcDirector.Gateway/Api/DevReportLinkRoute.cs` was rewritten. It no longer resolves a tenant, no longer
takes a `DevReportStore` or a `HostedTenantBoundary`, never looks a report up, and has no 403, no 404 and no
response body at all. It decides one thing and 302s:

- a phone User-Agent (`MobileRedirect.IsPhoneUserAgent`) to `/mobile/report/{reportId}`
- anything else to `/report/{reportId}`

`UseDevReportLink(WebApplication app)` lost its two other parameters. In `GatewayHost.cs` the registration
moved from between the tenant scope and the mobile front door to **immediately before the authentication
middleware** - so it now runs ahead of both gates. The comment at the call site says why each ordering
matters and names the tests that fail if either is undone.

**Tests deleted, and why.** Two, both about things this route no longer does:

- `LinkRoute_UnknownReportId_IsA404WithASentenceAndNoRedirect` - there is no lookup, so there is no unknown
  report here and nothing to 404. Replaced by `LinkRoute_AnIdentifierNoReportHasEverHad_IsRoutedExactlyLikeARealOne`,
  which asserts the opposite and goes red if anyone re-adds the lookup.
- `LinkRoute_ReportOfAnotherAccount_IsTheSame404AndDoesNotLeakThatItExists` - there is no tenant, so there is
  no other account here. Replaced by `LinkRoute_AnotherAccountsCredential_GetsTheSameAnswerBecauseNothingHereIsAuthorised`.
  The leak it guarded has not moved: the route answers identically for every identifier, so its answer
  distinguishes nothing. Whether this account may READ the report is decided by the app's authenticated
  `GET /dev-reports/{id}`, which still 404s across accounts (`DevReportRoutesHostedTests`).

`LinkRoute_SignedOutHtmlNavigation_ReachesTheSignInGateCarryingTheLinkItself` was also deleted and is a
DIFFERENT case: it asserted the exact behaviour the ruling says cannot work. Its replacements are the two
`LinkRoute_WithNoCredentialAtAll_*` tests, which assert that no sign-in redirect happens here at all.

**The nosniff test the brief asked me to keep does not exist.** `4e0f4dd29` added the
`X-Content-Type-Options` header to this route and added no test for it. The header protected the plain-text
403/404 sentences, and both sentences are gone - the route writes no body on any path, so there is nothing
to sniff. `WriteSentenceAsync` was deleted with them. The nosniff header on the report's own `/html` route
is untouched and is still pinned by `DevReportRoutesHostedTests`.

Kept and still meaningful: device routing, the escaping, the path shape (`ReadReportId` theory), the
already-decoded path, the mobile front-door ordering, and all four `ReportRecord_*` label tests.

## B - `/report/:reportId` in both shells

One shared landing, in `packages/client-core/src/devreports/DevReportLanding.tsx`. It reads the report
through `getDevReport` - it sits inside each shell's device-key gate, so the call is authenticated - takes
`sessionId` off the record, and hands it to the shell. Both shells mount it and supply only the one thing
that differs, which is where a report lives in that app:

- Cockpit (`apps/cockpit/src/sessions/ReportLanding.tsx`): `/session/{sid}?tab=reports&report={rid}`
- Phone (`apps/mobile/src/pages/ReportLanding.tsx`): `/session/{sid}/reports/{rid}`

Both replace rather than push, so the browser's Back does not bounce through a screen whose only job is to
forward you again.

**The route tables moved out of `main.tsx` in both shells** - `apps/cockpit/src/routes.tsx` (`COCKPIT_ROUTES`)
and `apps/mobile/src/routes.tsx` (`MOBILE_ROUTES`, `MOBILE_BASENAME`). This is the change that makes the
proof worth anything. The tables were literals inside modules that start the whole app when imported, so
every routing test in this repository writes its own little table - and a test written that way would have
stayed green through the exact defect this phase fixes, because the defect was a route that did not exist.
`main.tsx` keeps every startup side effect and hands the array to `createBrowserRouter`; nothing else about
either shell changed in the move.

### The one product decision I had to make, and it is the only thing I would flag

The ruling says a report that is not in this account "shows the 'this report does not appear' state the
viewer already has". **The viewer has no such visible state.** `DevReportViewer` renders nothing on
`notFound` - it calls `onNotFound`, and each shell uses that to navigate back to the report LIST, which the
landing cannot do because it has no session to go back to.

So the landing says one sentence of its own: **"This report does not appear."** It is in client-core, so
both surfaces say the same words, and it never redirects to a guess. If the owner wants different words, or
wants this to land somewhere rather than stop, that is a one-line change in `DevReportLanding.tsx`.

A failed read is a THIRD state and says the real failure ("The report could not be opened: ..."), never
"does not appear" - saying a report is absent because the network was down would be saying something untrue
about the report.

## C - the viewer proof's claim E9

`packages/client-core/browser-tests/dev-report-viewer-proof/run-proof.mjs` read `[data-drn=queued]` and
`[data-drn=sent]` INSIDE the hosted frame. The hosted page draws neither any more, so those two reads would
have thrown on the next run. They now read the APP's two lists - `queuedTexts` and `sentTexts`, from
`T.queuedItem` / `T.sentItem`, which the same function already gathers two lines earlier. The claim is
unchanged: the note either reaches the Gateway or stays visibly queued, and never reads as delivered. The
evidence block records `appShowsFirstAsSent` where it used to record `trayShowsFirstAsSent`.

**Not run.** That proof needs the browser rig; it is the proof Worker's and I did not run it. What I fixed is
a read that could not have worked, not a claim I re-proved.

## D - `MobileRedirect` still drops the query string

Untouched, as instructed. `UseMobileRedirect` still redirects every phone HTML navigation not already under
`/mobile` to the constant `/mobile/`, discarding the query string, so `next` is still lost for every
signed-out phone deep link that goes through it. The design above routes around it - `/r/{id}` now answers
before that middleware, and `/mobile/report/{id}` is already under `/mobile` so the front door ignores it.

## The revert evidence

Every fix below was reverted by changing the exact production line named, the suite was watched going RED
with the message quoted, and then restored. **Everything was committed before the first revert** (`63de915f2`,
`2fe7f8dcf`), and no restore leg was run with `--no-build` - each Gateway run rebuilds.

### The Gateway (`.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReportLinkRoute"`, 19 tests)

**R1 - the registration moved back to AFTER the authentication middleware** (its position before this phase).
RED, 2 of 19, controls green:
- `LinkRoute_WithNoCredentialAtAll_StillRoutesTheDesktopToTheCockpitsLanding`:
  `Expected: "/report/c8219858-..." Actual: "/signin?next=%2Fr%2Fc8219858-..."`
- `LinkRoute_WithNoCredentialAtAll_StillRoutesThePhoneToTheMobileLanding`: the same, `"/signin?next=%2Fr%2F..."`.

That is the defect verbatim: a `next` of `/r/{id}`, which neither router can resolve.

**R2 - the registration moved to AFTER the mobile front door.** RED, 4 of 19:
- `LinkRoute_PhoneNavigation_AnswersBeforeTheMobileFrontDoor`: `Assert.NotEqual() Failure: Strings are equal | Expected: Not "/mobile/" | Actual: "/mobile/"`
- `LinkRoute_PhoneUserAgent_RedirectsToThePhoneAppsReportLanding`: `Expected: "/mobile/report/73efc302-..." Actual: "/mobile/"`
- both `WithNoCredentialAtAll` tests, red again (moving past the front door also moves past authentication).

**R3 - the tenant gate re-added** (`ResolveReadTenant`, 403 when nothing is bound). RED, 7 of 19, every live
route test: `Expected: Found | Actual: Forbidden`. Note this is broader than a signed-out failure and that is
the finding: at this position in the pipeline no credential has been resolved yet, so a tenant gate here
refuses EVERY caller, signed in or not.

**R4 - the report lookup and its 404 re-added.** RED, 7 of 19: `Expected: Found | Actual: NotFound`, including
`LinkRoute_AnotherAccountsCredential_...` and `LinkRoute_AnIdentifierNoReportHasEverHad_...`.

**R5 - the two targets put back to the session-shaped addresses.** RED, 9 of 19, including the three pure
policy tests:
`Expected: "/mobile/report/r-1" Actual: "/mobile/session/x/reports/r-1"` and
`Expected: "/report/r-1" Actual: "/session/x?tab=reports&report=r-1"`.

**Restore leg:** `-Gateway -Filter "FullyQualifiedName~DevReport"` - `Passed: 36, Failed: 0`,
`outcome=Completed total=36 executed=36`.

### The Cockpit (`npm --prefix apps/cockpit run test -- reportLandingRoute`, 4 tests)

**R6 - `/report/:reportId` removed from `COCKPIT_ROUTES`.** RED, 2 of 4:
`expected '/report/b41e77a2-...' to be '/session/7d2f9c10-...?tab=reports&report=b41e77a2-...'` - on both the
cold mount and the callback's `router.navigate`. The address resolves to nothing and the reader stays where
they were.

**R7 - `/report/:reportId` removed from the phone's `DEV_REPORT_ROUTES`.** RED, all 4 of the mobile file,
including `expected undefined to be '/report/:reportId'` from the route-table match.

**R8 - the Cockpit's gate back to a bare `/signin`.** RED, 1 of 4:
`expected '/signin' to be '/signin?next=%2Freport%2Fb41e77a2-...'`. (The phone's gate already carried `next`,
fixed by the apps Worker; this is its Cockpit twin, and now watched.)

**R12 - the Cockpit landing pushes instead of replacing.** RED, 1 of 4: `expected 'PUSH' to be 'REPLACE'`.

### The shared landing (`npm --prefix packages/client-core run test -- DevReportLanding`, 3 tests)

**R9 - a report that does not appear redirects to a guessed session instead of saying so.** RED, 1 of 3:
`TestingLibraryElementError: Unable to find an element by: [data-testid="dev-report-landing-not-found"]`.

**R10 - the landing composes a session instead of reading the one on the record.** RED, 1 of 3:
`expected "spy" to be called with arguments` - `- "7d2f9c10-...", + "b41e77a2-..."`. The session comes off the
record; the address never carries one.

**R11 - a failed read swallowed into "does not appear".** RED, 1 of 3:
`Unable to find an element by: [data-testid="dev-report-landing-error"]`.

Each revert was applied alone and the tree was verified clean against `HEAD` between them. One restore
(`git checkout`) collided with a stale `index.lock` in the shared `.git` mid-loop and left R10 applied under
R11; both were re-run individually afterwards and the messages above are from those clean runs.

## The parked suites found one regression of mine, and it is fixed

`.\scripts\test-local.ps1 -Parked` failed
`CcDirector.Core.Tests.MobileViewportContractTests.TheVisibleViewportHook_ExistsAndIsMountedOnceInTheAppShell`
with "apps/mobile/src/main.tsx does not CALL useVisibleViewportHeight()". **That was mine**: the phone's
gated layout, which mounts that hook, moved into `routes.tsx` with the route table. The behaviour never
changed - the layout still calls the hook and every gated screen still hangs off it - but the guard reads
one named file, and the file it named was no longer where the layout lived.

Fixed in two ways, so the split cannot hide a real break:

- the guard follows the layout: `ShellPath` is `apps/mobile/src/routes.tsx`, and it still requires a real,
  uncommented CALL.
- a SECOND half was added, because the first is only worth something while the entry point renders that
  table: `main.tsx` must really call `createBrowserRouter(MOBILE_ROUTES, ...)`. My first attempt at this was
  a `Contains("MOBILE_ROUTES")`, which passed happily with the mount swapped for a different array - the
  import line mentions the name. It is a line-by-line call check now, the same shape as the hook check, and
  the fake version is recorded in the comment beside it.

**R13 - `main.tsx` mounts a different array.** RED: `apps/mobile/src/main.tsx no longer hands MOBILE_ROUTES
to createBrowserRouter, so the app shell layout in apps/mobile/src/routes.tsx - and the hook mounted in it -
is never rendered.`
**R14 - the hook call removed from `routes.tsx`.** RED: `apps/mobile/src/routes.tsx does not CALL
useVisibleViewportHeight().`
Restored: 9 passed, 0 failed.

This also closes one of the gaps I had listed for myself: that `main.tsx` mounts the table the tests mount
is now asserted, not merely read.

**The other parked failure is not mine, and that was checked rather than assumed.**
`CcDirector.Gateway.Tests.TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)`
fails with `Expected: "no_transcript" Actual: "ok"`. It fails IDENTICALLY on the parent commit `ab176d48d`,
in a worktree cut from it - run and read, not inferred - and nothing in my diff is in its call path.

## What ran

- `.\scripts\test-local.ps1` - 8 projects, all `outcome=Completed`, 2,057 tests, 0 failed. It printed the
  COVERAGE GAP for `Core.Tests`, `Gateway.Tests` and `Gateway.UnitTests`.
- `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"` - 36 passed, 0 failed. Twice:
  once before the revert proofs, and once as their restore leg.
- `dotnet test CcDirector.Core.Tests --filter MobileViewportContractTests` - 9 passed, 0 failed.
- `npm --prefix packages/client-core run test` - 111 files, 1,292 passed.
- `npm --prefix apps/cockpit run test` - 43 files, 363 passed.
- `npm --prefix apps/mobile run test` - 16 files, 86 passed.
- `npm run typecheck` (all four workspaces) - clean.

**The FULL `Gateway.Tests` suite has NOT been run against this change, and it is the gap I would chase
first.** I moved a middleware in the global request pipeline, so the whole Gateway suite is the right reader
for it, and I could not get a run:

- The first `-Parked` attempt was mine to spoil. I rebuilt other test projects in the same worktree while it
  was running, which is exactly what stops a gate run being evidence, so I stopped it rather than quote it.
- The second attempt queued behind another session's `Gateway.Tests` - session `6b9b9276`, worktree
  `devthrottle-dev-reports-p4-director` - which held the machine-wide lock for over fifty minutes. Mine
  aborted while still queued and collected ZERO tests. A run that collected zero tests is a broken
  instrument, and I am not quoting it as anything.

What the spoiled `-Parked` run DID report, offered as an indication and never as a gate: `Gateway.UnitTests`
5,520 passed with the one pre-existing Grok failure above, and `Core.Tests` 4,412 passed with the one
viewport failure that is now fixed.

**The cockpit suite intermittently prints 1-3 "Unhandled Errors" (`ReferenceError: window is not defined`,
after the environment is torn down), and that is NOT mine.** Measured rather than assumed: five runs with
this change produced errors twice; five runs of the SAME suite in a worktree cut from the parent commit
(`ab176d48d`, before any of this) produced errors once. Same shape, same post-teardown React commit, present
on both sides. Every test passes in every run either way.

## What I did NOT prove

- **Nothing was driven in a real browser.** No screenshots, no real Gateway, no real phone. Every claim here
  is a unit or host test. In particular nobody has yet followed a real printed `/r/{id}` from a real phone
  through a real sign-in into a real report - that end-to-end loop is still owed and is the proof Worker's.
- **The viewer browser proof did not run.** I repaired E9's two reads; I did not execute the proof.
- **`DeviceCallback` itself is not in any test here.** I proved the thing it does - a router navigation to
  `/report/{id}` - by calling `router.navigate` on the app's real table, in both shells. I did not drive the
  enrollment callback screen end to end, so "the callback calls navigate with the remembered next" rests on
  reading `DeviceCallback.tsx`, not on a test.
- **The COCKPIT's `main.tsx` handing its table to the router is not asserted.** The phone's is, by the
  viewport contract guard above. The Cockpit has no equivalent guard, so "main.tsx passes COCKPIT_ROUTES to
  createBrowserRouter" rests on reading that one line. It is typechecked, and nothing else in the repository
  reads it.
- **The Gateway's `/report/{id}` shell serving is reasoned, not tested.** `/report` is not in
  `CockpitReactApp.BrowserPageRoots` and there is no top-level `MapGet("/report")`, so a hard navigation
  falls through to the SPA fallback and gets `index.html`. I read that; I did not add a host test for it.
  The phone half is covered by the existing public `/mobile/**` shell surface.
- **A phone that navigates to `/report/{id}` directly is still eaten by `MobileRedirect`** and lands on the
  mobile home screen. Nothing sends a phone there - `/r/{id}` sends it to `/mobile/report/{id}` - but a
  typed or shared desktop address opened on a phone would hit it. That is the item D defect, left in place.
- **The other 4,259 `Gateway.UnitTests`** were not read for anything related; they ran in the parked run only.
