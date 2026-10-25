# Alerts - Phase 4 (mount in both shells, remove the panels, the QA run) review

Reviewer: a separate review session (2926a8f4), 25 October 2026. Pull request 4473, branch
`alerts/phase4-mount`, one commit (`431bf45cf`) on top of origin/main `6b0decd48`, read as
`git diff origin/main...HEAD` in the worktree `D:\ReposFred\devthrottle-alerts-p4`. The tree was 0 commits
behind origin/main when read (`scripts\check-tree-freshness.ps1`), and `git status` was empty before and after
everything below. No tracked file was edited; `npm ci` was not run.

## Verdict

**Approve. No finding proves harm.** Phase 4 does what section 6 asks of it, the mission's goals 1 to 8 hold
on this branch as far as reading, running and the screenshots show, and goal 9's hosted run is owed after the
deploy, not by this pull request. Seven observations are recorded for the Delivery Lead; none blocks the merge.

## Scope

**What I read.**

- The whole mission document, and the Phase 2 and Phase 3 reviews with their answers.
- The whole diff: 100 files, 1,723 insertions, 1,996 deletions. In full: the Gateway side (`AlertDtos.cs`,
  the two push notifiers and their tests); client-core (`AlertsProvider.tsx` and its new `enabled` prop,
  `RestartRequestActions.tsx`, `actions.test.tsx`, `rosterGuard.test.ts`, `restartRequests.ts`,
  `package.json`); the Cockpit (`AppShell.tsx` and its test, `alerts/CockpitAlerts.tsx`, `documentTitle.ts`
  and their test, `sessions/AskChip.tsx`, `LinkRequests.tsx`, `SecretTransferChip.tsx`, `SessionRoster.tsx`,
  `SessionsView.tsx`, both chip tests, `styles.css`, `public/sw.js`, `push/sw.test.ts`); the phone
  (`pages/Home.tsx`, `routes.tsx`, `components/LinkRequestNotifications.tsx` and its new test,
  `HomeAlerts.test.tsx`, the four Home tests that lost their mocks, `dictationDeliveringSurfaces.test.tsx`,
  `styles.css`, `public/push-sw.js`, `push/pushSw.test.ts`); the eight deleted files (the three panels, their
  two stylesheets, their tests, the two phone pages and their tests); and the QA folder: `README.md`,
  `run-log.txt`, `guard-before.txt`, `guard-after.txt`, `tools/qa.py`, the four steps files, and
  `tools/host/Program.cs` with its project file.
- Every one of the 39 screenshots, opened and looked at, against the state its name and the README table claim.
- The shared code the shells lean on, in this tree: `AlertsBell.tsx`, `AlertsTray.tsx`, `AlertDetail.tsx`,
  the variables header of `alerts.css`, `polling/useVisiblePolling.ts` (its `enabled` parameter already
  existed), the `SecretTransferPlace` type (`"phone" | "cockpit" | "badge"`), `waitingFrom` on the provider,
  the Cockpit's `main.tsx` (a data router, so `useNavigate` is stable) and the phone's `main.tsx` (basename
  `/mobile`), the whole Cockpit service worker, the phone worker's click handler, and the Gateway's
  `FleetMessageLinkRequestEndpoints` answer route (the one production path that reopens a request).
- A sweep of HEAD for anything still naming the deleted modules, their exports, their stylesheets or the two
  removed phone routes: the only hits are two historical review files and two committed proof logs under
  `docs/proof/`, which are records, not code.

**What I ran** (all in this worktree, against the node modules already present).

| Command | Result |
|---|---|
| `npx tsc --noEmit` in `packages/client-core`, `apps/cockpit`, `apps/mobile` | exit 0, all three |
| `npx vitest run` in `packages/client-core` | 187 files, 2,370 tests passed |
| `npx vitest run` in `apps/mobile` | 38 files, 195 tests passed |
| `npx vitest run` in `apps/cockpit` | 132 files, 1,429 tests passed |
| `npx eslint` over every changed source and test file in the three workspaces | clean, exit 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert\|FullyQualifiedName~PushNotifier"` | 115 passed, 0 failed |
| The roster guard on this branch (`src/alerts/rosterGuard.test.ts`) | 3 of 3 passed |
| The roster guard against origin/main, out of tree | FAILS with the same 10 hits `guard-before.txt` records |

The last row is my own run, not the Developer's file: a scratch test (untracked, deleted afterwards, the tree
clean again) imported the real `requestPanelsIn` from the guard and fed it the two roster files read straight
out of git with `git show origin/main:<file>`, never the working tree. It listed the three panels and three
imports in `Home.tsx` and the two panels and two imports in `SessionRoster.tsx`, and the guard's own assertion
failed on them. So the guard has failed on code that mounts a panel and passes on this branch, which is what
section 7 demands of it.

**What I could not reach.**

- No local Gateway and no browser were started by me: the screenshots were read, not reproduced. The QA host is
  a program that starts the real `GatewayHost` and seeds its stores, and the steps are scripts that drive
  browser-harness; I judged them by reading. Every printed line in `run-log.txt` agrees with the screenshot it
  sits beside.
- The hosted run (section 7, "the same flow once on the hosted Gateway after deploy, from the phone") has not
  happened and cannot have: it needs the merge and the deploy first. It is still owed by the mission.
- The Cockpit's per-session chip popover (`AskChip`, which now draws the shared `AlertDetail`) has no screenshot;
  it is proved by the two chip tests in jsdom only.
- `scripts\test-database.ps1` was not run by me; the README records one executed, one passed.

## Checks against the mission that hold

- **Goal 1, nothing that asks the owner renders in the session list.** `Home.tsx` and `SessionRoster.tsx` mount
  no panel and import none of the three modules; the three panels, their two stylesheets and their tests are
  deleted, not left beside the new code; the three export paths are gone from `package.json`; the guard fails on
  origin/main and passes here (run both ways by the Developer and by me). The two session-list screenshots show
  the bell at 3 and no card, and the run counted 0 matching elements.
- **Goal 2, one bell in both headers, from one component.** The phone mounts `AlertsBell` in the Home app bar
  left of the filter button (`HomeAlerts.test.tsx` pins the order); the Cockpit mounts it in the rail head
  through `RailAlertsBell`, kept with its badge when the rail is collapsed (`AppShell.test.tsx`, and the
  `desktop-bell-twelve-collapsed-rail` screenshot). No badge at zero, "1", "9+" at twelve: six screenshots, both
  apps. The red count is the Gateway's `needsAnswerCount` and nothing else.
- **Goal 3 and 4, the tray and the opened alert.** Needs you above Earlier, newest first; each of the three
  kinds opened at both widths with its own action block; the restart's two-step confirmation kept (`Yes, restart
  it` / `Not now`); Details collapsed by default and expanded for the shots.
- **Goal 5, clearing.** Clear one, clear all (Earlier gone, "Clear all" no longer offered), and the 409 refusal
  shown in the Gateway's exact sentence, on both apps.
- **Goal 6, answered means gone from the count at once.** The accept drops the badge from 3 to 2 and the alert
  moves to Earlier in the same step; on a Gateway with no launcher the Gateway's own re-check refuses, and the
  Phase 4 fix in `RestartRequestActions` makes a refused accept read the list again at once, so the opened
  alert says STOPPED rather than offering "Yes, restart it" until the next poll. The new test pins it.
- **Goal 7, nothing overflows.** At 360 px on the phone and in a 280 px Cockpit popover, with the 46-character
  version, the 300-character reason, a long machine name, a long session name and a long secret entry name: the
  six overflow screenshots show every line wrapping inside the tray, and the run measured every element in the
  tray against the window edge and its own box, printing the tray's width (344 px, 280 px) beside the empty
  list so an empty result is never an empty tray. The Cockpit at 390 px, found in the run to draw the tray 125 px
  past the edge, now moves it left to 8 px inside (`cockpit-390-tray`, box 8 to 382) with a test.
- **Goal 8, existing behaviour survives.** The Gateway diff touches only the two push payload `url` values and a
  helper that builds them; the restart, secret and message request routes, stores and state machines are
  untouched, and all 115 alert and push notifier tests pass. The place of answer travels: "phone" from the phone's
  bell, "cockpit" from the Cockpit's bell, "badge" from the session chip (`secretTransferChip.test.tsx` proves the
  body `("tr-1", approve, "badge")`). The message request's push is still closed on the phone once the Gateway
  lists it as answered, and that closer now runs on every screen (it sits in the gated layout) rather than only on
  the roster's All tab. A tap on a secret transfer or message request notification opens the phone on
  `/mobile/?alert={id}` and the Cockpit on `/sessions?alert={id}`, and each shell opens the tray on that alert
  and takes the parameter out of the address on close (tests in both shells). Older notifications and bookmarks on
  the two removed phone routes are redirected to the roster rather than a dead route.
- **Section 5.3, one stylesheet.** The phone and the Cockpit set only the documented variables (the `.app-bar`
  and `.rail-alerts` blocks); the Phase 3 guard that no shell stylesheet writes an `.alert-` rule still passes.
- **Section 5.5, the tab title.** `(2) DevThrottle Cockpit`, and the count is composed with the page's own name
  rather than overwriting it (`documentTitle.ts`; the saved-filter title still works through `setPageTitle`).
  The only other writer of a title in the Cockpit writes into a new window it opens, not this document.
- **Phase 3's observation 3 (the deep-link effect) is handled as the comment asks.** Both shells clear the link
  inside the `onOpenChange(false)` handler, so the tray cannot reopen on a link already used; a test in each shell
  closes with Escape and watches the address lose the parameter.
- **The screenshots are safe to publish.** Every name is a seeded fixture from `Program.cs`: machines `QA-DESK`,
  `mac-mini`, `linux-rig`, `build-box`, `devthrottle-mac-mini-build-02`; sessions 121, 125, 130, 142 with
  made-up names; repository `example-app`; account "Account 1"; secret entry NAMES only (`vercel-bypass`,
  `posthog-key`, and the long fixture), never a value. No email address, no token, no device key (the key is
  split off the host's READY line before it is printed), no real hostname. The two entry names already appear
  in tests on origin/main.

## Findings

None. Nothing I read, ran or looked at produces a wrong screen, a lost answer, a wrong count, a leaked value or
a broken test with the code as it stands on this branch.

## Observations (no proven harm; for the Delivery Lead unless named otherwise)

1. **A refusal sentence can outlive the state it describes, but only on a path production cannot reach.** The
   `clear refused` and `cleared all` screenshots show the 409 sentence staying under the row after the row moves
   to Needs you; the README notes it. `clearErrors` in `AlertsProvider` is keyed by id and is only reset by the
   next clear attempt on that id, so if that alert were later answered the sentence "This still needs your
   answer" would sit under an Earlier row until its X is pressed. I did not call this a finding because the
   refusal needs an Earlier alert to go back to needing an answer between two reads, and the only production
   path that reopens a request does so inside the same answer request, milliseconds after claiming it, when
   setting up the link throws. The QA run reached it only by holding the browser's list read and reopening the
   request by hand. A one-line change (drop an id's clear error whenever a read lists that alert with
   `needsAnswer` false, or whenever the alert leaves the list) would close it; it is Phase 3 code.
2. **The roster guard is a name guard.** It catches a JSX element named `...Request|Transfer|Approval` and drawn
   as `...Panel|Card`, and an import of the three old modules, in exactly two files. A future card named
   otherwise (`RestartBanner`, `ApprovalsList`) or mounted from a roster subcomponent file would pass it. That
   is the strongest static check available without a runtime render, and it meets section 7; the Delivery Lead
   should know what it does and does not see.
3. **The bell is on the phone's roster screen only.** Section 5.4 says "present on every screen that has the
   app bar"; the bell is mounted in `Home.tsx`, and other phone screens draw their own bars without it. The
   provider and the notification closer sit in the gated layout, so the count is read everywhere; only the
   control is not. Goal 2 is met as written ("one bell in the header of both apps"); whether 5.4 meant more is
   the document's call.
4. **For the Delivery Lead - the Cockpit service worker does not know the message request push.** Pre-existing,
   not this branch: `MessageLinkRequestPushNotifier` sends to every subscription in the store, and the Cockpit's
   `sw.js` handles only `secret-transfer`, so a message request push arriving at a Cockpit subscription is read as
   a count of 0 and closes the "needs you" notification. The mission lists the message request as phone-only
   today and 5.6 puts the flows out of scope, so this is for a later issue, not this pull request.
5. **The QA host project is committed under `docs/`.** `tools/host/Program.cs` opens with "Out of tree, never
   committed", and it is committed. Nothing builds it by accident: it is not in the solution, no script globs
   project files outside `tools/`, and no bin or obj folder is in the tree. The sentence should match the fact,
   or the folder should go; either is fine.
6. **The accept screenshots end in STOPPED, not "Accepted - waiting for the Director".** The README says why: a
   local Gateway has no launcher, so the Gateway's own re-check refuses on accept. The "Accepted" row the
   mission describes is shown by the seeded running restart in Earlier. The accept a real Director takes is
   part of the hosted run that is still owed.
7. **For the Phase 2 Developer, nothing to do.** The `?alert=` parameter the phone's push URL carries is built
   with `Uri.EscapeDataString`, so the colon in the id is `%3A`; both shells read it back with
   `URLSearchParams`, which decodes it. Noted so nobody "fixes" the escaping.

## Answers (Phase 4 Developer, 25 October 2026)

The Delivery Lead ruled on each observation; the code changes are in the follow-up commit on the same pull request.

1. **Accepted, fixed.** `AlertsProvider` now remembers what the last read said about each alert needing an answer.
   A clear's error is dropped when a read lists that alert as no longer needing an answer after one that said it
   did (it was answered), or when the alert leaves the list. One narrowing of the wording I was given, on purpose:
   an error on an alert that was in Earlier and still is (the Gateway could not be reached for the clear) is kept,
   because the clear reads the list again straight after and a plain "needsAnswer is false" rule would wipe that
   failure in the same moment it was shown. Four tests in `Alerts.test.tsx` ("a clear's refusal"): it stays while
   the alert still needs an answer, goes once answered, goes once the alert leaves the list, and a failed clear on
   an Earlier alert keeps its sentence. With the drop disabled, the two "goes" tests fail.
2. **Declined, no change** (the Delivery Lead's ruling). The guard is a name guard and says so in its header; it
   meets section 7.
3. **Accepted, fixed.** The phone's bell is one component, `apps/mobile/src/components/AppBarAlerts.tsx`, which
   holds the bell and the `?alert=` deep link handling that used to sit in `Home.tsx`. It is mounted in every app
   bar the phone draws: the roster, the session screens (`SessionAppBar`, so Chat, Terminal, Voice mode and
   Reports), and About, Account, Diagnostics, the file viewer, the mail desk, Start a session, the recorder, the
   report landing and report view, Repos, Settings and Your Throttle. `AppBarAlerts.test.tsx` fails if any app bar
   in the phone source does not carry it (it must find at least 15 bars, so an empty sweep cannot pass; with the
   bell taken out of About it fails naming `pages\About.tsx`), and proves the deep link opens the tray on a screen
   other than the roster and leaves the address on close. Screenshot: `qa/phone-bell-on-settings.png` (Settings,
   count 3, from the same local Gateway).
4. **No change** (the Delivery Lead's ruling); for a later issue.
5. **Accepted, fixed.** The header of `tools/host/Program.cs` now says it is committed with the QA run as the record
   of how the screenshots were made, and that nothing builds it but `dotnet run` from that folder.
6. **No change** (the Delivery Lead's ruling); the accept a real Director takes is part of the hosted run.
7. **No change** (the Delivery Lead's ruling).

Section 7 rerun on the changed code: typecheck clean; client-core 2,374, phone 198, Cockpit 1,429 tests passed;
lint only the five known errors; Gateway unit tests (Alert and PushNotifier) 115 passed; Gateway.Tests Alert 13
passed; `scripts	est-database.ps1 -Filter "FullyQualifiedName~Alert"` 1 executed, 1 passed, 0 skipped.
