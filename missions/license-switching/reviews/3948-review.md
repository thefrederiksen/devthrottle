# Pull request 3948 review - the owner email for a sign-in that cannot be renewed

Reviewed at `87d83310a`, against its merge base with origin/main `505d51a8f`. 13 October 2026.

## Verdict: changes needed

One must fix: a Director that restarts in the middle of a failure streak ends the streak on the Gateway, the log
says "a renewal has now worked" when none did, and the owner is emailed a second time. Reproduced out of tree.
Everything else the brief asked about holds, or is a should fix or a note below.

12 findings: 1 must fix, 4 should fix, 7 notes.

## Findings

### 1. MUST FIX - a Director restart ends the streak, and the owner is emailed again

`src/CcDirector.Gateway/Licenses/LicenseRenewalEmailService.cs:132-141` (the close), `:255-257` (the sentence).

What is wrong. The service ends a streak when the holding is still held and its `RenewalFailedAtUtc` is null, and
writes "a renewal has now worked". A null time does not say that. `LicenseStore.cs:196-197` says it itself: "a report
with no time says the last one worked OR NONE WAS TRIED". The Director makes the second case happen on every start:

- `src/CcDirector.Core/Licenses/LicenseUsageReporter.cs:56` keeps the failures in a dictionary in memory. Nothing
  stores or restores it. A Director that has just started has forgotten every failed renewal.
- `src/CcDirector.Core/Licenses/LicenseRenewer.cs:87-88` asks for a report when it takes the key holder lock, BEFORE
  the renewal run of lines 94-119. So the first report after a start carries the same sign-in, the same end, and no
  failure time. The run then fails as before and the next report says so.

When the minute check lands between those two reports the Gateway closes the row and opens a new one: a second
email for one failure streak. That is certain when the failing renewal takes longer than a minute to fail (the
reason `expired`, "no approval came back in time", and a browser approval that does not complete), and a matter of
timing for the reasons that fail at once. The same happens when the key holder moves to another Director on the
machine. The owner restarts Directors several times a day.

Reproduced (scratch project outside the repository, the real store on SQLite, the pull request's own code):

    report: A failing, ends T0+2d            -> check: 1 email, row open
    report: A, ends T0+2d, no failure time   -> check: row ENDED, "a renewal has now worked."
    report: A failing, ends T0+2d            -> check: 2nd email, 2nd row
    Assert.Single(sent) -> FAILED: The collection contained 2 items

Why it matters. It breaks the headline of the change (one email for each sign-in for each failure streak), it
writes a false sentence in the Licenses log, and the email itself promises "Another is sent only when a renewal has
worked and a later one fails" (`:246`).

The pull request's own test holds the defect in place:
`LicenseRenewalEmailServiceTests.cs:283-302` "renews" account A with a report whose sign-in ends EARLIER than before
(T0 + 11 hours against T0 + 2 days) and expects the streak to end. A renewed sign-in ends later, never earlier.

A direction, not a fix: end a streak as renewed on something PRESENT - the holding's `SignInExpiresAtUtc` is later
than the row's `NotAfterUtc`, or its `SignedInAtUtc` is after the row was opened - and leave the row open on a
report that only lacks the failure time. Or have the Director keep the failure across a restart. That choice is
the Tech Lead's.

### 2. SHOULD FIX - an email about a machine that has been silent does not say how old its report is

`LicenseRenewalEmailService.cs:147-157` (when), `:225-248` (the words), `:251-252` (the row).

What is wrong. The check emails on the stored holding however old the machine's last report is, and neither the
email nor the row says when the machine last reported or when the renewal last failed (`RenewalFailedAtUtc` is
never said anywhere). Reproduced: a machine silent for seven days is written about in the present tense,
"the-mac could not renew its sign-in ... and that sign-in expires in 2 days", with nothing to say this is a
week-old fact. The contract paragraph added by this change decides that a stale machine is still emailed about; I
do not dispute that. The sentence is what is missing.

Why it matters. The owner may have signed the machine in again by hand while its Director was off, and is then told
something that stopped being true days ago, with no way to tell. The email already on main is more careful here:
`LicenseDecisionEngine.NoRoomEmail` sends nothing on a stale reading (`LicenseDecisionEngine.cs:211`). The brief
asked for at least that much care. Also: a machine that never comes back keeps its row open and "Failing" for good.

### 3. SHOULD FIX - the machine's own sentence goes into the email exactly as it was sent

`LicenseRenewalEmailService.cs:239-240`.

What is wrong. `RenewalErrorDetail` is put into the body as "The machine said: ..." whenever it is present. The
Gateway takes that field from the report unchecked: `LicenseUsageIngest.Malformed` (`LicenseUsageIngest.cs:211-226`)
does not look at it, and `LicenseStore.cs:205` only trims it to 500 characters. Today's Director sends one of its
own fixed sentences (`LicenseApprovalExchange.cs:99-114`), so nothing from a sign-in appears today - but that is the
Director's promise, not something this Gateway holds. Reproduced: a detail with blank lines and a link arrives in
the owner's inbox as given, as paragraphs of an email from DevThrottle.

Why it matters. An email leaves the product and cannot be taken back. The email on main carries no free text from
a machine at all. The Licenses page shows this sentence only when the Gateway knows no reason
(`LicenseDisplayFold.cs:852-855`); the email shows it always, so for every known reason the owner reads the same
thing twice in two wordings (the test at `LicenseRenewalEmailServiceTests.cs:128-129`). Say the Gateway's own
sentence; show the machine's only where the page does, on one line, or not at all.

### 4. SHOULD FIX - no test holds the guard against two checks of one account at once

`LicenseRenewalEmailService.cs:49`, `:114-118`.

What is wrong. The brief asks for one email "across two passes that overlap". The guard that gives it is correct
by reading, and no test of the pull request reaches it. Out of tree, with the guard made to never refuse, all 28
tests of `LicenseRenewalEmailServiceTests` stay green. A test that starts a second check while the first is waiting
inside the mail service passes on the real code and fails without the guard (two emails).

Why it matters. The host starts a pass every minute whether or not the last has finished
(`GatewayHost.cs:6599-6603`), and a mail service that is slow is exactly when two passes overlap.

### 5. SHOULD FIX - the table's own description does not know the new kind

`src/CcDirector.Gateway/Data/Entities/LicenseEntities.cs:143-157` and the property comments below it;
`LicenseStore.cs:11-20`, `:38-42`, `:54-61`, `:367-368`.

What is wrong. The entity's summary still lists three kinds and says which columns `no-room` reuses; the new kind
reuses `ToAccountEmail`, `NotAfterUtc`, `ReasonCode`, `LastSentAtUtc` and `SendCount` and is not named there.
`LicenseLogKinds` has no word for it (the word lives only at `LicenseRenewalFailureLog.cs:28`), and the comments on
`Open`, `Ended` and the three owner email words still say "a no-room row". `LicenseStore.ListLog` says it returns
"every no-room event still open"; it now also returns every open failed-renewal row however old (`State == open`
with no kind). It has no caller outside tests, so nothing breaks today.

Why it matters. "Every reader filters by kind" is a rule the next reader learns from that summary.

### 6. NOTE - the guard for an account with no machines cannot be seen by any test, and is right

`LicenseRenewalEmailService.cs:112-113`; `LicenseRenewalEmailServiceTests.cs:466-472`.

Break 13 of the proof did not compile. I removed the two lines out of tree instead: all 28 tests stay green,
`AnAccountNoDirectorHasReportedFor_IsLeftAlone` among them, and nothing is written or logged. Without the guard the
check reads the account (three reads and one of the log, all quiet), finds nothing failing and nothing open, and
returns at `:127-128`. So the guard changes only how many reads an idle account costs each minute: one instead of
four. By reading it is correct and it is the same guard as `LicenseSwitchService.TickAsync` on main. The test is a
test of "an account with nothing to do does nothing", not of the guard, and the proof should say the guard is held
by reading only. To hold it by a test, count the reads.

### 7. NOTE - two ways a row stays unmarked and the mail service is called every minute (both as on main)

`LicenseRenewalEmailService.cs:176-195`.

- The send is confirmed and `RecordEmail` then throws (the database refuses the write): the row still reads "not
  emailed yet", so the next check emails again, and again each minute while writes fail. The owner would get an
  email a minute. The "no account has room" email has the same shape (`LicenseSwitchService.cs:705-713`).
- The mail sender throws instead of answering (`LicenseOwnerMailer.cs:95-104` catches an unreachable service and a
  timeout, nothing else): no wait is recorded, so it is tried every minute with no growing wait. Reproduced: five
  tries in five minutes, the row's reason still empty. No email goes out, so the owner is not harmed; the log is.

Not asked of this change alone; a fix belongs to both emails.

### 8. NOTE - "does not email again" after a restart is said without its one exception

The class comment says it honestly (`LicenseRenewalEmailService.cs:31-33`): a Gateway that stops between the mail
service's confirmation and the row being marked emails once more. The pull request description, the proof
("so a Gateway that restarts in the middle of a streak does not email again") and the contract paragraph say it
without the exception. One clause in each would make them true.

### 9. NOTE - a sign-in that leaves a report and comes back is emailed about twice

`LicenseRenewalEmailService.cs:136-138`. Reproduced: failing, then a report without the holding, then failing
again gives two emails. This is the contract as amended ("or in which the machine no longer holds the sign-in"),
so it is as built. It is the second road to more than one email for what an owner would call one failure, after
finding 1.

### 10. NOTE - the renewal check waits for the whole switch pass

`GatewayHost.cs:6611`, then `:6624`. The switch pass is awaited first, and it waits up to 60 seconds for any
account's Director that does not answer and for any "no account has room" email. So one account's silent Director
delays every account's renewal email by up to a minute. The other direction is safe: a slow mail service in the
renewal check holds up nobody's switch check (each minute starts its own pass) and nobody else's renewal check
(`LicenseRenewalEmailService.cs:74-106`, the accounts' checks are awaited together, each failure caught at
`:101-105`). That last part is by reading; the pull request has no test of two accounts with one slow sender.

### 11. NOTE - one cause can send many emails in one minute

One email for each machine for each account is what the contract asks. One cause - the approving browser signed
out - fails every renewal it approves, so an owner with five machines and three accounts can get fifteen emails
in the same minute. A question for the Architect, not a defect of this change.

### 12. NOTE - small things

- `LicenseRenewalEmailService.cs:242-243`: for the ACTIVE sign-in that has already expired the body says "its
  sessions stop when the sign-in ends". It has ended.
- `LicenseDisplayFold.cs:811`: `reasonText` of the row is the bare word (`owner-emailed`), as it already is for a
  `no-room` row on main. The page's sentence is the `summary`, which is finished.
- `GatewayDbContext.cs:2204-2206` pins `FromAccountEmail` to byte order on PostgreSQL because rows are looked up by
  it. The new rows are looked up by `ToAccountEmail`, which is not pinned. Equality is still exact there, and the
  proof reports the PostgreSQL test green.
- The machine name is now in the email SUBJECT (`:231-232`), unchecked beyond its length, as the account email
  already is on main.

## What I checked and found right

**One email for each holding for each streak (apart from finding 1).** Across minutes: the row is found open and
recorded `owner-emailed`, the check passes over it (`:153-154`). A failure time that changes every report does not
start a new streak: the row is matched on machine and account only (`:217-218`). Two machines holding one account
are two rows; two accounts on one machine are two rows. A failed send is recorded `owner-email-failed`, never as
sent, and waits 1, 2, 4, 8, 16, 32 and then 60 minutes, or longer when the mail service asks (`:204-209`); a row
recorded as sent is never changed back (`LicenseRenewalFailureLog.cs:117`).

**The restart, walked.**
- Stopped after the row is opened and before the email is sent: the row is stored with no reason. The first check
  after the start finds it and sends. The owner receives ONE email.
- Stopped after the mail service confirmed and before the row is marked: the row still has no reason (or
  `owner-email-failed`), so the first check after the start sends again. The owner receives TWO emails. Said in the
  class comment; the same on main (finding 8).
- Stopped after the row is marked: nothing more is sent. The owner has received ONE email.
- The growing wait is in memory only, so a restart tries a failed send again at once. Bounded, and the same on main.

**When a streak starts.** On `RenewalFailedAtUtc` being set and nothing else (`:124`); a reason word with no time
is not a failure. Only within the warning time by the Gateway's clock and the stored end (`:147`). An end that is
not known emails nobody. An end already past emails, and says "has expired".

**Tenant isolation.** Every read and write opens the store for the account passed in; the sender is given that
account and the mail service finds the owner (`LicenseOwnerMailer.cs:92`), the same channel as on main. A
self-hosted Gateway answers "no account", which is recorded as `no-owner-account` and not tried again in that
streak. The hosted test sends two accounts' reports over the real tunnel and finds one email, to the right account.

**The readers of the table.** `ListOpenSwitches`, `CreatePendingSwitch`, `OpenSwitch`, `ApplySwitchReadBack`,
`OpenNoRoom`, `ListOpenNoRoom` and `OpenNoRoomRows` in `LicenseStore`, `LicenseSnapshotSource.Build` (`:89`) and
`LicenseDisplayFold.SwitchInProgress` (`:428`) all filter by kind. `FindSwitch` matches on the switch identifier,
which the new row does not have. The bars are their own table. The log route (`LicenseEndpoints.cs:160`) returns
rows of every kind on purpose and the fold gives this one its words. There is no `LicenseSignInLog` in this tree.

**Lengths and migrations.** The kind is 14 characters in a column of 16; the state and reason words are the ones
already stored; the message is cut to 500. No entity, context or migration file is changed.
`dotnet ef migrations has-pending-model-changes` answers "No changes have been made to the model since the last
migration" for SQLite and, with `CC_GATEWAY_EF_PROVIDER=postgres` and the Postgres migrations project, for PostgreSQL.

**Quiet.** With an open row already emailed and the renewal still failing, a check reads and writes nothing and
logs nothing (`:161-163`).

**Rules.** Try and catch only at the timer boundary (`GatewayHost.cs:6622-6633`), at one account's boundary
(`:101-105`), and as log-and-throw in the log class. No fallback. Tests assert on what is present.

## The proof, checked

| Claim | Found |
|---|---|
| 522 license unit tests green | 522 passed, 0 failed, here at `87d83310a` |
| 24 license tests of the hosted suite green | 24 passed, 0 failed |
| 26 tests that run as 28 | 25 facts and 1 theory of 3 cases |
| The four product files did not change after the last break (`8a8fc58ac`) | true: the diff is empty |
| Break 13 not proven | true, and it cannot be by a test of behaviour (finding 6) |
| No pending model change | true on both providers |

Not run by me: `.\scripts\test-database.ps1`, `.\scripts\test-local.ps1` with or without `-Parked`, and the 19
breaks of the proof. No email was sent anywhere.

## The reproductions

A scratch test project outside the repository, linking this worktree's projects and the pull request's own test
file; the worktree was never edited (`git status` is empty apart from this file). 37 tests: the pull request's 28
green; of mine, the controls green and three red - the two of finding 1 and the one of finding 2. Two changed copies
of the service were compiled in place of the real one: the no-machines guard removed (28 green), and the
one-check-at-a-time guard made to never refuse (28 green, my overlap test red).

The project is in the reviewer session's scratch folder (`repro3948`, one test file of about 220 lines); it is
handed over on request.
