# Re-review - pull request 3925, the Gateway routes the Licenses page reads

Reviewed at `63aa81f61` (round 1 was at `871fd3d39`). Reviewed by a separate review session that wrote none of it.
Nothing in the worktree was edited except this file. Round 1 is `3925-cockpit-api-review.md`, the answers are
`3925-answers.md`.

## Verdict: changes needed

Two should fix, nine notes. The must fix of round 1 is closed: the usage history is bounded, and I could not break
the bound. Sixteen of the eighteen round 1 findings are closed as answered. Two are closed for the case round 1
named and still open one step further on, and both are things the brief for this round asked to be checked hardest:

- the forecast route can still answer 500 on input a Director can send (new finding 1);
- a machine is not yet named the same way everywhere: the log, and the sentences the decision engine writes, use
  the machine name alone, while rule 9 of `cockpit-api.md` says every list and every sentence uses the four fields
  and the label (new finding 2).

Both are small. If the Tech Lead rules either into the follow-up pull request, then `cockpit-api.md` should say so
in this one, because phase 3 builds from that file.

## What I ran and read

- Read in full, as they stand: `LicenseUsageHistory.cs`, `LicenseForecaster.cs`, `LicenseDisplayFold.cs`,
  `LicenseUsageIngest.cs`, `LicenseEndpoints.cs`, `LicenseReadModel.cs`, `LicenseSnapshotSource.cs`, the manual
  switch half of `LicenseDecisionEngine.cs`, sections 1 to 6 of `cockpit-api.md`, and the diff from `871fd3d39` of
  the response types, the engine, the guard and the license tests.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~License"`: passed 288, failed 0.
- `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"`: passed 8, failed 0.
- A script compared every key of the five JSON examples in `cockpit-api.md` with the properties of the response
  types, in order and at every depth: 54 objects, no difference. Every field the tables name is on a type, and every
  property of every type is named in the document.
- An out-of-tree console project (in the session's scratch folder, referencing the Gateway project, with the test
  assembly's name so it can call the route handlers) ran the real ingest, the real store on a throwaway SQLite file
  and the real route handlers. Its output is quoted under each finding.
- NOT done: I did not re-run the sixteen mutations R01 to R16. I read the test each one names and each does assert
  the mutated behaviour; that is all. I did not run the whole gate; the proof file says its gate result is for
  `c3252762b`, the commit before the five new fields, and I did not check that result.

## The eight things the brief asked to check hardest

1. **The history is bounded - holds.** At most 256 account series per DevThrottle account, at most 600 points per
   window, no point later than the Gateway's clock. Out of tree:
   - round 1's case (50 reports of 64 new accounts stamped ten years ahead): `accounts held right after: 256; two
     hours and a minute later: 0`;
   - the most I could make one DevThrottle account hold (256 accounts, 700 distinct readings each, both windows):
     `accounts=256 points=307200 (ceiling 307200); most in one window=600` - it reaches the ceiling and stops there;
   - a reading ten years ahead, a reading pushed ahead by a clock correction of 400 days, and a reading 30 hours
     ahead with no time zone marked are all kept at exactly now;
   - a flood from one DevThrottle account leaves another's history untouched.
   Both halves have a test at the boundary (`Record_ExactlyTheMostAccountsKept_DropsNone`,
   `Record_AReadingAtExactlyNow_KeepsItsOwnTime`) and a test of the failure. Notes 4, 5 and 6 are what I found while
   trying to break it; none of them is a hole in the bound.
2. **The page never has to judge - holds, except the naming of a machine in the log (finding 2).** A stale
   account's windows are `stale` on both routes (`status=stale isStale=True five.level=stale week.level=stale`, and
   the same level on the forecast). The forecast window carries the overview's own words, by calling the same
   `Window` fold. `activeOnMachines`, `spareOnMachines`, `nextSwitch.machines` and `signInExpiry.machine` are the
   four fields, and two users of one machine are told apart in every sentence the fold writes
   (`active=m1 (alice) | M1 (bob)`, `Sign-in on M1 (bob) expires in 23 hours 59 minutes`). A switch row in an unknown
   state no longer says it is switching.
3. **The forecast route can no longer answer 500 - does NOT hold.** The slow rise of round 1 is fixed. Two other
   inputs still give 500: finding 1.
4. **A repeated reading is recognised by the machine's own read time - holds.** One reading sent five times with
   a correction that differs by milliseconds: `points=1 rate=null`.
5. **A page poll no longer floods the log - holds.** Measured: `accounts=2: overview=12 forecast=15 log=4`, and
   exactly the same for 5 and for 20 accounts, with no decision line in any of them (note 7).
6. **The Tech Lead's rulings as built - hold.** `signInExpiry` is its own object on the account row, the soonest
   across the machines holding the account, and is called sign-in expiry everywhere; the word "renewal" is not used
   for it in the code or the document. `renewsOn` and `renewsText` are on the account (null and "Renewal date not
   set"), `renewalFailedAtUtc`, `renewalFailedText` and `renewalFailedTone` are on a holding (all null), the shape
   test asserts each value, and the document describes all five as the code has them. Section 6 opens with "This
   section is final". Notes 8 and 9 are two small things about them.
7. **`cockpit-api.md` matches the code key for key - holds** (the comparison above). What does not match is one
   sentence of rule 9 against the code: finding 2.
8. **Nothing broke in isolation or the guard - holds.** `SessionKeyGuard.cs:370-372` still lists exactly the three
   reads under GET; the changes to that file since round 1 are factory email and cron pause lines from main. The
   handlers still take the account from the credential only. Out of tree, a second DevThrottle account read all
   three routes while the first held three machines and two open switches: empty answers with their sentences, and
   a request with no account bound is `403 no_account`. The 8 host tests pass.

## Round 1 findings, one by one

| # | Round 1 finding | Verdict now |
|---|---|---|
| 1 | The usage history is not bounded (must fix) | Closed. See check 1. |
| 2 | A stale account's window still says "at-limit" | Closed. The level `stale` is on both routes. |
| 3 | The forecast's windows carry no words | Closed. Same fold, same words; the shape test names the fields. |
| 4 | Goal 1's "renewal date" | Closed as ruled. `signInExpiry` on the account; `renewsOn` and `renewsText` hold the place of the real renewal date. |
| 5 | The forecast route answers 500 on a very slow rise | The slow rise is closed (`200`, "Not expected to reach 97% before it resets."). The route still answers 500 on other input: new finding 1. |
| 6 | A machine with two users is named twice | Closed in the four lists and in every sentence the fold writes. Still open in the log and in the engine's sentences: new finding 2. |
| 7 | An unknown switch state says "is switching" | Closed. `cancelled` gives its message, or a sentence naming the state. |
| 8 | A repeated reading is added again under clock skew | Closed. See check 4. |
| 9 | One page poll writes dozens of log lines | Closed. See check 5. |
| 10 | Nothing tests the history from two threads | Closed. The test exists, and the proof says plainly that it is a strong sensor and not a proof. Note 11. |
| 11 | Two sentences for cases that cannot happen | Closed. Both throw. I agree neither can be reached: the forecaster and the preview choose a target by the same code on the same machines, and every machine on an account is either switched or left out with a reason. |
| 12 | The fold's small public methods do not log | Closed. The header states the exemption and names each method. |
| 13 | A log walk can show one row twice | Closed. The document now says what the code gives (lines 655-660). |
| 14 | The guard test's name overstated it | Closed. Renamed, and the comment says what it does not catch. |
| 15 | `verifiedByUsageRead` has no words | Closed. `verifiedText` is null on every row whose switch was not made. |
| 16 | The rows of one switch share nothing | Closed as ruled, and written in the document as a decision. |
| 17 | Any session key can read every account, machine and user | Closed as ruled, and written in the document as a decision. |
| 18 | What "never a sign-in key" is proven by | Closed. Rule 7 says the test holds member names only. |

## New findings

### 1. The forecast route still answers 500 on input a Director can send - should fix

`src/CcDirector.Gateway/Licenses/LicenseForecaster.cs:165` and `:221`; the cause is
`src/CcDirector.Gateway/Licenses/LicenseUsageIngest.cs:211-226`.

`Malformed` checks the shape of a report and no number and no time in it. So a usage number of any size and a read
time of any year are stored and reach the forecaster. Two ways to a 500, both run through the real ingest, the real
store and the real route handler, two reports two minutes apart:

- **A rate that is infinite.** A reading of `-1e308` and then one of `96` (both are ordinary JSON numbers). The
  rise divided by the time overflows, `percentPerHour` is infinity, and the answer cannot be written:
  `forecast: THROWS ArgumentException: .NET number values such as positive and negative infinity cannot be written
  as valid JSON.` It lasts until those points leave the history, up to two hours - the same length of outage as
  round 1's case.
- **A read time in the last minutes of the year 9999, with use rising.** The reading is never stale (note 3), so
  the forecaster works out a date from it and `readTime.AddHours(hours)` passes the end of the calendar:
  `forecast: THROWS ArgumentOutOfRangeException: The added or subtracted value results in an un-representable
  DateTime.` The check added for round 1 bounds the hours, not the time they are added to.

The same missing check also puts words on the page that no person should see. With a reading of `1e308` the
overview answers `"utilizationText":"Infinity%"`, and with `-1e307` a `utilizationText` of more than three hundred
digits.

Why it matters: it is the same class as round 1's finding 5 - a Director with a defect produces it at will, and
one such reading fails the whole forecast for that DevThrottle account - and the brief for this round asks that the
route can no longer answer 500 on any extreme input. No other DevThrottle account is affected.

What would close it: refuse, in `Malformed`, a usage number that is not a real percentage (not finite, or far
outside 0 to 100) and a read time later than the report's own sent time; or, if the ingest is not to change in this
pull request, have the forecaster treat a rate that is not finite as no rate and never work forward from a time
later than now. The first closes notes 3 and 4 as well. A test for each case.

### 2. The log and the engine's sentences name a machine by its name alone, and rule 9 says they do not - should fix

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:584-585` and `:606-611` (the log row);
`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:176, 180, 323, 326` (the engine's sentences);
`missions/license-switching/cockpit-api.md:37-42` against `:670`.

Rule 9 of the document says: "Wherever a list names a machine it is the same four fields - `machineId`,
`machineName`, `osUser`, `label`" and "Every sentence in these answers calls a machine by its label." Two places do
neither:

- **The log.** A row carries `machineName` and `osUser`, no `machineId` and no `label`, and its `summary` uses the
  name alone. Out of tree, one switch of an account on two users of one machine:
  `has machineId=False has label=False machineName=M1 osUser=bob summary="M1 is switching from
  account-a@example.com to account-b@example.com."` and the same sentence for the other user. With one spelling of
  the name the two summaries are word for word the same, and the page cannot link either row to its machine in the
  overview except by building a name from the two fields - which rule 9 forbids ("never build a name from the
  other fields").
- **The engine's sentences**, which the fold passes on as `switchTargets[].unavailableText`, as the reason a
  machine is left out, and as a refusal. Out of tree: `account-b@example.com:False:M1 holds no usable spare for
  account-b@example.com.` The answers say this plainly and leave it for the pull request that owns the engine. That
  is a fair call, but the document does not say it: it says every sentence uses the label.

Why it matters: this is round 1's finding 6 on the third route, and phase 3 is told by rule 9 that it never has to
tell two users of one machine apart itself.

What would close it: put `machineId` and `label` on the log row and use the label in `summary` (the row already
has the machine's identifier in the store); and either make the engine's sentences take the label, or write the
exception in rule 9 with the name of the pull request that removes it.

### 3. A reading stamped later than now is never stale - note, for the Tech Lead

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:171-172`; `LicenseReadModel.cs:84-86`;
`LicenseDecisionEngine.cs:211-218`.

The history now keeps no point later than now. The store still keeps the read time as sent. A reading is stale
when now minus its read time is more than six or thirty minutes, so a read time ahead of now never is. Out of tree,
two readings stamped 400 days ahead, 50% then 60% two minutes apart: the overview says `status=in-use
isStale=False readAgeText=Read less than a minute ago`, and it will say so for 400 days; the forecast says
`"percentPerHour":300` and `"expectedText":"Not expected to reach 97% before it resets."` for an account that
reaches its limit in seven minutes. Such a reading is also the account's freshest for ever, so a true reading from
another machine never replaces it.

This is in the ingest, which was on main before this pull request, and it needs a Director whose read time is
later than its own sent time - a defect, not a wrong clock (a wrong clock moves both and is corrected). I name it
because finding 2 of round 1 rests on staleness being reachable, and because the check of finding 1 closes it.

### 4. A read time at the edge of the calendar makes the ingest throw - note

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:172`; the same sum at `LicenseUsageHistory.cs:81`.

With a clock correction in force, `read + readAtShift` passes the end (or the start) of the calendar and throws:
`INGEST THROWS ArgumentOutOfRangeException: The added or subtracted value results in an un-representable DateTime.`
The report is lost as an exception in the hub rather than refused as malformed. `LicenseUsageHistory.Record` does
the same sum before its clamp and throws the same way when called directly. The store's line was there before this
pull request. The check of finding 1 closes this too.

### 5. A flood of new accounts pushes the real ones out of the history - note

`src/CcDirector.Gateway/Licenses/LicenseUsageHistory.cs:91-92`.

The account that goes is the one whose newest point is oldest. Out of tree, a real account with two readings, then
four reports of 64 new accounts from another machine of the same DevThrottle account: `is account A still held?
False`. Its forecast says "No rate yet" until two more of its own reports arrive. And when more than 256 accounts
keep reporting in turn, none of them ever holds more than one point, so none ever has a rate. Both stay inside one
DevThrottle account and inside the bound, and no real fleet has 256 accounts. A fair price for the bound; named so
it is a known price.

### 6. A repeat is recognised by read time alone, across machines - note

`src/CcDirector.Gateway/Licenses/LicenseUsageHistory.cs:130`.

Two machines that read the same account at the same instant with different numbers: `points=1`; the second number
is dropped. Harmless for a rate, and it needs two reads at the same tick.

### 7. The answer to finding 9 undercounts the lines of a read - note

The answers say a read of the overview "now writes the fold's two lines and the route's two". Measured, one read of
the overview writes 12 lines, the forecast 15 and the log 4, because the store, the read model and the snapshot
source each log their own. What matters is true: the count does not grow with the number of accounts and no
decision line is among them. Only the sentence is short. The test that holds it reads source text rather than the
log, as the answers say, so a log call added somewhere other than the two places it reads would pass it.

### 8. "Renewal" now means two things in one answer - note, for the Tech Lead

`renewsOn` and `renewsText` on an account are about the SUBSCRIPTION being billed again. `renewalFailedAtUtc`,
`renewalFailedText` and `renewalFailedTone` on a holding are about a machine renewing its SIGN-IN. The document
tells phase 3 never to label the sign-in expiry "renewal" (line 58), and eighty lines later gives a sign-in three
fields named renewal. The names are ruled final and the document does say "tried to renew this sign-in" (line
136). One sentence beside line 58 saying that `renewalFailed...` is about the sign-in, not the subscription, would
keep the page from showing a failed sign-in renewal next to "Renews 17 Oct" as if they were one thing.

### 9. The three failed renewal fields are on the type and not in the fold - note

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:350-363`.

The brief asks that the five new fields be on the types, in the fold and in the document. `renewsOn` and
`renewsText` are set in the fold (lines 115-116). The three on a holding are not mentioned in `Holding()`: they
are null because the type's default is null. The shape test asserts the three nulls, so the answer is held; there
is simply no line in the fold where the follow-up will see where they belong.

### 10. A switch row with an empty state word is said to be in the unknown state "Recorded" - note

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:567` and `:611`. Out of tree: `state='': stateText='Recorded'
summary="m1: a switch from account-a@example.com to account-b@example.com, in a state this Gateway does not know
(Recorded)."` The store never writes an empty state, so this is wording only.

### 11. The several-threads test takes seven to ten seconds on few cores - note

`src/CcDirector.Gateway.UnitTests/Licenses/LicenseUsageHistoryTests.cs`,
`RecordAndRead_FromSeveralThreadsAtOnce_NeverSeeAHalfWrittenSeries`. Its assertion that every reader read at least
once while the writers wrote depends on the readers being given a thread before 100,000 writes finish. I ran it
pinned to one core three times and to two cores three times: it passed six times out of six, in 7 to 10 seconds.
I did not see it fail. It is the slowest license test by a wide margin, and it is the one to look at first if this
suite ever shows a red that passes when run alone.

## One thing the document already declares, so it is not a finding

Section 6 says the page does not offer Switch now for an account whose only machine is waiting for a Director that
is offline, although the route of the follow-up would accept it, and that the follow-up closes it. The page under
offers; it never offers what would be refused. Named here so the follow-up's review looks for it.
