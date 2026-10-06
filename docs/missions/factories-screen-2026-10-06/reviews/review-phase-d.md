# Phase D review - the Cockpit's Factories screens

**Verdict: APPROVED - no blocking defect. Three low follow-ups for the Lead to decide, none of which needs to hold the merge.**

Reviewed commit `2e575864a` (pull request 3595, the Factories screen phase D) with `git fetch origin` and
`git diff origin/main...HEAD`, against the merge base `8669823a7` (origin/main at the time of review). The Talk
answer's shape was read on branch `origin/factories-screen/talk` (pull request 3590, `FactoryTalkStartedDto` in
`FactoryRegistryDtos.cs` and `FactoryTalkEndpoints.cs`). The build target was the Cockpit brief
(`BRIEF-developer-cockpit.md`) and the four mockups in `DESIGN-report-dcdcdec9-v4.html`.

## Findings

No finding proves harm that would block the merge. The three items below are real but low; each names the harm
and why it is not a blocker.

### 1. [Low] The Talk button's busy words are the Cockpit's, not the Gateway's

Location: `apps/cockpit/src/factory/FactoryParts.tsx:393` (`{busy ? "Starting the talk..." : talk.label}`)

Every other busy label in this area arrives from the Gateway: `FactoryPause.busyLabel` on the pause button and
`handledBusyLabel` on a waiting item. The Talk button hard-codes "Starting the talk...". The phase B DTO
(`FactoryTalkDto`: `Label`, `FactoryId`, `SeatId`) carries no busy label, so the Developer could not read one; the
deviation is the DTO's, not this pull request's alone.

Harm: the one state of the button during the wait shows words the Gateway cannot change, and a later change to the
Talk wording on the Gateway leaves that state behind. It does not mislead today. Fix is a `BusyLabel` on
`FactoryTalkDto` folded in `FactoriesScreenFold` and read here - a Gateway change, so a follow-up, not this merge.

### 2. [Low] The old Gateway factories view and map now have no reader anywhere, and were kept

Location: `GET /gateway/factory-agents/factories` and `GET /gateway/factory-agents/factories/{id}/map`
(`FactoriesViewDto`, `FactoryMapDtos.cs`, their folds and tests, untouched by this pull request)

This pull request removes `getFactories` and `getFactoryMap` from client-core and the views that used them. I
searched the Cockpit, the mobile app, the Python tools and the docs for any other reader of those two routes and
found none. The brief's item 7 said to remove the Gateway's old view fields "only if nothing else reads them";
nothing does, and they were left. The pull request even edits a test of that dead view
(`FactoryAgentsViewRouteTests.cs:207`, the `MapHref` assertion) to keep it green.

Harm: a served, tenant-scoped surface with a fold and tests that nobody renders keeps being maintained, and the
next person reading `FactoryAgentsFold` cannot tell it is dead. Not a defect of the screens; a scope call for the
Lead. If the decision is to remove, it is a Gateway pull request of its own.

### 3. [Low] The Talk request does not assert it got JSON, unlike the three reads beside it

Location: `packages/client-core/src/factory/factoriesScreenClient.ts:179` (`startFactoryTalk`)

`getJson` refuses a 2XX that is not `application/json` with the sentence "This Gateway does not serve the
Factories screen - it answered with a web page instead of data". `startFactoryTalk` has no such guard. I checked
what actually happens when the Talk route is absent (a Gateway deployed from this pull request before pull
request 3590 merges): `CockpitReactApp.ServeAsync` answers a non-GET with 404 and the text
`Not found: POST /gateway/factory-agents/factories/<f>/seats/<s>/talk`, which `GatewayError.from` reads as the
reason and the button shows beside itself. So the failure is NOT silent - the brief's hard rule holds - but what
the owner reads is a raw path, not a sentence that says the Gateway is older than the screen. A content-type
check matching `getJson` would make the two paths say the same thing. Cosmetic until the deploy order puts D live
before C; worth one line.

## What I checked and found sound

**Rule 7 - the client decides nothing.** Every word, tone, order and count on the three screens is read from
`FactoriesScreenFold`'s answer and rendered as given: the status chip (`statusWord`, `statusTone`,
`statusReason` as the tooltip), the waiting text, the "No CEO" text, the column headings, the tab labels and their
order, "Seats (n)", the seat count text, the computer text, the goal, goal number, waiting items, CEO lines and
"Last talk with you", the Seats rows and the note under them, the footer sentence, the empty and truncated
sentences, and every href (row, breadcrumb, "All reports", saved reports, the Talk answer's `href`). The client's
only decisions are which component renders a given tab key and that a tab the Gateway did not offer opens the
Overview, which the brief asks for. The last-run tone becomes a colour in one place (`.fa-lastrun.fa-tone-*`),
the pattern the area already uses. Fixture words are deliberately odd ("NEEDS YOU-X", "1 question (fixture)") so
the verbatim tests cannot pass by accident.

**Talk cannot fail silently or double-start.** The button disables and sets `aria-busy` on the first press (test:
"shows it is working at once"), so a second press on the same button cannot fire; the press stops propagation so
the clickable list row does not also open the factory (asserted in the test by the address staying put). On
success it navigates to the Gateway's `href`; `/session/:sessionId` is a real Cockpit route (`routes.tsx:123`).
On refusal the catch shows the Gateway's own sentence in a `role="alert"` and re-enables the button. I traced the
real wire path, which the test does not: the Talk endpoint refuses with `{ error }`, `failureDetailFromText` reads
`error` as the reason, and a 4XX is not retryable so no "Try again." is appended to a 409 whose own sentence says
"press Talk again". The committed screenshot `qa/after-talk-refused.png` shows exactly that sentence. A
switched-off area answers an empty 404 (phase C review, finding 2, on the Gateway side) and the button then shows
the generic "could not start the talk" sentence - not silent.

**Redirects and dead links.** All four old address shapes redirect with their query kept
(`FactoryRedirects.tsx`): the list (dropping `tab=agents`), waiting, the factory page (old `?tab=agents` to
`/seats`, `?tab=memory` to `/memory`, `?tab=map` and no tab to the Overview) and the agent page to
`/factories/<f>/agents/<a>`. Ten cases are tested. Every href the Gateway still writes was moved in the same
change (`FactoryAgentsFold`: saved reports, `WaitingHref`, `ActivityHrefFor`, `MapHref`, `AgentHref`) and its
Gateway tests updated; the agent page's `askHref` goes to `/fleet-manager`, untouched. The remaining
`/factory-agents` strings in the repository are historical mission documents and proof scripts, plus the
administrator API path, none of which a user follows. Route order is right: `/factories/waiting` is static and
wins over `:factory`; `agents/:agent` has three segments and cannot be caught by `:tab`.

**"change - coming" is visibly not a control.** It is a `<span class="fa-coming">` - dim, italic, 11px, no
border, no underline, `cursor: default` - and the tests assert it is a SPAN with no `a` or `button` ancestor on
both the page header and every seat row. The screenshots (`qa/after-factory-page.png`, `qa/after-phone-seats.png`)
show it reading as a footnote beside the computer name.

**Phone width (390px).** The list's rows become cards under 640px (`grid-template-areas`, heading row hidden,
Talk stretched full width), the Overview collapses to one column, the tabs wrap, and the Seats table turns into
block cards so no `min-width: 600px` table forces a sideways scroll (the later `.fa-seats { min-width: 0 }` wins
by order). `overflow-wrap: anywhere` is set on every list cell and on the error sentence. The three phone
screenshots in `qa/` show no horizontal scroll and the Talk buttons in reach. The tests prove the stylesheet
holds those rules and that the rows carry the classes the rules read (jsdom applies no media queries, and the
test says so rather than pretending).

**Removed code is not referenced.** The Cockpit and client-core type-check clean (`tsc --noEmit`, both exit 0)
and `eslint` on the changed files passes. No source file imports `FactoryAgentsView`, `FactoryPageView` (the
component), `FactoryMap`, `AgentTable`, `getFactories` or `getFactoryMap`; `FactoryPageView` now names only the
DTO type. The `.fa-map-grid` rule was kept on purpose for the Memory tab and its comment says so. Every export
left in `FactoryParts.tsx` has a reader.

**Visual style.** `docs/VisualStyle.md` states in its own preamble that it does not govern the Cockpit; the rule
for the web shells is to use the tokens in `apps/cockpit/src/styles.css` and the nearest component family. Every
colour used is an existing token (`--surface`, `--surface-2`, `--border`, `--text`, `--text-dim`, `--accent`,
`--ok-text`, `--danger-text`, `--accent-text`, `--warn`), the buttons are the shared `Button` with its `primary`
and `secondary` variants (one primary per surface, on the page header), and the cards reuse `.fa-panel`.

**Against the mockups.** Mockup 1: sidebar "Factories", three tabs, one row per factory with name, waiting text,
status chip and "Talk to <CEO>" or "No CEO", worst first, footer sentence - matches `qa/after-list.png`. Mockup 2:
breadcrumb, header with status, CEO, seat count, computer and the coming label, Talk, six tabs, five Overview
cards - matches. Mockup 3: the Seats tab's four columns plus Talk and the note - matches. Mockup 4: phone cards -
matches. The "All factory agents" tab is gone and the test asserts its absence.

**Tests run.** Cockpit: 79 files, 802 tests, all passed. client-core: 150 files, 1784 tests; one test in
`src/devreports/devReportNotes.test.ts` (a `selectionQuote` assertion, nothing to do with factories) failed on
the first run and passed on two reruns - a flaky test outside this change, noted for whoever owns it.

## Noted, not findings

- `Crumbs` in `FactoryView.tsx` builds the breadcrumb's links by splitting the Gateway's crumb string on " / ".
  A factory title containing " / " would mislink the middle part. No registered title has one (the ten manifests
  were checked), so there is no harm today; a `crumbParts` list from the fold would remove the parsing.
- `FactoryView` stays mounted when only the `:factory` parameter changes (same route element), so for the length
  of one request the previous factory's page is on screen under the new address. No screen offers such a
  transition - every link out of a factory page goes to the list, a session or the agent page - so it is reachable
  only by editing the address bar.

## Scope

I read all 40 files in the diff: the shell and rail (`AppShell.tsx`, `NavIcon.tsx`), the routes and redirects,
the three new views (`FactoriesView.tsx`, `FactoryView.tsx`, `FactoryActivityTabs.tsx`), the shared parts and the
Talk button (`FactoryParts.tsx`), the new client (`factoriesScreenClient.ts`) and the trimmed old one, the
stylesheet, the fixtures, the new test file and the three adjusted test files, the four Gateway href changes and
their tests, and the twelve screenshots. I did not run a browser against a live Gateway; the phone-width and
rendering claims rest on the stylesheet, the tests and the committed screenshots, and the Talk wire path rests on
reading pull request 3590's endpoint and `client-core`'s error parsing, not on a live call.

## Round 2

**Verdict: APPROVED - both answered findings are fixed as described, and nothing new was found.**

Reviewed commit `05a83390d` (pull request 3595 rebased onto main after pull request 3590, Talk, merged as
`656394653`), with `git fetch origin` and `git diff origin/main...HEAD`. The rebased phase D commit (`202431382`)
is byte-identical to the round 1 commit in every web file (`git diff 2e575864a 202431382 -- apps packages` is
empty), so the rebase introduced nothing. The fix commit touches eight files; I read all of them.

### Finding 1 - fixed: the busy words are the Gateway's

`FactoryTalkDto.BusyLabel` is folded in `FactoriesScreenFold` for the CEO's button ("Starting the talk with Nora
Hale...", or "with the CEO..." when two CEOs share a name - the same `who` the label uses, so the two cannot
disagree) and for every seat row (the seat's name). `TalkButton` renders `talk.busyLabel` and holds no words of
its own. Fold tests assert the label on the list, the duplicate-name case and the Seats tab; the Cockpit test
asserts the fixture's deliberately odd busy words reach the button. The Gateway fold tests pass (48 of 48 in
`FactoriesScreenFoldTests`, run here).

### Finding 3 - fixed: the Talk answer must be JSON, and a refusal shows every sentence

`startFactoryTalk` now runs the same `assertJson` as the three reads, so a Gateway older than the screen answers
"This Gateway does not serve the Factories screen..." on Talk exactly as on a read; the new
`factoriesScreenClient.test.ts` proves it on both paths. A refusal is read by `talkRefusalReason`: the Talk
route's `{ error }`, a problem-details `{ title, detail }` (which is what the session create behind the route
answers at 502 - `Results.Problem(err, 502)` in `GatewayEndpoints.DirectorSpawn.cs:125`), and both the error and
the detail when a body carries both, each terminated so they read as sentences. I checked what the wire path
does with it: the reason lands in `GatewayError.serverReason`, a 502 is retryable by status class so
"Try again." is appended, and a 4XX refusal shows the Gateway's sentence unchanged. A short non-JSON text (the
shell's "Not found: POST ...") is still carried as the reason; an empty body (the switch-off 404 from
`FactoryAgentsGate`, which merged main still answers with no sentence) shows "Could not start the talk (error
404)." - not the Gateway's words, but not silent either, and that is the Gateway's gap (phase C review,
finding 2), not this pull request's.

### Finding 2 - filed separately

The old Gateway factories view and map with no reader are issue 3596. Nothing to check here.

### New in the diff since round 1

- `AppShell.test.tsx`: the rail test renamed from "Factory Agents" to "Factories" with the new href. Sound.
- `FactoriesScreenDtos.cs`, `FactoriesScreenFold.cs`, `FactoriesScreenFoldTests.cs`: the busy label, as above.
- `factoriesScreenClient.test.ts`: new, five cases, all passing.
- `fixtures.ts` and `FactoriesScreen.test.tsx`: the busy label in the fixtures and the two Talk assertions.

Nothing else moved. The round 1 verdicts on rule 7, double-start, redirects, the coming label, phone width,
removed code and style stand unchanged.

### Tests run at this head

Cockpit: 79 files, all passed. client-core: 151 files, all passed (the flaky dev reports test passed this time).
`tsc --noEmit` clean on both; `eslint` clean on the changed files. Gateway: `FactoriesScreenFoldTests`, 48
passed. Not run: the full Gateway suites and a browser against a live Gateway.

### Scope

The eight files of the fix commit in full, the rebase check above, the merged Talk endpoint and the session
create's refusal shapes on main (`FactoryTalkEndpoints.cs`, `GatewayEndpoints.DirectorSpawn.cs`,
`FactoryAgentsGate.cs`), and client-core's error parsing (`GatewayError`, `gatewayErrorMessage`,
`withRetryHint`) to trace what the owner reads for each refusal.
