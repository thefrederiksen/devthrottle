# Alerts - Phase 5 (the phone screen redesign) review

Reviewer: a separate review session (70a8bcab), 25 October 2026. Pull request 4477, branch
`alerts/phase5-phone-screen`, one commit (`365ddfb70`) on top of origin/main `9e9e5c9dc`, read as
`git diff origin/main...HEAD` in the worktree `D:\ReposFred\devthrottle-alerts-p5`. The tree was 0 commits behind
origin/main when read, and `git status` was empty before and after everything below. No tracked file was edited;
`npm ci` was not run (the tree's `node_modules` was already present, so the suites below ran without it).

## Verdict

**Approve. Three findings, none proving harm that should hold the merge.** Judged against the owner's complaint,
the phone screens in `qa/` are what he asked for: a full, opaque screen with a back arrow and the title, nothing
greyed behind it, the question as the card's heading, who asked and why once, and the answer one tap away on the
card. Nothing is cut short, nothing repeats, nothing overflows at 390 or 360 px, and the pages look like one
product. The three findings are a wrong command in the committed README (a tab ate a backslash), the restart
alert naming its asker without a session number while the other two kinds carry one, and one vacuous
measurement line in the phone run log. Six observations follow for the Delivery Lead.

## Scope

**What I read.**

- The mandate `D:/ReposFred/_briefs/alerts-phase5-developer-2026-10-25.md`, the mission document, the record, the
  pull request body, and the Phase 4 review for the shape of this one.
- The whole diff, 109 files. In full: the Gateway (`AlertDtos.cs`, the new `AlertWords.cs` and
  `AlertWordsTests.cs`, the three alert sources and their tests, the two push notifier tests, the endpoint and
  tenant isolation tests); client-core (`AlertsList.tsx` new, `AlertsTray.tsx`, `AlertDetail.tsx`,
  `AlertsBell.tsx`, `alerts.css`, `alertsFixtures.testkit.ts`, `package.json`, and the whole of
  `Alerts.test.tsx`); the phone (`pages/Alerts.tsx` new, `components/AppBarAlerts.tsx` and its test, `routes.tsx`,
  `styles.css`, `HomeAlerts.test.tsx`, `pushSw.test.ts`); the Cockpit (the four touched tests - its shell code is
  unchanged); and the QA folder: `README.md`, `run-log.txt`, `tools/host/Program.cs` (the seed data), and the
  diffs of `qa.py` and the five steps files.
- The code the diff leans on, on origin/main: `AlertsService.cs` (how the sources are composed),
  `DirectorRestartRequestService.RequesterName` (the only writer of the name the restart source now requires),
  the header of `DirectorRestartRequestStore.cs` (in-memory, never persisted), the three unchanged action
  blocks (`RestartRequestActions.tsx`, `SecretTransferActions.tsx`, `MessageRequestActions.tsx`), and
  `AppShell.tsx` / `CockpitAlerts.tsx` (the Cockpit still mounts the bell with `openAlertId` and `onOpenChange`).
- Every phone screenshot (27) and the Cockpit ones that carry the new content (11), opened and looked at against
  the state the README claims and against the owner's complaint, and for anything private.

**What I ran, in this tree, on the branch head.**

| Run | Result |
|---|---|
| client-core: `vitest run src/alerts src/restart src/secrets src/fleet` | 14 files, 143 tests passed (Alerts.test.tsx: 34) |
| phone app: `vitest run` (whole workspace) | 39 files, 199 tests passed |
| Cockpit: the four touched test files | 4 files, 31 tests passed |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert\|FullyQualifiedName~PushNotifier"` | 133 passed, 0 failed, 0 skipped |

**What I could not reach.** The local Gateway rig was not rerun; the screenshots and `run-log.txt` are the
Developer's run and I judged them as artifacts. `npm run typecheck`, `npm run lint`, the whole Cockpit and
client-core suites, `Gateway.Tests` and the DatabaseTests project were not rerun here; the pull request body and
README report them and nothing I read contradicts those numbers. The hosted Gateway was not touched. A real
phone was not used; the screenshots are a 390 x 844 and 360 x 780 browser viewport.

## The owner's complaint, checked against the screenshots

- **A clear full screen, no greyed background.** `phone-alerts-three-need-you-earlier-collapsed`, `-one`,
  `-zero`, `-twelve`: an app bar with a back arrow and "Alerts", the page's own background, no shade, no sheet,
  no handle. The run printed `dialog: False shade: False` on opening it and the address changed to
  `/mobile/alerts`. `AppBarAlerts.test.tsx` and `HomeAlerts.test.tsx` prove there is no dialog on the phone.
- **What needs an answer is obvious.** "NEEDS YOU" first; each card has a red left edge, the question in bold
  as its heading, one sentence under it, the expiry, and the two buttons. Earlier is a dim collapsed row below.
- **Answered in one tap from the card.** Approve/Deny and Accept/Decline sit on the card
  (`phone-answer-message-request-allowed`, `phone-answer-secret-transfer-approved`). The restart keeps its two
  steps on the card (`phone-answer-restart-step-1-confirm`: the Gateway's accept sentence, "Yes, restart it" in
  red, "Not now"); `AlertsList_Restart_AcceptedFromTheCard_InTwoSteps` proves nothing is sent on the first tap.
- **No text cut short, nothing repeated.** `alerts.css` no longer contains `text-overflow: ellipsis` (the test
  asserts it); the 300-character reason is on the card in full at 360 px (`phone-360-overflow-cards`). The reason
  appears once per card: the quoted line under the summary is gone, and the chip tests in the Cockpit assert the
  reason occurs exactly once.
- **Nothing overflows.** The run measured every element on 13 screens with `"bad":[]` and printed the content
  width beside it (328 px at 360, 280 px in the popover), so an empty result is not an empty screen. The 46-character
  launcher version and the long guarded-restart word wrap inside the card in `phone-360-overflow-restart-details`.
- **Professional.** Yes. The cards, the section headings, the quiet Earlier rows and the Cockpit popover read as
  one design; the `--alerts-*` variables are the only thing the phone sets (`styles.css` diff), so the two
  surfaces cannot drift.

## The answer paths, seen, clear, and the restart two-step

All kept, by test and by screenshot: the secret transfer is answered with `where: "phone"` from the phone's
card (`AlertsList_SecretTransfer_ApprovedFromTheCard...` asserts the body) and with the phone device's own key in
the QA run (the README explains why two devices); the message request calls its existing answer route; the
restart's accept and decline call theirs with every Gateway check intact (the action blocks are unchanged).
Showing the list marks only unseen alerts seen (`AlertsList_Showing_MarksOnlyTheUnseenAlertsSeen`). Clear one,
clear all from Earlier's own header without opening it, a 409 shown verbatim and removing nothing, a failed read
keeping the last-known list, the refusal sentence dropped once the alert is answered (Phase 4 observation 1,
tests kept) - all pass. The Cockpit popover keeps its focus trap, Escape and focus return; the phone page puts
focus on its title on open, on the alert for a deep link, and back on the bell on return
(`Home_Bell_OpensTheAlertsScreen_AndBackPutsFocusOnTheBellAgain`, `phone-back-to-session-list`).

## Rule 7 (the Gateway owns the wording)

Every sentence on a card is the Gateway's: title, summary, status, expiry, the live-sessions sentence, the accept
sentence, the Details lines. The new `AlertWords` lives on the Gateway and the three sources call it; the tests
pin the owner's own example ("Release Manager (121) wants to message Release Manager (125)" with "Its reason:
..."). The client adds no verdict: it joins the Gateway's status word and title for the answered line, counts
Earlier for its header, and lays out. One client-written line remains, "Asks for: one message and a reply", in
`MessageRequestActions` since issue 4373 - not this phase's change, and `requestedAmountWords` is a label for an
enum value, not a ruling.

## Private names in the screenshots (public repository)

None. Every machine, session, repository and account name is the QA host's seed: QA-DESK, QA-PHONE, mac-mini,
linux-rig, build-box, lab-02, studio-pc, win-tester, devthrottle-mac-mini-build-02, `example-app`, "Account 1",
"Release - Manager" and the other three sessions. No email address, token or key value appears. The secret ENTRY
names `vercel-bypass` and `posthog-key` were already in the public fixtures and the Phase 4 screenshots on
origin/main before this phase; no value is shown anywhere.

## Findings

### Finding 1 - the committed QA README prints a command that does not exist (minor, documentation)

`docs/missions/alerts-2026-10-24/qa/README.md` line 111, in the section 7 table: the backslash in
`scripts\test-database.ps1` became a literal TAB character (`scripts<TAB>est-database.ps1`), so the one command the
row exists to give the reader is wrong. `grep -P "\t"` finds exactly that one line. **Harm:** someone repeating the
database proof copies a path that is not there. **Fix:** write it as `scripts/test-database.ps1`, or escape the
backslash; one character.

### Finding 2 - the restart alert names its asker without a number, the other two kinds with one (minor, wording)

The mandate asks for every session reference to read "role (number)". The secret transfer and message request
do ("Rig Developer (142) asks", "Release Manager (121) wants to message ..."); the restart reads "Rig Developer
asks: ..." (`RestartRequestAlertSourceTests`, `phone-alerts-one`). The README discloses it: the request stores
"name (short id)" and no number. **Harm:** a mission commonly has two or three sessions with the same mission and
role ("Alerts Developer asks: ..."), and the number is the only thing the owner can act on
(`session-numbers`); a restart asked by one of them reads the same as one asked by another. **Why it is cheap:**
`DirectorRestartRequestService.RequesterName` already holds the `SessionDto` when it writes the name (origin/main
lines 512 to 519), so the number can be written beside the name there without a new lookup, and
`AlertWords.FromRestartRequester` can read it the way `FromSecretAskedBy` reads the secret's form. Whether that
goes in this pull request or a follow-up is the Delivery Lead's call; it touches a string the Director's drain
step also prints ("asked for by ...").

### Finding 3 - a measurement in the phone run log reads as a pass and measures nothing (minor, proof)

`run-log.txt`, the phone `mixed` pass: each of the three answers prints `badge before: none after: none`. The
phone's Alerts screen has no bell (by design), so the step measured an element that is not there, before and
after; the line would print the same if the count had not dropped. On the desktop the same step printed
`3 after: 2`, `2 after: 1`, `1 after: none`, which is the evidence. **Harm:** a future phone run in which the count
did not drop would print an identical line and pass by absence. The real proof on the phone is there beside it
(`needsYou` going 3 to 2 to 1 to 0, and `phone-back-to-session-list` showing the bell at 1); the step should print
that and drop the badge line on the phone, or read the badge after Back.

## Observations (no change asked)

1. **One empty requester name would take the whole list down.** `RestartRequestAlertSource.AskedBy` now throws
   when `RequestedBySessionName` is blank, and `AlertsService.Compose` reads the sources in one `SelectMany`, so
   one such record fails `GET /gateway/alerts` for the account: the bell says "Could not load" and the secret and
   message alerts go with it. Unreachable today - `RequesterName` always returns a non-empty string, it is the only
   creator, and the store is in-memory so there are no old rows - but the blast radius is worth knowing before
   anyone adds a second creator or the tests' habit of `new DirectorRestartRequestDto { ... }` reaches product code.
2. **A deep link now drops the page's router state.** Phase 4 passed `state: location.state` when it cleaned
   `?alert=` off the address; Phase 5 replaces the address with the Alerts screen and its own state, and Back
   returns to the path alone. I did not sweep which phone pages read `location.state`; if one does, a push arriving
   while the owner is on it loses that state after Back.
3. **The "Failed:" and "Stopped:" lines are the rig's, and the line follows the Gateway.** The answered line is
   recomputed from the current list on every render, so on a real Gateway it will read "Approved: ..." and change in
   place if the status moves within its six seconds. The README explains both outcomes; nothing to change.
4. **The refusal sentence under an Earlier row sits at the page edge** (`phone-clear-refused-needs-an-answer`),
   not indented under the row it belongs to. Cosmetic.
5. **Gateway unreachable shows two lines for one loss on the phone** - the phone's amber "Bad connection" banner
   and the list's red line. Pre-existing from Phase 4, true, and not this phase's change.
6. **`AlertDetail` under an Earlier row still shows the machine-shaped labels** ("Asking session: 121 Release -
   Manager", "Asked by: Session 142 "Rig - Developer"") inside the collapsed Details. That is the diagnostics
   section the mandate reserves for them; the owner-facing sentences above are the rewritten ones.

## Answers

By the Phase 5 Developer, on the Delivery Lead's decisions (fix the three findings and observations 2, 3 and 4; no
change for 1, 5 and 6). Everything below is in the same pull request, 4477.

- **Finding 1 - the README's command.** Fixed: `scripts/test-database.ps1`, with a forward slash. The README holds no
  tab character now.
- **Finding 2 - the restart asker without a number.** Fixed at the one writer: `DirectorRestartRequestService.RequesterName`
  now stores "Release - Manager (121)" when the session has a number, from the `SessionDto` it already reads, and keeps the
  short id only for a session that has none. `AlertWords.FromRestartRequester` reads it as "Release Manager (121)", like
  the other two kinds. The Director's drain step prints the same stored string ("asked for by Release - Manager (121)"),
  so the two stay consistent without a change there. A number match is at most seven digits, so a short id made only of
  digits is never read as a number. Tests: `DirectorRestartRequestServiceTests` (the stored name),
  `AlertWordsTests` (every form, including the all-digit short id), `RestartRequestAlertSourceTests` (the summary).
- **Finding 3 - the vacuous phone measurement.** Fixed in `steps_mixed.py`: the phone counts the cards before and after
  each answer (`cards 3 after: cards 2`, then 1, then 0), and the step back to the session list prints the bell's badge
  there (`badge: 1`). The Cockpit still prints its bell's badge.
- **Observation 2 - router state lost across a deep link.** Swept: three phone pages read `location.state` - the Alerts
  screen, Home (the roster's Voice or All tab) and VoiceMode. VoiceMode's state holds one-shot commands, which it scrubs
  from the history entry on arrival, so they are spent before any push can land. Fixed: the deep link carries the page's
  own state with it (`backState`) and Back puts it back. Test: `Home_DeepLink_BackKeepsTheRostersOwnState`, run without
  the fix first (fails) and with it (passes).
- **Observation 3 - a failed answer looking calm.** Fixed per the Delivery Lead's design. Each source stamps a `tone`
  beside the status word (`AlertTones`: good, bad, neutral) on the Gateway; the answered line and the Earlier row colour
  what they are told - red for bad, green for good, the plain border for neutral and for any tone this app does not know.
  Tests: every state's tone in the three source tests, the field on the wire (`List_CarriesEachAlertsTone_...`), and two
  client tests (a failed answer reads bad in the line and the row; a good one reads good; an unknown tone draws neutral).
  Screenshot: `phone-answer-secret-transfer-approved-then-failed` and its Cockpit pair.
- **Observation 4 - the refusal at the page edge.** Fixed: the sentence is indented under its own row
  (`.alert-line.alerts-row-error`, two classes so it outranks `.alert-line`'s margin, which a first attempt did not and
  the screenshot showed). `*-clear-refused-needs-an-answer` show it.
- **Observations 1, 5, 6.** No change, as decided.

Section 7 was run again on the final code (numbers in `qa/README.md`), and the whole QA pass was redone on one build.
