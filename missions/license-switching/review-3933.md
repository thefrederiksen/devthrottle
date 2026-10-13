# Review of pull request 3933 - the Licenses page of the Cockpit

13 October 2026. Reviewed at commit `dd193bb36` (branch `license-switching/phase-3-licenses-page`, 0 commits behind
`origin/main`, three commits ahead of it).

## The answer first

The page itself does what the mission asks and I found no defect in how it behaves. It renders the Gateway's words
and tones, it never draws "not read" as zero, it sends nothing for Switch now until the person confirms, and the
note's claim of a run against a real local Gateway holds from what is committed.

**One finding should be fixed before or on the day of the merge: the tests do not hold several of the things the
note says they hold.** Eight wrong changes I made to the page left all 21 of its tests green (finding 1). Two
smaller findings follow. None of the three is a defect a user would see today.

## The six questions

| Question | Answer |
|---|---|
| Does a view decide what a state means? | No, with one small exception: the page colours a read error red by itself (finding 2). Status, tone, window level, expiry verdict and the Switch now offer are all the Gateway's. |
| Is "not read" ever drawn as zero? | No. A bar is a filled meter only when there is a number AND the level is `normal` or `at-limit` (`licenseParts.tsx` line 48). Everything else is hatched with the Gateway's text. Seen in `qa/accounts-with-data.png` on account-d. |
| Empty, loading and error states? | All three are in the code for the overview and for the log, and the forecast has an error banner. Empty, overview error and forecast error are tested. Loading and a failed log read are not tested (finding 1, R6 and R7). |
| Is Switch now behind a confirmation that names what moves? | Yes for the Gateway's own choice: the dialog shows "Moves the-mac and the-windows-machine to account-b@example.com." and nothing is sent before Confirm. For a target picked by hand the dialog names the account and the target but no machine (finding 3). |
| Are the tests real? | The eighteen wrong changes in `mutations-output.txt` are real and I reproduced one as a control. But eight more wrong changes survive (finding 1). |
| Does the "real local Gateway" claim hold? | Yes, from what is committed. See "The run against a local Gateway" below. I did not run it again. |

## Finding 1 - eight wrong changes leave every test green

I copied the committed sources to a scratch folder outside the worktree, made one wrong change at a time there, and
ran `npx vitest run src/licenses` (21 tests). The control is the author's own change number 3, to show the copy
detects a red at all.

```
CONTROL (the author's number 3) the status word in place of its label | ran 21 | red 2
R1 every pill is drawn in the good colour, whatever tone the Gateway sent | ran 21 | red 0
R2 every schedule row shows the FIRST machine's forecast | ran 21 | red 0
R3 every card shows the FIRST account's forecast sentence | ran 21 | red 0
R4 a read error is not drawn, on the card or on the machine's line | ran 21 | red 0
R5 a machine is called by machineName, not label, on the cards, the schedule and the log | ran 21 | red 0
R6 a failed read of the switch log shows nothing at all | ran 21 | red 0
R7 the page never shows Loading | ran 21 | red 0
R8 how old the numbers are is not drawn | ran 21 | red 0
RESTORED; baseline: ran 21 red 0
```

What each one means, and why it survives:

- **R1 - the colour of every status is unproven.** With this change an account "At limit" and a switch that "Failed"
  are drawn green, and the tests pass. The tests check `data-tone`, an attribute the page copies straight from its
  input and that the stylesheet never reads; the colour comes from the class `lic-tone-*` (`licenses.css` lines 19
  to 53), which no test of a pill looks at. Only the two sign-in expiry tests look at a class.
- **R2 and R3 - a row can show another row's forecast.** The schedule then tells every machine the first machine's
  next account and time, and every card the first account's "expected to reach its limit" sentence. They survive
  because the fixture gives both machines the same sentence, the schedule test reads only the first row, and no test
  reads `expectedText` on a card.
- **R5 - "a machine is called by the Gateway's label" is unproven in all three places.** The note says this of the
  cards, the schedule and the log. In every fixture `label` equals `machineName`, so no test can tell them apart;
  the one assertion (the row does not contain "owner") passes for either field. The case the label exists for - two
  users of one computer - is in no fixture.
- **R4 - a read error can vanish.** No fixture carries a `readErrorText`, so the page could stop saying a machine
  could not read an account and nothing would go red.
- **R6 - a failed log read can be silent.** With the banner gone, a failed first read of the log leaves the heading
  "Switch log" with nothing under it. The overview's and the forecast's banners are tested; the log's is not.
- **R7 - Loading is never looked for.** Rule 1 of the project (immediate feedback) rests on it.
- **R8** is the least of them: the line saying how old the numbers are can be dropped unseen.

What would close it, all small: assert the class on the status pill and the log pill; give the two machines and two
accounts in the forecast fixture different sentences and read the second row and a card's `expectedText`; add one
fixture machine whose `label` differs from its `machineName`; one fixture with a read error; one test of a failed
log read; one assertion that Loading is on screen before the first answer.

## Finding 2 - the page decides that a read error is red

`LicenseAccountCard.tsx` lines 62 and 73 give `readErrorText` the class `lic-tone-bad`. The Gateway sends no tone
for a read error. The effect is on screen in `qa/accounts-with-data.png`: on account-d the Gateway's verdict is
"Stale" in its warning tone, and beneath it the page has painted "The machine could not reach the provider..." in
the bad tone - the page's own severity sitting above the Gateway's. It is the only place a colour on the page does
not come from a tone word (rule 3 of `cockpit-api.md`). Either the fold sends a tone for it, or the page draws it
in the dim text colour like its neighbours. Small, and it harms nobody today.

## Finding 3 - Switch now for a target picked by hand names no machine

`SwitchNowChoice` (`LicensesView.tsx` line 278) shows the Gateway's sentence only while its own choice is the one
selected, which is right: the sentence names the Gateway's target. But once another target is picked, the dialog
holds the title "Switch account-a@example.com now?" and the selected radio button, and no sentence saying which
machines move. A test pins exactly that ("stops showing the sentence that describes the Gateway's own choice").

This is not a defect the page can fix alone - the Gateway sends one sentence, for its own choice - and it cannot
do harm today because the route does not exist. It belongs in the follow-up: a sentence per target in
`switchTargets`, so the confirmation always says what moves. Two neighbours of it, also for the follow-up:

- When `canSwitchNow` is true and `switchNowTargetAccountEmail` is null (a state `cockpit-api.md` section 2 names),
  Confirm is enabled with no target selected and posts a null target. No test draws this state.
- The panel that shows the accepted answer calls a machine left out by `machineName` and uses it as the list key
  (`LicensesView.tsx` lines 313 to 316). Rule 9 says label. The note already says this shape is a guess; the test
  for it hand-builds the answer, so it proves the drawing and nothing about the route.

## The run against a local Gateway

I did not run it (see scope). From what is committed, the claim holds:

- `qa-gateway/Program.cs` starts the product's `GatewayHost` and seeds through `LicenseUsageIngest.Ingest` and
  `LicenseStore`; the four members it reaches by reflection exist on `origin/main` (`GatewayHost.cs` lines 405,
  408, 411 and 6747), and the three routes are in `LicenseEndpoints.cs` there.
- `qa_run.py` sets no request interception; the development server forwards `/gateway` to the Gateway
  (`vite.config.ts` line 82), and the key goes in as `cc.deviceKey`, the name the client reads.
- The saved answers agree with the seed and with each other: account-a at 58% then 64% twenty minutes apart is the
  forecast's "18% an hour"; the two machine identifiers are the same in all three answers; the failed switch is
  dated two hours before `asOfUtc` and the good one five; account-d has null numbers and level `not-known`.
- The screenshots agree with the answers: four cards, two hatched bars, "Updated 13 Oct, 02:24" against an
  `asOfUtc` of 06:24 UTC, two log rows with one failed, and "Not found: POST /gateway/licenses/switch-now".
- The note says plainly what the run is not: no Director, no real account, switch rows written through the store
  rather than by the decision engine, Switch now answered 404. I found nothing it overstates.

The two oddities in the Gateway's sentences that the note reports (the never-read account) are real and visible in
`qa/answer-overview.json`; they are the Gateway's to fix, as the note says.

## Smaller things, not findings

- The schedule's "Next account" cell is blank both when the forecast has not arrived (or failed) and when the
  Gateway says there is none. The error banner covers the failure; nothing marks the short wait.
- `docs/CockpitVisualStyle.md` asks for one primary button per surface; each card that offers Switch now has one.
  With two accounts in use there are two. The stylesheet itself uses only tokens, no colour of its own.
- After many new log rows arrive while older rows are on screen (more than one page in one poll), the list would
  have a gap between the newest page and the rows read earlier. At 50 rows a page this needs 50 switches in 30
  seconds or a tab left hidden for a long time; I do not think it is worth code.

## Scope

**Read:** `MISSION.md` sections 3 and 5 (and the rest), `cockpit-api.md` in full, `phase-3/README.md`, rule 7 of
`CLAUDE.md`, the button, surface and confirmation sections of `docs/CockpitVisualStyle.md`; every source file of the
change - `LicensesView.tsx`, `LicenseAccountCard.tsx`, `licenseParts.tsx`, `licenses.css`, `licensesClient.ts`, both
test files, the route, menu and icon changes - and `ConfirmDialog.tsx`; `mutations.py` and its output; `qa_run.py`,
`qa-gateway/Program.cs` and its project file, `qa-run-output.txt`, the three saved answers, and three screenshots
(`qa/accounts-with-data.png`, `qa/switch-now-confirmation.png`, `qa/log-with-failed-switch.png`);
`layout-check/layout_check.py`.

**Ran:** the tree freshness check (0 behind); in the worktree, the whole Cockpit suite (103 files, 1075 passed, 0
failed - the note's number) and the client's 9 tests (9 passed); in a scratch copy outside the worktree, the control
and the eight wrong changes above.

**Did not run, could not reach:**

- `qa_run.py` and the QA Gateway: it rewrites the committed answers and screenshots and builds the Gateway inside
  the tree, and I was told to change no existing file. My judgement of that run is from its committed output only.
- `mutations.py` in the worktree, for the same reason; I reproduced one of its eighteen changes in the copy and took
  the other seventeen from its committed output.
- The type check, the lint run, and the rest of the client-core suite.
- The page in a browser, the six `layout-check` screenshots, `fixtures.ts` line by line, `qa/empty.png`,
  `qa/not-read.png`, `qa/switch-now-no-route-yet.png`, `qa/answer-overview-empty.json`.
- The Gateway's own code (`LicenseDisplayFold` and the routes) beyond confirming the members and routes exist: this
  review is of the page.
- Switch now accepted or refused by a real route, a phone-width window, the hosted service: none exists or was in
  reach.

The worktree is as I found it apart from this one new file.
