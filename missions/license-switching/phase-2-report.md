# License Switching - phase 2 report (the Gateway)

Phase 2 of `MISSION.md` section 6: the contract between the Director and the Gateway, and the whole Gateway side of
the feature - storage, the usage reports, the decision every minute, the switch commands, the owner email, Switch now
by hand, the forecast, the routes the Licenses page reads and writes, and the Gateway side of contract section 8
(approval on another machine, a sign-in started from the Cockpit, the email for a renewal that keeps failing).
Written by the phase 2 Tech Lead on 13 October 2026. The Tech Lead wrote no product code: each piece was built by a
Developer in its own worktree, reviewed by a separate review session, checked by the Tech Lead, and merged. Every
pull request below is merged to main. Nothing was deployed. The machine lost power twice on 13 October while this
phase was running; each time the state was read back from git and GitHub before anything went on, and nothing was
lost.

## What was built

| Pull request | What it is | Merged |
|---|---|---|
| 3852 | `missions/license-switching/contract.md`: the usage report (`PushLicenseUsage`), the switch command (`license-switch`) and its result, how a spare is added and removed, every disagreement and failure (a Director offline, a stale report, a switch already running, a Gateway restart mid-switch), the rules the contract enforces, and its constants in one place. | 54fd5da0d |
| 3854, 3876 | The message types (`src/CcDirector.Gateway.Contracts/LicenseMessages.cs`) and a test that no license message type can carry a sign-in key, by any declared, inherited or interface member (`LicenseMessagesCarryNoKeyTests`). | efe526a1c, 013c07442 |
| 3871 | Storage and ingest: the license tables for both database providers, `LicenseStore`, `LicenseReadModel`, and `LicenseUsageIngest` behind the hub method `PushLicenseUsage` - one Director per machine reports, a sequence number orders reports, a reconnect cannot land an old report over a new one. | ad8fe76df |
| 3865 | The decision engine and the forecast (`LicenseDecisionEngine`, `LicenseForecaster`): 97% of either window; the target is an account every affected machine holds a spare for, with both windows under 97%, not barred, lowest weekly use then lowest 5-hour use; every machine on the account is switched; no target opens ONE exhaustion event. Pure code, no clock and no database of its own. | 8610c8fb2 |
| 3925 | The routes the Licenses page reads: `GET /gateway/licenses`, `/gateway/licenses/forecast`, `/gateway/licenses/log`, with every verdict folded on the Gateway (`LicenseDisplayFold`), and `missions/license-switching/cockpit-api.md`, which phase 3 built the page from. | 0b83751ed |
| 3923 | The check that runs every minute (`LicenseSwitchService`): sends the switches the engine decides, settles the answers, resends after an outage under the same identifier, recovers after a Gateway restart, records "no account had room", emails the owner once per exhaustion event (`LicenseOwnerMailer`), and offers `SwitchNowAsync`. Inert for an account until a Director reports. | 30e468c18 |
| 3936 | The writes of the Licenses page: `POST /gateway/licenses/switch-now`, `PUT /gateway/licenses/accounts/renewal` (the date a subscription is billed again, set by a person), the stored failed renewal of a sign-in, a bar shown on its account, and the `AddLicenseRenewal` migration for both providers. Both writes are for a signed-in person only. | 94dcfcc8b |
| 3943 | The Gateway side of contract section 8, parts 1 to 3: who approves each account's sign-ins (`GET`, `PUT`, `DELETE /gateway/licenses/approvers`, kept in the per-account settings, no migration); the approval relay (hub method `RequestLicenseApproval`, which returns at once, and the commands `license-approve` and `license-approval-result`); and Add a spare and Renew now from the Cockpit (`POST /gateway/licenses/sign-ins/add-spare` and `.../renew`, command `license-sign-in`), whose outcome lands in the Licenses log as its own kind of row. The address a sign-in is approved at and the sealed approval are held in memory for the one relay and reach no store, no log row and no log line. | d9ca4957a |
| 3948 | The owner email of contract 8.1 (`LicenseRenewalEmailService`): one email for each sign-in for each failure streak, when a machine reports that a renewal failed and the sign-in ends within three days. The streak is a row of its own kind in the Licenses log, so the email survives a Gateway restart without being sent twice; a streak ends only on a sign-in that is really newer. No table and no column added. | d055b18a3 |

The Gateway never holds a sign-in key. The one field that carries anything derived from a sign-in is the sealed
approval of contract 8.3, which the Gateway cannot open, relays in memory and never stores or logs.

## The check the Tech Lead ran

For each pull request I cut a worktree of my own at its head, merged origin/main into it, and ran the suites there -
never in the Developer's worktree.

| Pull request | What I ran on the head merged with main | Result |
|---|---|---|
| 3923 | `dotnet test src/CcDirector.Gateway.UnitTests` (whole suite); the hosted license tests in `Gateway.Tests` | 11,045 of 11,046; 10 of 10. The one red was `WindowRestorePlacementTests`, which reads the real clock and fails between 03:00 and 06:00 Toronto time on main itself (filed as issue 3934). |
| 3936 | The same two | 11,217 of 11,217; 22 of 22 |
| 3943 | The same two, and `.\scripts	est-database.ps1` | 11,484 of 11,484; 39 of 39; 72 of 72 on a throwaway PostgreSQL, none skipped |
| 3948 | `.\scripts	est-local.ps1 -Parked`; `.\scripts	est-database.ps1`; the hosted license tests | GREEN: all 16 suites completed, 23,599 tests, none failed. 73 of 73 on a throwaway PostgreSQL, none skipped. 42 of 42. |

3948 was the last merge, so its head merged with main (`d0453c5ca`) is the whole of phase 2: **the full local gate
with the parked suites is green on everything this phase built.** The gate record is
`d0453c5cac68a9d443585ada8105763d8f429ccc-20261013T214045Z.json`.

The earlier pull requests (3854 to 3925) I checked the same way before merging them, but I wrote the counts on the
pull request only for 3925; for the others the counts that are on record are the Developer's, in the proof files.

The gates each Developer ran, with counts, are in the proof file of each piece under
`missions/license-switching/proof/`. On the last full runs: `.\scripts\test-local.ps1 -Parked` 23,251 of 23,253
(3923) with every Gateway suite green, and `.\scripts\test-database.ps1` 71 of 71 on a throwaway PostgreSQL, none
skipped (3936).

**Reds that were not this phase's, each named in the merge notes:** the machine-dependence baseline guard and the
`docs/BackgroundWork.md` count for `GitHubActionsBackend.cs` in `Core.UnitTests` (red on main, fixed by other work);
`WindowRestorePlacementTests` (issue 3934); `ContextLessRouteCensusTests` (red on main since pull request 3920);
and three tests that failed once in a full run and passed when run alone (`TeamBillEndedTests`, issue 3908;
`UpdaterStateConcurrencyTests`; `CircularTerminalBufferTests`). None of them was red in the final run above. By the Delivery Lead's ruling a pull request merged
on the suites that pass plus a note naming the known failure.

## Reviews and their answers

Every pull request was reviewed by a separate review session before merge. Every review and every answer is in
`missions/license-switching/reviews/`.

| Pull request | Rounds | In short |
|---|---|---|
| 3854, 3876 | three | Found: the test that no message carries a key did not scan every license type, then missed inherited members, then members that come from an interface. All fixed, each with a probe type that makes the test go red. Declined by the Tech Lead: the finding that comments naming the product whose licenses are managed break the rule against attribution - they name what is managed, not who wrote the code; the reviewer's dissent is recorded. |
| 3871 | three | Found: a report from a connection that had just been replaced could still be written; the hub offered `PushLicenseUsage` even when no ingest was set; Director identifiers compared by case; an in-memory lock held across a database write. All fixed; the reconnect case is held by a test that fixes the order of the two writes. |
| 3865 | two | Found: a reading taken before a window's reset re-opened the exhaustion event and would have emailed the owner every minute (must fix); the 97% limit was not tested at exactly 97% on either window; the forecast could name a different target than the engine. All fixed. Ruled: one email per continuous outage - the event stays open while a fresh reading is still tired. |
| 3925 | two | Eighteen findings, then eleven. Found: the usage history could grow without limit (must fix); several places where the page would have had to judge for itself (a stale account's windows, the forecast's words, an unknown switch state); the forecast route answered 500 on a very slow rise; one page poll wrote dozens of log lines. All fixed. Ruled: any session key may read the licenses (accepted and documented); the renewal date is the subscription's billing date set by a person, and the end of a sign-in stays its own field. |
| 3923 | two | Ten findings, then four. Found: a check that failed after planning a send stranded that switch for the life of the process (must fix); a resend cut off part-way could give a machine a second switch identifier; a replayed answer could overwrite a newer report when the Director's clock was ahead; one Director that did not answer held up every other account. All fixed: the last by starting a pass every minute and skipping only the account whose own check is still waiting, and answers are now judged by the Gateway's own send time, never the Director's clock. |
| 3936 | two | Ten findings, then seven. Found: a Director's own device key could make both writes (must fix) - they now accept a signed-in person only, held over real HTTP for the machine credential, the shared token and a raised session key; a body with no date cleared the stored date; an account name of any length was logged as typed. All fixed. Left as ruled: a repeated identical Switch now answers the waiting row and the minute check sends it. |
| 3943 | two | Ten findings, then seven. Found: the limit on approvals in flight did not bound a Director asking in a loop (must fix) - a request past the limit is now refused on the hub call and holds nothing; an unanswered sign-in could be recorded as failed when it had worked, from a report older than the ask or by comparing the machine's clock with the Gateway's; a relay that threw left the asking Director with no answer; text from a request was logged before it was checked. All fixed: an unanswered sign-in is settled first by asking the Director again under the same identifier, which replays what happened and starts nothing. Round 2 found two ways that settling could still record the wrong end (a Director that no longer has the result; a machine clock more than five minutes slow); both fixed. Noted, not changed: every refused request writes one log line, and a repeated identifier in flight is dropped without an answer. |
| 3948 | two | Twelve findings, then three. Found: a Director that restarts forgets its failed renewals, so its first report ended the streak and the owner was emailed twice (must fix); the email did not say how old the machine's report was; the machine's own sentence went into the email as sent. All fixed: a streak ends only on something present - a sign-in end later by more than an hour, or a later sign-in time. Round 2 found the same double email by another road (must fix): a sign-in's reported end moves by seconds on every key refresh; the one hour the license tool itself allows is now a named constant of the contract. Also fixed for the "no account has room" email: an email the mail service confirmed is never sent again by the same process while the database refuses the mark. |

## Decisions the phase made

- **One email per continuous outage.** The exhaustion event stays open while any fresh reading is still at the
  limit, and its end moves to the later reset; the owner is emailed once for it, and the email survives a Gateway
  restart without being sent twice.
- **The Gateway's clock judges.** Whether an answer is newer than a report is decided by when the Gateway sent the
  command and accepted the report, never by a Director's clock.
- **A report is refused, not repaired.** Usage that is not a finite number in range, or a read time more than two
  minutes after the sent time, is refused at ingest.
- **The writes are a person's.** Switch now, the renewal date, the approver setting and the sign-ins from the
  Cockpit refuse a session key, a Director's device key and the shared machine token. An agent session can read the
  licenses and can change nothing.
- **A bar stops the automatic choice only.** A person may still name a barred account as the target of Switch now;
  the page is told of the bar in a finished sentence.
- **Reads that run every minute log failures only.** A departure from the rule that every public method logs entry
  and exit, made by ruling because the alternative was about fourteen log lines a minute per account for good.
- **An unanswered sign-in is asked about, not guessed.** The Gateway sends the same identifier again and takes the
  Director's stored answer; it settles from a report only when that cannot be had, and a sign-in row whose machine
  has sent no report for 24 hours is ended so the page offers the sign-in again. This rule is the phase's own; it
  is written into contract 8.4.
- **A request the relay cannot take is refused on the call.** The hub method throws, as it does for a bad
  identifier, so nothing is held for a request past the limit.

## What is not done, and who has it

- Nothing is deployed, and nothing here ran against a real account, machine or sign-in: every test uses made-up
  accounts and isolated folders. The live proof is phase 5's.
- **For the Architect:** one cause can send many emails in a minute. The contract asks for one email for each
  machine for each account, so an approving browser that is signed out fails every renewal it approves: five
  machines and three accounts are fifteen emails. Raised by the review of 3948, not changed.
- A Gateway that stops between the mail service confirming an email and the row being marked sends that email once
  more after it starts. Known, said in the code and the contract, the same for both emails.
- The lookup column of the new failed-renewal rows is not pinned to byte order on PostgreSQL the way
  the switch rows' is; pinning it needs a migration and equality is exact as it stands.
- Every refused approval request writes one log line; a Director asking in a loop is bounded in memory, not in
  the log.
- Two tests that are not this phase's still wait for their owners: `WindowRestorePlacementTests` (issue 3934) and
  `TeamBillEndedTests` (issue 3908).

## Issues filed along the way

3861 (`session prompt` refuses a session waiting for input), 3862 (Codex seats: a withdrawn default model, and
`cc-devthrottle` not on the path), 3908 (`TeamBillEndedTests` fails under load), 3934 (`WindowRestorePlacementTests` reads the
real clock).
