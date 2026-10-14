# License Switching - review of phase 3 (the Licenses page of the Cockpit) as a whole

**Verdict: no blocking finding.** The page shows the owner everything `MISSION.md` section 3 asks for, every
verdict on it comes from the Gateway, no key, real address, machine name or account name is in its tests or
fixtures, and it was opened in a real browser against a Gateway built from this tree with throwaway storage: every
section drew, every control worked or was refused in the Gateway's own sentence, and a sign-in started from the page
was settled on screen. Eight findings follow, none blocking. The first is the one a review of a single pull request
could not see: two Gateway routes the page reads fold the same machine from two different pictures, so right after a
Switch now with a Director offline the page says both that the machines move and that nothing would be switched. It
should be fixed before phase 5's proof run, which will meet it the first time a Director is offline.

## Scope

Read on this tree, which is `origin/main` at `25e367758` (the merge of pull request 3963):

- `MISSION.md` (sections 3 and 5 in full), `cockpit-api.md` (sections 1 to 7 in full), `phase-3/README.md`,
  `phase-3/writes.md`, `phase-3/sign-ins.md`, `review-3963.md` and its answer, and `review-phases-2-and-4-whole.md`
  as the model of this kind of review.
- The page: `apps/cockpit/src/licenses/LicensesView.tsx`, `LicenseAccountCard.tsx`, `LicenseSignIns.tsx`,
  `licenseParts.tsx`, `licenses.css`, the route in `routes.tsx`, the menu row in `AppShell.tsx`, and the four test
  files and `fixtures.ts` for what they hold and what they contain.
- The client: `packages/client-core/src/licenses/licensesClient.ts` and its test; how a refusal becomes a sentence
  (`GatewayError.from` and `gatewayErrorMessage` in `api/client.ts`, `describeAndReport` in `reportClientError.ts`);
  the shared `ConfirmDialog`.
- The Gateway read model the page renders: every property of every type in `LicenseCockpitDtos.cs` and
  `LicenseSignInCockpitDtos.cs` (listed by script and compared one by one with what the page draws); the parts of
  `LicenseDisplayFold.cs` that write the sentences seen on screen; `LicenseEndpoints.cs` (which picture each route
  folds from); `LicenseSwitchService.SnapshotSwitchNowDecidesOn` and `LicenseDecisionEngine.SplitMachines`.
- The phase-3 proof programs: `qa-gateway/Program.cs` in full, `qa_run.py` in full, the other two by their headers
  and their "not proved" sections.

## What was run, and what was not

- **The page in a real browser against a Gateway built from this tree.** Not the phase's own scripts: a script of
  this review's own, in the session's scratch folder, started the phase's `qa-gateway` program (the product's
  `GatewayHost` in hosted mode, its whole storage under a throwaway folder in the temporary folder, five made-up
  accounts, no Director of the product, no real Claude account, no sign-in key anywhere), the Cockpit's development
  server forwarding the Gateway routes to it, and a headless browser enrolled with each seeded browser key. Every
  screenshot and every saved answer is in that scratch folder, outside the tree. What it did, in the order it ran:
  read the four routes as four accounts and saved each answer; opened the page as the owner and read every card, the
  schedule, the sign-ins, the approvers and the log off the screen; opened the Switch now confirmation and cancelled
  it; stored a renewal date (the card showed "Renews 17 Oct" from the Gateway's answer); opened the approver editor;
  opened the page as the account whose target is barred and read the barred sentence and the confirmation that then
  needs a pick (its confirm button was disabled until one was made); opened the page as the sign-ins account and
  started Add a spare for real - the stand-in Director held the command, the page showed "Signing in", the stand-in
  was told to finish, and within one poll the panel read "Signed in", the log held a "Sign-in" row and a "Spare
  added" row, and a fifth account card appeared; opened Renew now and cancelled it; opened the page as the account
  whose machines never reported (the Gateway's sentence, no tables); opened the page at 390, 768 and 1024 pixels
  wide and measured what overflows; sent a Switch now for real with both Directors offline and read the answer, the
  cards and the schedule after it; and stopped the Gateway under an open page and read what the page did.
- **The page's tests and the client's tests**, from this tree: `apps/cockpit` `src/licenses` 5 files, 81 passed;
  `packages/client-core` `src/licenses` 1 file, 27 passed. (The README's counts are older; the tests grew with the
  second and third pull requests.)
- **Not run**: the phase's own three proof scripts, the mutation script, the Gateway's unit tests, the parked suites,
  the lint, and the type check. No real Director, no real account, no hosted service, no person signed in through
  the login page (device keys were placed in the browser's storage, as the phase's scripts do).
- **No tracked file was changed.** Running `qa-gateway` wrote only its ignored `bin` and `obj` folders. This file is
  the only addition.

## Rule 7 across the whole page: what the page decides for itself

Every status, tone, level, verdict, sentence and offer that was looked for was found to come from the Gateway and to
be shown as sent: the account status pill and detail, the window bars (level word only, number clamped to the bar),
the reset sentences, the read age, the read error, the extra-usage sentence, the barred sentence, the renewal
sentence, the approver sentence and tone, each holding's expiry sentence and tone, signed-in sentence, read error and
failed-renewal sentence and tone, Renew now's offer and sentence, Switch now's offer and sentence, each target's
availability and reason, the machine status pill, last-report sentence, held sentence and switch-in-progress
sentence, the forecast's page sentence, per-account expected sentence and per-machine sentence, every log row's kind,
why, state, tone, summary, reason, timing and verification sentence, the approvers' status and sentence, the Add a
spare offer and sentence, the sign-ins in progress, and every refusal of every write (shown word for word through
`GatewayError.serverReason`). The page compares no percentage with 97 and no time with now; `LocalTime` only formats.

Conditionals in a view that put words to a state, in full:

| Where | What it decides | Weight |
|---|---|---|
| `LicenseAccountCard.tsx` line 175: `holding.isActive ? "Active" : "Spare"` | Writes the role word of a sign-in from a boolean. The Gateway sends `isActive` and no label for it. | The one real case. Not blocking; a `roleText` on the holding would end it (finding 5). |
| `LicensesView.tsx` line 447: `isAutomaticChoice && "The Gateway's choice"` | Writes a note from a boolean. | Same kind; part of finding 5. |
| `licenseParts.tsx` line 266: a window is a meter only when `utilization !== null` AND the level is `normal` or `at-limit` | The level word alone is meant to be enough (`cockpit-api.md` section 2). The second condition can only matter for an answer that contradicts itself (a level `normal` with no number). | Harmless; not a verdict of the page's own. |
| `LicenseAccountCard.tsx` line 185: `renewalFailedTone ?? "neutral"` | A default tone for a sentence sent without one. The contract says the three fields are null together, so it never runs. | Harmless. |
| `LicensesView.tsx` line 276: "4 Claude accounts on 2 machines." | A sentence with counts the page takes from the lists. | A client-written sentence, not a verdict (finding 6). |

Everything else a view branches on is a null check (draw the sentence when there is one), a boolean offer the
Gateway sent (`canSwitchNow`, `canAddSpare`, `canRenewNow`, `canChoose`), `isEmpty`, a style hook (`isInUse`,
`isStale` on the card, `toneClass`), or layout.

## Every served field against what is drawn

Compared from the property lists of the two contract files. Fields that are words beside a shown label (`status`,
`kind`, `state`, `why`, `level`, `verdict`, `reasonCode`, `readError`), raw times beside a shown sentence, and
identifiers are not listed: the contract says to show the text and may show the time. What is served and never
drawn anywhere on the page:

| Field | On | What the owner does not see |
|---|---|---|
| `signInExpiry` (text, tone, verdict, machine, time) | account | The one sentence naming the sign-in that expires FIRST across every machine ("Sign-in on the-windows-machine expires in 2 days 5 hours"). The card lists every holding with its own expiry instead, so the fact is on screen, scattered; the status pill "Sign-in expires soon" is the only summary. |
| `activeOnMachines`, `spareOnMachines` | account | Not drawn; the card rebuilds the same lists itself from `machines` (`heldOn`). Same facts from the same source, so no disagreement is possible today; two derivations of one fact all the same. |
| `rateLimitTier` | account | Never drawn. |
| `fiveHour.rateText`, `sevenDay.rateText`, `fiveHour.expectedText`, `sevenDay.expectedText`, `percentPerHour` | forecast account | The rate ("28.5% an hour") and the per-window forecast. The card draws only the account's one `expectedText`. `MISSION.md` section 5 asks for "the expected time to 97% at the current rate": the time is shown, the rate never. |
| `nextSwitchText`, `nextSwitchTargetAccountEmail` | forecast account | The per-account next-switch sentence - the one the hold fix appends "The automatic switch keeps failing and is held..." to. The page shows the forecast's one page-level sentence and the per-machine sentences, so the appended text is seen only when that account is the next to switch (as `review-3963-answer.md` says, left for the phase that works on the card). |
| `nextSwitch` (window, windowText, machines, expectedAtUtc) | forecast | Only its `nextSwitchText` is drawn, which says the same in words. |
| `isReadingStale` | holding | Never drawn, and has no sentence. |
| `message` | log row | Never drawn; the contract says `summary` is what is shown. |
| `sendCount`, `lastSentAtUtc` | log row | Never drawn, and they have no sentence. "How many times the command was sent" is nowhere on the page. |
| `isOpen`, `heldUntilUtc`, `finishedAtUtc`, `startedOnMachineAtUtc`, `finishedOnMachineAtUtc`, `exchangeMilliseconds`, `verifiedByUsageRead` | log row | Covered by `summary`, `timingText` and `verifiedText`. |
| `emptyText` | forecast, log (log's is drawn), approvers (drawn) | The forecast's empty sentence is never drawn: when the overview is empty the page shows the overview's sentence and renders no schedule at all. Same sentence, so nothing is lost. |
| `canApproveNow`, `machineName`, `osUser`, `machineId` | approver | Covered by `label`, `statusText` and `text`. |

Every sentence the page draws comes from the Gateway except these, which the page writes itself: the subtitle ("Your
Claude accounts, which machine is on which, and when they switch." / "N Claude accounts on M machines."), "Updated"
before the time, the role words "Active" and "Spare", "The Gateway's choice", the four confirmation titles ("Switch X
now?", "Add a spare on M?", "Renew the sign-in for X on M?", "Remove the approver of X?"), the field labels of the two
editors, every button label and busy label, the column headings and section headings, the bar labels "5-hour window"
and "Week", the accessibility labels, and the client's own sentences for an answer that lost its list or its sentence
("The Gateway answered /gateway/licenses without an 'accounts' list."). None of them judges a state.

## The page against `MISSION.md`

| The owner must see | Where it is | Seen in the run |
|---|---|---|
| Each account's plan, 5-hour use, weekly use, when each resets | Card: `planText`, two bars with `utilizationText` and `resetsText` | yes |
| Renewal date, and set it | Card: `renewsText`, "Set renewal date" editor, `PUT .../accounts/renewal` | yes; stored and shown from the answer |
| Which machines are on which account | Card: one line per holding, machine label with Active or Spare; Schedule: "On now" column | yes |
| The schedule: on now, next, roughly when | Schedule table from the forecast; the page-level `nextSwitchText` | yes |
| The log: when, machines, from, to, why, whether it worked | Switch log table, one row per machine, `kindText`, `whyText`, state pill, summary, reason, timing, verification; "Show older" | yes |
| Held machines | Schedule: `heldText` under the machine's status | by test only (no hold was seeded) |
| Barred accounts | Card: `barredText` with the time in the viewer's zone | yes |
| Sign-ins about to end | Status pill "Sign-in expires soon"; each holding's `expiryText` in its tone; a failed renewal in red | yes |
| Switch now | Card button where `canSwitchNow`; confirmation with the targets; the answer panel following the log by `entryId` | yes, accepted (pending, Directors offline) and refused by test |
| Approvers | Approvers table, Set / Change / Remove, `approverText` on each card | yes (the empty sentence and the editor; setting one was not sent) |
| Add a spare, Renew now | Sign-ins table and each holding; both routes | Add a spare yes, end to end; Renew now opened and cancelled |

Empty and failed states: no machine reported gives the Gateway's one sentence and no tables (seen); a failed read of
any one of the four polls keeps the other three on screen with a banner and "Try again" (seen for all four at once
when the Gateway was stopped: the cards stayed, the "Updated" time stayed at the last good answer, which is true);
switching off everywhere is the empty case, because the routes are always mapped and a Director that is off never
reports.

## Findings

### 1. Not blocking, fix before phase 5's proof - the overview and the forecast fold a machine mid-switch from two different pictures, and the page shows both

**Where.** `src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs` line 128 (`ApplySwitchNow` is given
`LicenseSwitchService.SnapshotSwitchNowDecidesOn(picture)`) against line 513 and `Api/LicenseEndpoints.cs` line 139
(the forecast is folded from `picture.Snapshot`); `LicenseSwitchService.cs` lines 601 to 640;
`LicenseDecisionEngine.SplitMachines` line 447. Seen on screen in the run (scratch folder,
`owner-switch-now-answer.png`).

**What.** A switch row that is `pending` with reason `director-offline` is one the Gateway knows it never sent. For
Switch now, such a row does not hold its machine back (`SnapshotSwitchNowDecidesOn` leaves `HasOpenSwitch` false),
which is right and is what `cockpit-api.md` section 6 promises. The forecast, on the same request cycle, is folded
from `picture.Snapshot`, where `LicenseSnapshotSource` marks EVERY open row as mid-switch, so `SplitMachines` skips
the machine with "a switch sent to it has not finished". After one Switch now with both Directors offline the page
read, at the same moment:

- the account card: "Switch now - Moves the-mac and the-windows-machine to account-b@example.com.";
- the page-level schedule sentence: "account-a@example.com is expected to reach 97% of its 5-hour window in about 1
  hour 49 minutes. No machine on account-a@example.com can be switched now: every machine on it is stale or already
  mid-switch. Nothing would be switched.";
- each schedule row: "Waiting to switch from account-a@example.com to account-b@example.com: the machine's Director
  is not connected." and under it "Would not be switched now: a switch sent to it has not finished."

Two of the three are wrong in fact: nothing was sent, and the minute check will go on trying. The owner cannot tell
which sentence to believe, and the whole point of the page is that it never has to be worked out by the reader.

**Why it is not blocking.** No key is at risk and nothing is switched wrongly; the automatic check and Switch now
both do the right thing. It is a display contradiction, and it appears only while a Director is offline with a switch
waiting for it - which is exactly the state the seat that proves phase 5 will produce first (a machine switched off, then
Switch now).

**Proposed.** One picture for both routes: fold the forecast's `moves` (`LicenseDisplayFold.cs` line 520) and
`EveryMachineHeldText` from `SnapshotSwitchNowDecidesOn(picture)` too, so a never-sent row neither skips the machine
nor is called "sent". The schedule row then reads "Next: account-b@example.com, in about 1 hour 49 minutes." beside
its "Waiting to switch..." sentence, which is what is true. A test in `LicenseDisplayFoldTests`: a pending
`director-offline` row, the overview offers Switch now for the account, and the forecast's machine text for that
machine is not a "would not be switched" sentence.

### 2. Not blocking - the Switch now confirmation says nothing about a target the owner picks by hand, and nothing about why a choosable target was not the Gateway's choice

**Where.** `LicensesView.tsx` lines 432 to 450; `LicenseSwitchTargetDto` (no sentence on a choosable target). Seen
in the run (`barred-switch-now-dialog.png`).

**What.** For the account whose only target is barred, the confirmation reads "No other account has room for the
machines on account-a@example.com. A target can still be chosen by hand." and then lists `account-b@example.com`
as a bare radio button: nothing says it is the barred one, nothing says which machines would move to it, and once it
is picked the Gateway's sentence above disappears (by design, since it describes the Gateway's own choice). The card
behind the dialog carries the barred sentence, but the dialog is what the owner reads before pressing a button that
moves every machine. This is the item every phase-3 record lists as still owed ("a sentence per target in
`switchTargets`"); it is still owed on this tree.

**Proposed.** A `text` on each `switchTargets` entry, folded on the Gateway: for a choosable target "Moves the-mac
and the-windows-machine to account-b@example.com. It is left out of the automatic choice until its 5-hour window
resets." and for one that is not, the existing `unavailableText`. The page then shows the sentence of whichever
target is picked, and "The Gateway's choice" becomes part of that sentence rather than a note the page writes.

### 3. Not blocking - one outage, four identical banners

**Where.** `LicensesView.tsx` lines 288, 320, 343 and `LicenseSignIns.tsx` line 222. Seen in the run
(`owner-gateway-gone.png`): when the Gateway went away under an open page, four banners appeared, each "DevThrottle
could not complete that - the Gateway hit an internal error (error 500). Try again", one above the cards, one above
the schedule, one above the approvers and one above the log.

**What.** Four polls each report their own failure, which is right when ONE of them fails (the other three stay on
screen and the banner says which part is stale). When all four fail for one reason the owner reads the same sentence
four times and four "Try again" buttons that do four separate things. (The "error 500" is the development server's
proxy answering for a dead Gateway; against the hosted service it would be the unreachable sentence. The count is
what matters, not the words.)

**Proposed.** When every poll's error is the same sentence, draw one banner at the top whose "Try again" refreshes
all four; keep the per-section banner for the case where only that section failed.

### 4. Not blocking - two Gateway sentences on the never-read card still disagree, and two values still stand alone on a line

**Where.** `LicenseDisplayFold.cs` (the account's `statusDetail` and the holding's `readErrorText`; `extraUsage.text`
and `readAgeText` for an account never read). Seen in the run (`owner-full.png`, the account-d card), and reported
by `phase-3/README.md` on 13 October as findings 1 and 2 of the first run.

**What.** The card reads "No usage reading has arrived for this account yet." and, three lines down, on the holding:
"The machine could not reach the provider. The numbers shown are the last ones read." There are no numbers shown.
Below the detail, "Not known" (extra usage) and "Never read" (read age) sit alone on their lines, where neither says
what is not known or not read. The page draws them exactly as given, which is right; the sentences are the Gateway's
to fix. They were reported to the Delivery Lead by the first pull request's record and are unchanged on this tree.

**Proposed.** In the fold: the read-error sentence for a holding with no reading at all should not promise numbers
("The machine could not reach the provider; this sign-in has never been read."); extra usage and read age should be
full sentences when they stand alone ("Paid extra usage not known", "Never read on any machine").

### 5. Not blocking - two words the page writes from a boolean belong in the fold

**Where.** `LicenseAccountCard.tsx` line 175 ("Active" / "Spare" from `isActive`); `LicensesView.tsx` line 447 ("The
Gateway's choice" from `isAutomaticChoice`).

**What.** `cockpit-api.md` section 1 rule 3 says words and their labels travel together, and section 4 says of
`verifiedByUsageRead` "never put words to the boolean". These two are the only places the page does. They are
harmless today (a boolean has two words and the page knows both), but they are the pattern the rule exists to stop:
the day a third role exists (a sign-in being replaced, say) the page will call it one of the two it knows.

**Proposed.** `roleText` on `LicenseMachineHoldingDto` ("Active" / "Spare"), and finding 2's per-target sentence
carries "the Gateway's choice" for the automatic target. Then `isActive` and `isAutomaticChoice` stay as words for
tests and style hooks, which is what they are for.

### 6. Not blocking - fields served with no sentence and never drawn

**Where.** `LicenseLogEntryDto.SendCount`, `LastSentAtUtc`; `LicenseMachineHoldingDto.IsReadingStale`;
`LicenseAccountDto.RateLimitTier`; the forecast's per-window `RateText`, `ExpectedText` and `PercentPerHour`.

**What.** The table above lists everything served and not drawn. Most of it is covered by a sentence the page does
draw. These are not: how many times a switch command was sent and when last (the contract calls them out as facts of
the row, and nothing on the page says them); whether a holding's reading is stale (no sentence exists for it); the
rate limit tier; and the rate of use per window, which `MISSION.md` section 5 names ("at the current rate") and which
the owner never sees - only the time it leads to.

**Why it matters.** A field with no sentence and no reader is either a sentence the fold owes or a field the contract
should drop; left as it is, it is the first place a later page change will write its own words.

**Proposed.** Draw `rateText` beside each window on the card (one short word each: "28.5% an hour", "Not rising", "No
rate yet"; the per-window `expectedText` is then not needed). Give `sendCount` a sentence on the row ("Sent 3 times,
last 2 minutes ago") or fold it into `timingText` for open rows, and draw it. Drop `isReadingStale` and
`rateLimitTier` from the contract, or draw them.

### 7. Not blocking - who approves an account cannot be set while no machine has reported

**Where.** `LicensesView.tsx` lines 292 to 295 (the empty branch renders nothing but the sentence) and
`LicenseSignIns.tsx`. Carried from `review-3954-answer.md` finding 2 and listed as still owed in `sign-ins.md`.

**What.** The approvers are their own route and their own poll, and `PUT /gateway/licenses/approvers` accepts a
machine that has not reported. But the "Who approves a sign-in" part is drawn inside the non-empty branch, so an
owner setting the fleet up in the order the mission describes (set the approver, then add spares) cannot set the
first approver until a Director has reported. The sentence the owner sees instead says nothing about approvers.

**Proposed.** Draw the approvers part in the empty branch too (it needs only `approvers.data`), under the Gateway's
empty sentence.

### 8. Not blocking, a question for the Architect - the phone has no Licenses page, and the Cockpit is not built for a phone-width window

**Where.** `apps/mobile/src` (no file mentions licenses); `apps/cockpit/src/styles.css` (the shell's one width rule
hides a clock at 900 pixels; the left rail never collapses); `licenses.css` (no width rule; cards at least 380 pixels
wide). Measured in the run: at 390 pixels wide the Cockpit's main pane itself overflows (408 against 170 pixels),
before the page is reached; at 768 and 1024 only the two tables scroll inside their own wrap, which is by design.

**What.** `CLAUDE.md` rule 8 binds Settings, which this page is not, and `MISSION.md` asks for the Cockpit page only,
so nothing here is a defect of phase 3. But the mission's why is the owner asleep in the middle of the night, and the thing he reaches
for then is the phone. Today the phone shows nothing of this: not that a switch happened, not that no account had
room, not that a sign-in is about to end. The email is the only reach the phone has.

**Proposed.** Not for this phase. A question for the Architect, with a recommendation: a read-only Licenses screen on
the phone (the cards' status pills, the page-level next-switch sentence, and the newest log rows; no writes), built
in `client-core` so the two surfaces share it as Settings does. If the answer is no, say so in the mission record so
phase 5 does not look for it.

## The other things the brief asked for, as checked

- **No key, real email address, machine name or account name in the tests or fixtures.** Every address in
  `apps/cockpit/src/licenses`, `packages/client-core/src/licenses`, the three phase-3 proof scripts and the 46
  tracked license test files of the Gateway and the Director is `*@example.com` (one test spells it
  `Account-A@Example.com` on purpose, for the capitals); every machine is `the-windows-machine`, `the-mac`,
  `the-laptop`, `the-linux-machine`, `machine-of-one` or a one-letter name; every user is `owner`, `second` or a
  one-letter name. No token-shaped string, no `Bearer` value but the seeded key in the proof scripts, no host name.
  The `qa-gateway` program makes up every number and time and holds no sign-in.
- **The fixtures are the Gateway's own answers**, copied by `copy_fixtures.py` from `cockpit-api.md`, and
  `fixtures.test.ts` holds them to it.
- **A machine is called by its label everywhere**: the cards, the schedule, the sign-ins table, the approvers, the log,
  the confirmations and the answer panels. The one place a name is built from parts is a React key
  (`machineName|osUser` for a skipped machine, which has no `machineId`), which is never shown.
- **The log is keyed on `entryId`** and merged by it, so the repeated row the contract warns of is harmless; an
  answer panel finds its row by `entryId` and never resends.
- **Nothing is sent without a target** where the Gateway chose none: the confirm button was disabled in the barred
  case until a target was picked.
- **Every write is `application/json` with `Accept`**, through one header function, and the three readers refuse an
  answer that lost its list or its sentence rather than drawing an empty account.
- **Menu and route**: "Licenses" sits in Set up after Directors, with the key icon, at `/licenses`, and
  `AppShell.test.tsx` and `routes.test.tsx` hold both.

## Not reviewed

- The Gateway's fold sentences beyond the ones seen on screen or named above; `LicenseForecaster`'s numbers.
- The phase's three proof scripts were not re-run; this review ran its own. The mutation script was not re-run.
- The Gateway's unit tests, the parked suites, the lint and the type check were not run on this tree.
- A person signed in through the login page; the hosted service; a real Director; a real Claude account; the approval
  relay (the page never sees it).
- A machine that is held (no hold was seeded; the page's tests hold the drawing), a machine not reporting, and a
  switch that a machine carries out, fails or is left out of by the real Gateway (no Director of the product was
  connected).
- Accessibility beyond reading the labels and roles in the source.
- The stylesheet beyond the widths measured and the screenshots looked at.
