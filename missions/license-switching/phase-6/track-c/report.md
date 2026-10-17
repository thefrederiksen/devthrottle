# License Switching phase 6, Track C - standing approvers: track report

17 October 2026, by the Track C Tech Lead. Mandate items 6, 7 and 10. Every piece is merged to main.

## What Track C set out to do

Before this track, an approver existed only if the owner clicked through a form with free-text boxes, nobody knew
whether an approver could still approve until a sign-in failed on it, and the monthly renewal had never been shown to
complete through an approver with no person. Four things were asked for:

1. Approver records can be made without the owner clicking a form, while the owner-only routes stay owner-only.
2. A daily check of every approver that opens the real approval page and says can approve, signed out or needs a fresh
   sign-in. An approver that cannot approve is a not-ready reason for Track A and raises the license alarm. The check
   never signs in and never presses Authorize.
3. A usable approver form: pick lists, no free text, nothing wrongly filled in, and a Save that says what is missing.
4. The monthly renewal goes through the approvers with no person.

## What was built (twelve pull requests, all merged)

| Pull request | Item | What it did |
|---|---|---|
| 4109 | 1 | Registration: the hub methods `GetLicenseApproverAssignments` and `ReportLicenseApproverChecks` (contract section 10), the record's source (owner or registered), health and removal records, and the registration rule |
| 4111 | 3 | The approver form: four pick lists, the chosen machine's own browser profiles, Save that names each missing choice |
| 4116 | 2 | One page reader, `classify_page`, for both `license approve` and the new `license check-approver`; the reauth page gets its own word, `needs-fresh-sign-in` |
| 4127 | 2 | Readiness: the health sentences, the new approver-row fields, an owner's removal that sticks, and `LicenseApproverReadiness.WhyNotReady` for Track A |
| 4140 | 2 | The approver alarm, kind `approver-cannot-approve`, through Track A's one license alarm mechanism |
| 4148 | 2 | The hosted proof of the alarm |
| 4150 | - | Main fixed forward: the Cockpit fixtures regenerated after 4127 |
| 4123 | 1, 2 | The Director's daily approver check and the shared browser lane, with the opt-in setting `licenseSwitching.offerApprovers`, off by default |
| 4153 | 4 | Hosted proof that the renewal completes through the approver, including a machine that approves itself; each relayed approval is now an `approval` row in the Licenses log |
| 4165 | 3 | Each approver row shows who made it, why a sign-in cannot reach the machine, the profile's health and the one thing to do, in the Gateway's words |
| 4177 | - | The approval-check part's summary |
| this one | - | This report |

The three part summaries hold the detail, with every test named: `c1-summary.md` (registration, readiness, alarm),
`c2-summary.md` (the page check, the daily check, the renewal proof) and `c3-summary.md` (the form and the health rows).
Each pull request's proof file and its review and answers are beside them in this folder.

### How the pieces fit

A Director with license switching on, and only the key holder, runs `LicenseApproverCheck` at start and once a day.
It asks the Gateway which approver records name its machine and checks each one with
`cc-devthrottle license check-approver`, which opens a real approval page from a throwaway login in a temporary folder,
reads it, and cleans everything up without pressing anything. When the machine offers its profiles as approvers, it
also checks each of its own browser profiles with no account. All rows go up in one report.

The Gateway takes the tenant and the Director from the hub connection, never from the report. A profile that can
approve an account the tenant holds becomes that account's approver, unless the owner set one or removed this one. The
Gateway folds every sentence the Cockpit shows (repository rule 7). Only a recent can-approve check counts as able to
approve; no check in 26 hours reads as not checked. Track A asks `WhyNotReady`, and an approver that cannot approve
raises one alarm per account, after each report and on the minute check, closing by itself on recovery.

A browser profile is always named by its identifier, compared exactly; its display name travels beside it as
`browserProfileName`. This was decided after the review of 4111 and is written in the Track C design.

## How it was proved

Every pull request had a review by a separate review session, and every finding was answered in writing in its
`review-<number>-answers.md`. The reviews raised one high finding, seven medium and about fifteen low; all were fixed
with a test or accepted in writing. 4165 had none.

Every pull request was gated by its Developer and again by me: the default `.\scripts\test-local.ps1` and
`.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~License"`, with TEMP and TMP on `D:\cc-gate-temp`, on the
branch merged with current main. Python changes also ran the whole tool suite in a clean environment pinned as
continuous integration pins it (4,729 passed). Cockpit changes also ran the type check and the Cockpit and client-core
tests. The last License runs were green with about 1,077 Gateway license unit tests, 26 hosted, 253 Director and 32
desktop.

### What the tests show, in plain words

- **Nobody can register an approver for someone else.** A report from a Director that is not the one reporting for a
  machine of the tenant, a row of any wrong shape, an account no machine of the tenant holds, a machine that does not
  offer its profiles, an account whose approver the owner set, a registration the owner removed, a healthy approver
  already in place, a row about another machine's record, and a tenant already at its limit are each refused by their
  own test, and the refused row changes nothing. The same Director identifier in another tenant reaches nothing.
- **A session key still cannot remove an approver, and the owner's removal sticks** across the next daily check, on a
  real hosted Gateway.
- **Can approve is a reading, never an absence.** No approver, never checked, checked over 26 hours ago, and every
  failed state each give a not-ready sentence. A check the tool could not run, a crashed tool, or an answer that is not
  JSON is reported as `check-failed`, never left out and never read as can approve. A report where every row failed is
  still sent.
- **The page is read one way.** The approval and the check run the same function in the browser, against eight
  recorded page shapes. The fake browser has no click helper, so any press where none is allowed fails the test. The
  check leaves no login, folder or changed profile behind even when the browser step, the folder removal or the login
  stop fails, or the program crashes.
- **Nothing from the page reaches the owner.** The detail in a report is one of the Director's fixed sentences. Text
  planted in the fake tool appears nowhere in the report, and a Director that echoes the approval address into a
  refusal leaves no trace of it in the Licenses log.
- **The alarm opens and closes for the right person.** On a hosted Gateway, a signed-out check over the tunnel opens
  the alarm for one user's approver, a second user with a healthy approver for the same account gets nothing, and a
  can-approve check closes it. It was shown red with the link from the report to the alarm taken out.
- **The browser is shared fairly.** A check never runs beside a sign-in or an approval. A sign-in and the approval it
  waits for never wait for each other, which is what lets a machine approve its own renewal. An approval waits for a
  running check only while it could still be answered in time; past that it is refused `expired` and nothing is
  pressed.
- **The renewal needs no person.** Against a real hosted Gateway with the real Director code, only the browser, login
  and seal faked: a due spare is renewed through the approver the owner set; a machine that is its own approver renews
  with no deadlock, under a two-minute limit so a deadlock fails by name; an approver that needs a fresh sign-in fails
  the renewal with that word and its sentence. Each case leaves its `approval` row in the Licenses log.
- **The form cannot be filled wrongly.** Four pick lists and no free text, the account filled in only from its own
  card, changing the machine clears the user and the profile, and Save with a choice missing sends nothing and lists
  each one. The identifier is saved, never the display name.

## Known follow-ups

- **A running daily check does not give way to an approval that arrives.** The approval waits 30 seconds and is then
  refused `expired`, with nothing pressed, so that sign-in must be tried again. Letting the check stop and hand over the
  browser needs a graceful stop of the tool. Accepted in writing in the second review of 4123.
- **A renewed spare's new sign-in time reaches the Gateway up to ten minutes late**, at the reporter's next read. It
  heals itself and nothing acts on it sooner.
- At 900 pixels wide the approver status column wraps word by word, and "Registered by {label}'s Director." reads
  oddly when the label carries a user in brackets.

## Reds seen on the way that are not Track C's

- `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused` cannot pass with TEMP on drive D:,
  which makes no short names. It passes with TEMP on C:.
- `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection` (from #4152) fails on a checkout
  with Windows line endings.
- `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne` was
  red on main after #4162 added a waiting loop without its row in `docs/BackgroundWork.md`.

All three were reported to the Delivery Lead.

## What the live proof should look for

1. **The opt-in.** On the approving machine, turn on "offer this machine's browser profiles as approvers". Before it is
   on, that machine registers nothing.
2. **Registration.** Within a day of the Director starting (it checks at start), the Licenses page shows the approver
   row with "Registered by machine-a's Director.", the profile's display name over its identifier, and a healthy
   status.
3. **Daily health.** Every approver row shows a check from the last 26 hours. One left unchecked longer turns not
   ready and raises the alarm.
4. **A failing approver.** An approver profile whose claude.ai sign-in needs a fresh sign-in shows that on its row
   with the one thing to do, Track A treats the account as not ready, and one `approver-cannot-approve` alarm opens.
   After the owner signs that profile in again, the next check closes the alarm. The check itself must never sign in
   or press Authorize; the browser should show nothing pressed.
5. **The owner stays in charge.** Removing a registered approver keeps it removed through the next daily check, and
   setting one by hand through the form is never overwritten.
6. **The monthly renewal.** When a spare comes due, it renews with no person, and the Licenses log has an `approval`
   row naming the approving machine. Its new sign-in time may take up to ten minutes to show.
