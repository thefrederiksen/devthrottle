# Review: #2312 pull request 1 - the Cockpit team switcher (thefrederiksen/devthrottle#3527)

Written by a separate review session, 2026-10-04. Head reviewed: ea4aba362 (merge base with origin/main:
cdaab4f2b). The reviewer did not write this code and changed no file.

## Scope

**Read, in full:** every file in `git diff origin/main...HEAD` (12 files): `packages/client-core/src/teams/`
(`teamsClient.ts`, `CurrentTeam.tsx` and their two test files), `apps/cockpit/src/teams/` (`TeamSwitcher.tsx`,
its test, `teams.css`), the changes to `AppShell.tsx`, `AppShell.test.tsx`, `railCollapse.test.tsx`,
`packages/client-core/package.json`, and the committed proof `docs/proof/teams-2312/pr1-test-runs.txt`.

**Read, to check the pull request against what the Gateway really answers:** `Api/TeamEndpoints.cs` (the
`GET /teams` answers, the self-hosted refusal, the shape of one team), `Teams/TeamsReleaseSwitch.cs`, the mapping
in `GatewayHost.cs` (`if (TeamsReleased) TeamEndpoints.Map(...)`), `Cockpit/CockpitReactApp.cs` (the page fallback
that answers an unmapped route), `packages/client-core/src/api/client.ts` (`gatewayFetch`, `GatewayError`,
`gatewayErrorMessage`), `auth/accountStore.ts`, `apps/cockpit/src/factory/useFactorySwitch.ts` (the nearest
existing pattern) and `apps/cockpit/vite.config.ts`. Also issue #2312's developer brief, section "PR 1", and
mockup S11.

**Ran, in the review worktree at ea4aba362:**

- `npx vitest run` in `apps/cockpit`: 68 files, 593 tests, all passed.
- `npx vitest run` in `packages/client-core`: 141 files, 1653 tests, 1652 passed and 1 failed -
  `src/devreports/devReportNotes.test.ts > note-mode > tells the app what the reader has selected...`. That file
  is not touched by this pull request, and run on its own it passed (64 of 64). I am reporting it as a test that
  fails intermittently in the full run, not as a defect of this change; I did not find its cause.
- The new tests on their own: client-core `src/teams` 18 of 18; Cockpit `src/teams`, `AppShell.test.tsx`,
  `railCollapse.test.tsx` 20 of 20.
- `npm run typecheck` at the root (client-core, Cockpit, mobile): clean.

**Could not reach:** a running Gateway and a real browser. Nothing here was seen on screen; the dark-Gateway
answer (HTTP 200 with the app's own page) was confirmed by reading `CockpitReactApp.cs`, not by a request. I did
not re-run the Developer's two "red proofs" (that would mean editing files). The mobile app is untouched by this
pull request and was not reviewed.

## Verdict

**Mergeable once F1 is answered. No blocker.** The things the brief asked me to look hardest at mostly hold:

- A person with no team, and a Gateway with Teams dark, get no switcher and one single request at page load - no
  loop. Verified in code and by tests that can fail.
- "Dark" is told apart from a real failure this way: a 404, or HTTP 200 with `text/html`, is "not offered";
  every other non-success status, a non-JSON body, a missing `teams` list or an unreadable team is thrown and
  shown. A real Gateway error is not treated as "no teams". The remaining weakness of reading "dark" from a
  content type is F2.
- The page decides nothing that belongs to the Gateway. The role is the Gateway's label, shown verbatim; nothing
  in the diff branches on a role.
- The chosen team is never sent to the Gateway in this pull request. It lives in React state and in the browser's
  storage only; `choose` refuses an id that is not in the Gateway's own list. Nothing implies the client grants
  access.
- No account subject or email is logged or written to the console. The storage key carries the browser-local
  account entry id, which `accountStore.ts` documents as never sent anywhere.

## Findings

### F1 - should-fix - one failed read puts a Teams message in front of people who have no team, and is never retried

**Location:** `packages/client-core/src/teams/CurrentTeam.tsx` lines 86-103 (the read runs once per mount of the
shell and never again); `apps/cockpit/src/teams/TeamSwitcher.tsx` lines 20-28 (the error line is drawn for any
`error` status, before it is known whether the person has a team).

**The harm.** The provider is mounted in `AppShell`, which lives for the whole life of the tab, and it asks
`GET /teams` exactly once. If that one request fails - a dropped connection, a 502 during the in-place deploy,
the 500 that `TeamEndpoints.Guarded` returns on a database fault, a 503 - the status is `error` until the person
reloads the page. Two groups are hurt:

1. A person who has never joined a team (and, today, every person, because production is dark) now sees
   "Your teams could not be read just now." at the top of their rail, permanently for that tab. The ruling is that
   such a person sees no change anywhere. The switcher cannot know they have no team when the read failed, and
   it chooses to speak. Cockpit tabs stay open for days.
2. A person who had a team on screen is silently put back on their own account (`current` is null whenever the
   status is not `ready`, line 126) and stays there for the life of the tab, even after the Gateway recovers
   seconds later. Every other read in the Cockpit recovers by itself on its next poll; this one does not, and
   the two later pages (#2303, #2304) will inherit that.

No test covers either case at the shell level: `AppShell.test.tsx` has no case where the read fails, and the
dark-Gateway case there (line 183) does not assert that the error line is absent.

**What must change:** the read has to be asked again after a failure (a bounded retry, or on the next visit to
the tab - not a tight loop), and the error line must not be the outcome of a single failed request for someone
who may have no team.

Developer answer: ACCEPTED, fixed in 929f6a684 (head 07b39291c). The read is asked again after a failure - 15 seconds, then 30, then every 60 until it succeeds; nothing reads again once it has succeeded, so it is never a loop (`CurrentTeam.tsx`, `retryDelayMs`). The error line is shown only to a person this browser knows is on a team (a remembered team); anyone else - including every person while production is dark - sees nothing, because they may have no team at all. A person with a remembered team is no longer silently put on their own account: `resolving` stays true until the Gateway answers (F3), and the remembered team comes back on screen when the retry succeeds. Tests that can fail: `CurrentTeamProvider_ReadFails_IsAskedAgainWithBackoff_AndRecovers` (fake timers: called at 0, 15 s and 45 s, recovers the remembered team, no call after success), `RetryDelayMs_BacksOff_15Then30ThenEvery60`, `TeamSwitcher_TeamsCouldNotBeRead_NoRememberedTeam_RendersNothing`, `TeamSwitcher_TeamsCouldNotBeRead_ARememberedTeam_SaysSo`, and at the shell level AppShell "shows nothing about teams to a person with no team when the read of their teams fails"; the dark-Gateway AppShell case now also asserts the error line is absent. Red proof: removing the retry fails the backoff test; showing the error line to anyone fails the two no-team cases.

### F2 - should-fix - under the Cockpit's own development server, Teams is silently "not offered" on a Gateway that has it on

**Location:** `packages/client-core/src/teams/teamsClient.ts` line 61 (HTTP 200 with `text/html` means "dark");
`apps/cockpit/vite.config.ts` lines 67-113 (the proxy list has no `/teams` entry, and this pull request does not
add one).

**The harm.** `vite.config.ts` says every root-relative prefix a Cockpit page calls must be in the proxy list.
`/teams` is not. Under `npm run dev`, with or without `COCKPIT_PROXY_TARGET`, the request for `/teams` is
answered by the development server with its own `index.html` - HTTP 200, `text/html` - which line 61 reads as
"this Gateway has not turned Teams on". So against a Gateway with `CC_GATEWAY_TEAMS=1` and a seeded team, the
switcher shows nothing, with no error and nothing in the console. The next two pieces of work (#2303, #2304) and
the second pull request of this issue are built on this switcher, and whoever runs the development server will
see Teams missing and have no signal why. This is the general cost of inferring "dark" from the shape of a
fallback page rather than from something the Gateway says: any other source of an HTML 200 on that path is read
the same way.

**What must change:** at the least, `/teams` in the development proxy list. Whether the Gateway should state
"Teams is not released" itself (as it does for the Factory Agents switch), so the client stops inferring it, is a
design question for the Tech Lead - I am naming it, not requiring it for this pull request.

Developer answer: ACCEPTED, fixed in 929f6a684: `/teams` is in the Cockpit development proxy, with a comment saying why. Kept "dark = route absent", as you recommend - it matches #2300's switch and its absence tests; no new always-on Gateway flag in this pull request.

### F3 - note - `current === null` means three different things, and the two later pages will read it

**Location:** `packages/client-core/src/teams/CurrentTeam.tsx` lines 33-34 and 125-128.

**The harm.** `current` is null when the person is on their own account, while the Gateway has not answered yet,
and when the read failed. A page that reads only `current` - the natural thing to write - will, for a person with
a remembered team, first load and show the person's OWN fleet, then switch to the team when the answer arrives
(a wasted request and a flash of the wrong data), and after a failed read will show the own account as if it had
been chosen. The store does expose `status`, so a careful reader can tell the cases apart; nothing in the type
makes them. This is not a defect in this pull request, since nothing reads `current` yet. It is worth one sentence
in the file's header telling #2303 and #2304 to wait for `status === "ready"` before treating null as "own
account", or it will be found the hard way twice.

Developer answer: ACCEPTED, fixed in 929f6a684. `CurrentTeamState` now has `resolving`: true while this browser remembers a team the Gateway has not yet confirmed (first read in flight, or failed), so a null `current` is not "the own account" then. Never true for a person who has never picked a team, so they see no change. The file header tells #2303 and #2304 to read `resolving` before treating a null `current` as the own account. Pull request 2's Fleet Map uses it (it shows a loading line, not the own fleet, while resolving). Tests: `Resolving_ARememberedTeam_IsTrueWhileUnconfirmed_AndFalseOnceRead`, `Resolving_ARememberedTeamAndTheReadFails_StaysTrue`, `Resolving_NoRememberedTeam_IsNeverTrue`.

### F4 - note - with the rail collapsed, nothing on screen says which team is in force

**Location:** `apps/cockpit/src/AppShell.tsx` line 243 (`{!railCollapsed && <TeamSwitcher />}`).

**The harm.** The collapsed rail keeps every destination but drops the switcher entirely. Harmless today, because
no page reads the current team. Once the Fleet Map (pull request 2) shows one team's roster, a person with the
rail collapsed is looking at a team's Directors, or their own, with no visible statement of which. Raised now so
it is decided before pull request 2 rather than discovered after it.

Developer answer: DECLINED, with the reason. Every page that shows one team states the team in its own heading - pull request 2's map reads "Fleet Map - DevThrottle" - and that heading is on screen whether the rail is collapsed or not, which is where "which team am I looking at" is asked. The collapsed rail is icon-only by design (issue #3074), and a team chip there would be a second statement of the same fact. If #2303 or #2304 adds a team page whose heading cannot carry the name, this is worth reopening then.

### F5 - note - the client keeps its own list of the four role names, and one unknown role fails the whole list

**Location:** `packages/client-core/src/teams/teamsClient.ts` lines 76-88; test `GetMyTeams_UnknownRole_Throws`.

**The harm.** Nothing in the client uses the role for anything except showing it, yet a role label outside the
hard-coded four makes `readTeam` throw, which fails the read of ALL the person's teams. A tab still running an
older bundle after a Gateway that adds or renames a role would lose the switcher for every team, not just show
the new label. It is a second copy of the Gateway's role list with a failure mode and no purpose beyond the
TypeScript type. Small, and arguably deliberate strictness; say which.

Developer answer: ACCEPTED, fixed in 929f6a684. Not deliberate strictness worth keeping: the client only shows the role, and the Gateway owns the list (rule 7). `TeamSummary.role` is now a plain string shown as sent; only an empty role is refused. `GetMyTeams_UnknownRole_Throws` is replaced by `GetMyTeams_ARoleTheClientDoesNotKnow_IsShownAsSent` and `GetMyTeams_AnEmptyRole_Throws`.
