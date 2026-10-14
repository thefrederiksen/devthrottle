# Review 1 - Factory Seats, phase 2b: the brief panel on the seat page

Date: 2026-10-13. Reviewer session: "Factory Seats - Reviewer - phase 2b the brief panel" (Pi on GLM-5.3).
What was reviewed: branch `factory-seats/phase-2b-brief-panel`, four commits ahead of `origin/main`, read in a
worktree cut from `origin/factory-seats/phase-2b-brief-panel`. The Developer's own proof
(`docs/qa/factory-seats/phase-2b/README.md`) was read and not trusted on its word; every claim below was checked
against the code.

## Verdict

**No findings.** Nothing was found, within the scope stated below, that breaks for anybody or that must change
before the pull request. The questions the Implementation Lead asked me to look hardest at were each answered in
the code, and each answer is held down by a test. The reasoning for each is given so the seat that built this can
check it rather than take it on trust.

## The questions I was asked to look hardest at

### 1. Are the three new read routes owner-only and account-scoped, and known to the session key guard?

Yes, on all three counts, and exactly the way the seat page route is.

- All three routes (`.../brief/editor`, `.../brief/history`, `.../brief/versions/{version}`) are mapped in
  `FactoriesScreenEndpoints.MapSeatPage` and pass through `FactoryAgentsViewEndpoints.Owner`, the same guard the
  seat page itself passes through: no account bound, refused; a session key, refused in words. They are inside the
  factory agents switch group like every factory route, so an account with the switch off gets "not found".
- The session key guard is an allow list, and none of the three paths is on it, so a session key is refused before
  any route runs. A raised session key is refused too: the raised widenings name typing, Fleet Manager routes and
  message links, and none of those covers these paths. This is asserted route by route in
  `FactorySeatRoutesTests.SessionKeyGuard_KeepsTheOwnersBriefViewsFromASessionKey`, and over a real web server in
  `BriefViews_AreTheOwners_ASessionKeyIsRefused` (status 403 for each of the three).
- Account scoping is the same machinery as every factory route: the tenant is resolved from the credential before
  the handler runs, and both the registry lookup and the brief store are keyed by account. Another account's
  credential finds no such factory and is answered "not registered". A session of the factory that wants its brief
  reads it through the phase 2a routes under `/factory-briefs`, which is unchanged and where the factory's own rule
  answers it - that is the design, and the new routes take nothing away from it.

So: no session, and no other account, can read a brief's text through the three new routes.

### 2. Is there any path where text the owner typed is dropped or overwritten without the owner choosing it?

I looked for one and found none. Everything the editor does goes through the phase 2a store, which is append-only
with the version stated on every write; there is no update and no delete anywhere in this phase, so nothing can be
overwritten at all - a "stale" save is refused, never merged by the machine.

- A stale save (a session wrote a newer version meanwhile): the Gateway refuses with the newer version attached;
  the owner's text stays in the box, the newer version is shown beside it, and nothing more is sent until the owner
  picks "Save my text after the newer version" (which appends after it) or "Take the newer version and drop my
  text" (which is the owner's choice, in the button's own words). A third writer in between is shown again the
  same way.
- A refused publish: the shared confirm dialog keeps the refusal on screen and the editor behind it keeps the
  text. If the text was saved first and the publish then refused, the editor remembers the saved version, so a
  second press publishes it and does not save the same text twice.
- A failed request (the Gateway unreachable, a web page instead of data): the editor shows the sentence and keeps
  the text; the version the text was started from is unchanged, so a retry states the same version.
- History and back: the editor has no History button; the only way out of it is "Close without saving" (labelled
  as exactly that), Save draft, or Publish. Navigating to History from the read panel unmounts nothing that holds
  typed words.
- The page reloading its data: the panel only reloads after a successful save, publish or bring back, and by then
  the editor has closed with its text stored. There is no polling that could remount the editor under the owner's
  hands.

The one place words can still be lost is leaving the whole seat page while typing - a link elsewhere in the
Cockpit, or closing the browser - which drops any form's unsent words everywhere in this application. That is not
a defect this phase introduced and not one it is alone in having; I am not raising it as a finding.

### 3. Can Publish ever publish a version other than the one the owner is looking at?

No. Two surfaces can publish, and each names its version.

- The editor: with the text unchanged it publishes the version the text was started from - the text in the box.
  With the text changed it saves that text as the next version first and publishes that one; if the save is
  refused because a newer version exists, nothing is published. The version is never taken from anywhere but the
  editor's own box.
- The history: each row's Publish carries the version of that row, and the confirm says "Publish version N" and
  "Every later talk and every later run ... will be given the text of version N". The button sends that version
  and nothing else.

And where the screen's picture is stale - a session saved a draft after the history page was read, another device
published first, the version was published and replaced - the store is the one that answers, in words:
already-published, older-than-published, no-such-version, and for a bring back sent against a version that has
moved, stale. The confirm dialog shows the store's own sentence, so the owner reads what actually happened rather
than a screen that guessed. The fold also offers no button the store would refuse on the page's own reading of the
brief's standing: `History_EveryButtonItOffers_TheStoreAccepts` presses every offered button against a real store.

### 4. Is the brief's text always shown as text - can markup or a script run in the owner's browser?

Yes, it is always text. The brief's text reaches the screen in three places: the panel's text block, the textarea
of the editor, and the opened version's block - each a React text node or a textarea value, which the framework
escapes. No code in this phase uses rich rendering of any stored string; every sentence the Gateway composes is
also rendered as text. A brief containing markup is displayed as the characters its author typed.

### 5. Can the owner publish somebody else's draft without being told it is not the published text?

No. When the newest version is a draft that is not the published brief, the editor's own first line says so, in
the Gateway's words, naming the version, who wrote it, when, and which version talks and runs are given until a
newer one is published. The read panel says it too. The confirm's sentences then describe the act itself. The
owner is told twice before any publish. When the newest version IS the published brief, the editor says that too
("This starts from version N, the published brief").

### 6. Does anything break for a seat with no brief on the Gateway - every existing seat?

No. A seat with no brief is the state every seat starts in, and it is the first case in the fold's tests, the
route tests over a real web server, and the cockpit tests: the panel shows the file's path and computer exactly as
phase 1 did, offers "Write the brief here" (the file's sentence kept word for word), and no History button (there
is nothing to page through). The editor starts empty, says where the file is, and the first save expects version
zero. Nothing in the phase 2a routes was changed to make room for this phase - the diff touches no file phase 2a
added except by adding the store to the seat page's wiring.

## Scope

What I read, in full: `FactoryBriefPanelFold.cs`, `FactoryBriefStore.cs`, `FactoryBriefEndpoints.cs`,
`FactoriesScreenEndpoints.cs` (the seat page and its three brief routes), `FactorySeatPageFold.cs`,
`FactorySeatBriefViewDtos.cs` and the brief half of `FactorySeatPageDtos.cs`, `GatewayHost.cs` (the wiring and
the switch group), the session key guard and the authentication step that consults it, `FactorySeatBrief.tsx`,
`factoryBriefClient.ts`, the brief half of `factoriesScreenClient.ts`, `FactorySeatView.tsx`'s use of the panel,
the shared confirm dialog, the stylesheet additions, the development proxy line, the test files
(`FactoryBriefPanelFoldTests`, the brief parts of `FactorySeatRoutesTests`, `FactorySeatBrief.test.tsx`,
`factoryBriefClient.test.ts`, the fixtures), and the mission document (phase 2b; proposals P2 and P3). I also
read the Developer's proof and the capture script's account of how its "person" was enrolled.

What I ran, in the worktree, in the foreground: the Gateway unit tests filtered to this phase's folds and routes
(83 tests, all passed), the client-core brief client tests (10, all passed), and the cockpit brief panel tests
(30, all passed). All three commands are subsets of the mission document's own check. I did not run the full
suites, the parked suites, the Python toolbelt tests or the web tests, per my instructions.

What I could not reach: the screenshots and the running-Gateway flow. Re-taking them needs a throwaway Gateway,
an enrolled browser and Chrome; I read the committed pictures' answers file and the script that produced it
instead, and the refusal answers quoted in the proof were checked against the sentences in the store and route
code, which is where they come from. No request was made against a real Gateway, so cross-account isolation is
read from the shared tenant machinery (the same every factory route passes through) and the per-account keying of
the registry and the store, not demonstrated by a live request from a second account. The flaky trigger test the
Developer's proof names is outside this phase and outside this review.
