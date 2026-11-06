# Review: step D, email off (pull request 4664)

**Verdict: CHANGES.** Two findings need a change before merge; both are small. Everything else checked holds.

Reviewed on 6 November 2026, at commit 770ec7b66, the two commits on top of main's ae0d9f4aa
(`bd26ec125` "email off - the morning summary goes to Your inbox, and no email is sent" and `770ec7b66` "open problems
in no factory get one morning message of their own"). The branch is one commit behind origin/main (ff55d00e4, pull
request 4658); a trial merge is clean.

## Scope

Requirements read: `MISSION-FINISH.md` section 5 "Email off" and section 7 (the morning-run test), `MISSION.md` decisions
row "Factory emails" and phase 5, and the owner's decision as restated in the brief.

Code read in full: the whole diff (31 files), with the surrounding code it depends on - `FactoryEmailSlots`,
`TenantScopedSweep`, the `Compose` method of `FactoryEmailComposer`, `CronRunResultFold.FactoryDays`, the `GatewayHost`
wiring order, `YourInboxFold`, `FactoryDefinitionCheck`'s `IsForbidden`, and the deleted `FactoryEmailSenderAndRoutesTests`.

Run here, on devlinux:

| What | Result |
|---|---|
| Gateway unit tests whose names contain FactoryEmail, FactoryDefinitionCheck, YourInbox or FactoryInbox | 606 passed, 0 failed |
| Core unit tests `BuiltInSkillsHaveOneSourceTests` and `ShippedSkillsTeachOwnershipTests` | 6 passed |
| Python tests for the command-line tool: the help and error contract, the actions catalogue, `email`, `factory email`, `inbox` | 924 passed, 1 skipped |
| client-core `yourInboxScreens.test.tsx` | 26 passed |
| A throwaway test I wrote and then discarded (below, finding 1) | passed, proving the gap |

Not done: the full `-Parked` gate (the Delivery Lead's job, in its own worktree); anything on the deployed Gateway; a
read of `devthrottle_internal` (not in this tree, so section 6 of `owner-emails.md` is taken as written). The
worktree was left clean: `git status` shows nothing but this file.

## Finding 1 - a factory with inbox news but no scheduled job writes no morning message (change needed)

**Claim under test:** "one Your inbox message per factory with news each morning", where news includes what came into
the factory's inbox (`FactoryMorningMessage.HasNews` returns true when `summary.Count > 0`).

**What the code does:** `FactoryEmailSweep.SweepTenantAsync` composes the account report first and returns at
`if (!report.Send)` before the per-factory loop runs, so `_summaryOf` is never read. `FactoryEmailComposer.Compose`
answers "nothing to report" whenever the account has no open problem, no factory day, no Mail Desk section and nothing
waiting - and `CronRunResultFold.FactoryDays` produces a day only for a cron job that names a factory. So an account whose
factories have no scheduled job gets no morning message at all, however much came into a factory's inbox. The kept row
is then `NothingToSend` for the slot, and every later tick that morning answers "already nothing-to-send", so the
message cannot arrive later either.

**Proof:** I added this test to `FactoryEmailTests` (same rig as the pull request's own first test), ran it, and
restored the file. It passed, which is the proof of the gap:

```csharp
await FactoryInbox.SendAsync(TenantId.Local, new FactoryInboxSendInput("research", "Look at the new competitor list.",
    FactoryInboxWords.Notes.None, FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone",
    TorontoTime(2026, 10, 10, 21, 15)));
_now = TorontoTime(2026, 10, 11, 8, 5);
var said = await Sweep().SweepTenantAsync(TenantId.Local);
var summary = FactoryInbox.Summary(TenantId.Local, "research", TorontoTime(2026, 10, 10, 8), TorontoTime(2026, 10, 11, 8));
Assert.Equal("1 message came in; 0 decisions made; 1 still waiting.", summary.Line);
Assert.True(FactoryMorningMessage.HasNews(/* the slot, no problems, no days, Factories() */, "research", summary));
Assert.Equal("slot 2026-10-11T08: nothing to send (this account has no factory schedules and no open problems, so there is nothing to report)", said);
Assert.Empty(InboxMessages());
Assert.Equal(FactoryEmailStates.NothingToSend, Store.Find(TenantId.Local, "2026-10-11T08")!.State);
```

The factory's Summary button shows one message waiting; `HasNews` says the factory has news; Your inbox gets nothing;
the kept row says there was nothing to report.

**Harm:** the owner's account has scheduled jobs, so his factories are not affected today - but the pull request's own
rule for "news" is contradicted by a gate written for the email, which never knew about the inbox. Any account whose
factories are inbox-only (a new customer, or the owner's account on a day every schedule is deleted) is told nothing of
what came in and what the Boss decided, while the step's description says the morning message carries it. The old
email did not reach the owner in this case either, so this is not "something the owner used to learn by email now
reaches nobody"; it is the new promise falling short of itself. The fix is small: decide "nothing to send" after the
per-factory loop, or make the account-level gate count inbox news. Add the test above (with a proper name) as the
regression test.

## Finding 2 - the owner-email list misses one owner email (change needed)

`docs/factory-inbox/owner-emails.md` section 1 lists `src/CcDirector.Gateway/Teams/TeamInvitationMailer.cs` as "Team
invitations. Kept. Goes to the person invited, not the owner." That file also declares `ITeamBillEndedMailer`
(line 18), and `TeamInvitationMailer` implements it (line 34). `src/CcDirector.Gateway/Teams/TeamBillEndedNotice.cs`
uses it to email the team's Owner once when a team account is refused because the bill has ended (its own summary,
lines 13 to 17). That is an owner email, found by the list's own second method (every user of `/account/email` and
`notify-owner`), and it is not on the list.

**Harm:** step D's "done when" is "a list of every owner email found and what became of each". A reader of the list
would believe the Teams folder sends nothing to the owner. It belongs beside the licence and network alerts as "Kept,
question for the owner": a system alert, not a factory. One row.

## Checked and holding

- **No email on any morning path.** `FactoryEmailSender.cs` and `IFactoryEmailSender` are gone from the tree (no reference
  anywhere outside tests). The sweep's constructor takes `YourInboxStore` and a summary delegate and no web request client, no
  account-notify client and no sender; `FactoryEmailSweep_TakesNoMailSender` fails if one is added back and if either
  deleted type reappears. The `GatewayHost` wiring passes `YourInbox` (built at line 2712, before the sweep at line 2985,
  so the null check cannot fire at start-up). The slot is 08:00 only, as before; the new log line that says so is correct.
- **What the owner used to learn by email reaches somebody.** Every open problem: in its factory's message, or - when its
  schedule is in no factory, an unregistered one or an archived one - in the one Gateway message from "Morning report -
  schedules in no factory", written only when there is at least one, once per slot, with no Reply offered and a reply
  refused in the Gateway's words (`no_reply_to_gateway`, status 409), all tested. Each factory's day line, its items
  waiting on the owner and the Mail Desk section (when the Mail Desk factory is that factory): in its message. The
  account report is still composed and kept, so the factory page's "Morning reports" card and the Read route still work;
  the card's words were renamed on the Gateway and the tests updated. Two narrow cases reach nobody, noted below as
  observations rather than findings because the loss is of stale items in a factory the owner archived.
- **Once per slot, once per factory.** `MorningOf` and `NoFactoryMorningOf` find a message by sender, seat and the slot
  boundary; the half-written-slot test shows a retry writes only the missing factory's message and leaves the one already
  written untouched. A tick after cancellation writes nothing and keeps nothing. Per-tenant failures stay isolated in
  `TenantScopedSweep.ForEachTenantAsync`.
- **The skill change obeys the one-source rule.** `dev-throttle.skill.md` is the one source; `.claude/skills/dev-throttle/SKILL.md`
  is frontmatter plus a byte-for-byte copy of the body (I diffed them: identical), and the two guard tests pass. The new
  section teaches `cc-devthrottle inbox send --subject --body`, which exists on main with exactly those options
  (`cli.py` line 4160), and `cc-devthrottle email owner`, which is kept. It teaches no spawn the product refuses. The
  seeder republishes on a body change, so the deploy will mint version 6.2's row.
- **The definition check.** The words come from the Gateway (`BriefEmailsTheOwner`), pinned by a test. A notice, never an
  error (`HasErrors` is false, tested). The negation guard is the same `IsForbidden` the Boss check uses, so "Never email
  the owner", "Do not email me" and "You must not send the owner an email" are not flagged (tested); email to prospects,
  customers and a mailbox is not flagged (tested); the owner's own "send me an email" anywhere in the brief turns the
  check off for that brief (tested, and documented as deliberate).
- **The command-line tool.** Help and docstrings reworded; the `factory email` command names and the `email owner`
  catalogue description are unchanged, so the pinned catalogue test still passes (it did: 924 tests green). Every
  filter and `--json` shape is untouched.
- **Clients only render.** The client hides Reply when the Gateway sends `replyTo: null`, and shows the Gateway's sender
  label verbatim; no client decides what a state means. Tested with a payload the client has never seen.
- **Tests fail without the change.** The sweep tests, the definition check tests, the Your inbox reply test and the
  fold tests reference types, members and words that do not exist on main (`FactorySummaryOf`, `FactoryMorningMessage`,
  `BriefEmailsTheOwnerPhrase`, `ForNoFactoryProblems`, "Morning reports"), so they do not compile on main;
  `FactoryEmailSweep_TakesNoMailSender` asserts the deleted types are absent, which is false on main.

## Observations - no change required

- **The fleet preamble still binds "email me".** `src/CcDirector.Core/Sessions/FleetPreambleTemplate.cs` line 34 tells every
  session at start: "Unless they say otherwise, 'me / my account / email me' means this user". It is an identity rule,
  not an order to email, so it is not an owner email - but the skill was changed precisely to stop saying that "email me"
  is about the user's email address, and the preamble reaches every session before any skill is fetched. Worth a row in
  the list and, probably, the same three-word edit.
- **The seat schema's example is still "Email the owner."** `src/CcDirector.Gateway/Factory/Definition/Schemas/seat.schema.json`
  line 178 gives it as the example of a branch step; `docs/factories/definition-format.md` was changed to "Tell the owner
  in Your inbox" and the schema was not.
- **Two narrow losses.** An item waiting on the owner from an archived factory, and the Mail Desk section when the Mail
  Desk's factory is archived while the Mail Desk stays on, were in the email and are now in no message (the orphan message
  carries problems only; `FactoriesOf` skips archived factories). Both describe a factory the owner chose to archive, so I
  do not count them as harm, but they are the edges of "reaches somebody".
- **Notices the regex will raise that are not about the owner.** "Email the user a welcome message" and "when a prospect
  replies, email him the price list" match the third pattern (the owner is "the user" and "him" there). A notice only;
  the message says the words were matched, not understood.
- **Team accounts.** The old sender answered a team account "not supported" once. The sweep now writes Your inbox
  messages for any tenant it is enabled for; whether a team tenant's Your inbox is readable from a client was not checked.
- **The factory page still says "Sent ..."** for a kept report's time (`FactoryEmailsFold.Item`). True enough of a report
  written to Your inbox, and the card's note explains it; cosmetic.
- **The list's search base.** `owner-emails.md` says main was searched at a09edb1e7; main is twelve commits past that.
  The only mail-related change in between is a workflow for staff emails, so nothing new was missed.

## Answers from the developer

**Finding 1 - accepted, fixed at the root.** `FactoryEmailSweep.SweepTenantAsync` now decides what the morning writes
first - each factory's message when `HasNews` says it has news, and the message for problems in no factory - and only
then decides "nothing to send", when there is nothing to write at all. The account report's own gate no longer stops the
factories: it was written for the email and never knew about the inbox. The report is still composed and kept as before;
when the report itself had something to say but no factory had news, the kept row's reason is the Gateway's sentence
`FactoryEmailSweep.NoNewsReason`. The regression test is your test with a proper name,
`SweepTenantAsync_AFactoryWithInboxNewsAndNoSchedule_GetsItsMorningMessage`: a message in research's Factory inbox, no
schedule anywhere, and one morning message written with the row marked delivered. Run against the sweep as it was, it
fails ("Strings differ": the sweep answered "nothing to send"); with the fix it passes. An account with no schedule and
no inbox news is still kept as "nothing to send" (`list` test, unchanged).

**Finding 2 - accepted.** `owner-emails.md` section 1 now has its own row for `TeamBillEndedNotice` through
`ITeamBillEndedMailer`: **Kept, question for the owner.** It is a billing notice to a team's Owner, ordered by the
owner's Teams ruling of 7 October ("tell the Owner"), not a factory or a session; it has to reach a team Owner who may
not open DevThrottle until the bill is paid, which is what an email is for, and Your inbox is per account, so it would
land in an inbox that person is not reading.

**Observations.** Two were taken because each was one line: the seat schema's example branch step is now "Tell the
owner in Your inbox", matching `definition-format.md`; and the fleet preamble's "email me" has a row in the list (an
identity rule, not an order to email, left to a change of its own because it rewrites every session's start text). The
others are left as you recorded them.

## Re-review after the answers

**Verdict: APPROVE.** Both findings are fixed at the root, every guarantee the first review checked still holds, and
nothing new was found that proves harm.

Reviewed on 6 November 2026, at commit 41af68b16 ("answer the review"), the one commit on top of the review commit
2403d1744. Only that commit and the answers above were read; the earlier commits stand as reviewed. A trial merge
with origin/main at 9f96fb043 is clean. The worktree was left clean: `git status` shows nothing but this file.

Run here, on devlinux:

| What | Result |
|---|---|
| Gateway unit tests whose names contain Factory, Inbox or Email | 3730 passed, 0 failed |
| The regression test alone, against the sweep as it was before this commit (the parent's `FactoryEmailSweep.cs` put in place, the test run, the file restored) | failed: "Strings differ", expected "wrote 1 morning message", actual "nothing to send (this account has no factory schedules ..." |

### Finding 1 - fixed at the root, with a test that fails without it

The root cause was the order of two decisions: the account report's "nothing to report" gate ran before the per-factory
loop. The commit reverses it. The loop now runs first and collects each factory's message into a pending list; the
message for problems in no factory is collected the same way; and only then, when the slot has no kept row and the count
of messages this morning is zero, is the slot kept as `NothingToSend`. The report's gate is no longer consulted to stop
the factories; its reason is kept as the row's reason when it applies, and the Gateway's own sentence
(`FactoryEmailSweep.NoNewsReason`) when the report had something to say but no factory had news and no problem is open
outside a factory. That second sentence is reachable only on the edges the first review already recorded as "two narrow
losses" (a day, a waiting item or a Mail Desk section for an archived or unregistered factory), and it is true there.

The regression test `SweepTenantAsync_AFactoryWithInboxNewsAndNoSchedule_GetsItsMorningMessage` is the first review's
proof with a proper name and the expected outcome inverted. I ran it against the parent commit's sweep and it failed
exactly as the developer says; against this commit it passes.

### The other guarantees, each checked against the new code

- **One message per factory with news.** The loop's body is unchanged apart from where the message goes (a pending
  list instead of the store); `HasNews` still gates it. The first sweep test and the new test each show one message.
- **None for a quiet factory.** `continue` when `HasNews` is false, as before; the first sweep test shows outreach gets
  none. An account with no schedule and no inbox news is still kept as `NothingToSend` (the `List` test's Saturday).
- **The message for problems in no factory, only when there are any.** The `orphans.Count > 0` gate is unchanged; both
  no-factory tests pass.
- **A retried slot writes only what is missing.** The `MorningOf` and `NoFactoryMorningOf` checks run before anything is
  added to the pending list, and a slot that already has a kept row skips the "keep first" block, so no second `Failed`
  row is written. The half-written-slot test still shows one new message, the old one untouched, and the kept row
  marked delivered with the count of all messages.
- **Kept before written, still.** The `Failed` "composed, not written to Your inbox yet" row is saved before any message
  is added to Your inbox; the pending list lives only in memory until then. A crash during composition now keeps no row
  at all, so the slot is tried again on the next tick, which is the same promise as before.
- **No mail sender in the sweep.** The constructor is untouched; `FactoryEmailSweep_TakesNoMailSender` passes.

### Finding 2 - the answer is sound

`TeamBillEndedNotice` (read on origin/main) tells a team's Owner once per ended bill, through `ITeamBillEndedMailer`
implemented by `TeamInvitationMailer`, and its own summary quotes the 7 October ruling "tell the Owner". The new row says
exactly that and files it beside the licence and network alerts as "Kept, question for the owner", which is where the
first review said it belonged. The reason given for keeping it as an email holds: the person refused is not the Owner,
so the Owner has no prompt to open DevThrottle, and a message in an inbox nobody is reading would not tell them. A fresh
search of the Teams folder on origin/main finds no other sender of email than those two files, so the list's claim that
the folder was looked at again is borne out.

### The two observations taken

The seat schema's example branch step now matches `definition-format.md`; no test pins the description's wording, and the
schema tests pass. The fleet preamble's row describes the line correctly as an identity rule and leaves the edit to a
change of its own, which is the right size for a sentence that reaches every session.

### Observation - no change required

- **A morning with inbox news and nothing in the report keeps a delivered row with no title.** On that morning the
  report is the composer's "not sent" report, so the kept row is `Sent` with an empty subject, an empty body and an empty
  factory list. The factory page's "Morning reports" card lists only rows whose factory list names the factory, so the
  card does not show that slot; before this commit the slot was `NothingToSend` and the card did not show it either. An
  unfiltered list of kept reports (the list route with no factory) shows the row with an empty title and the correct
  detail "Written to Your inbox as 1 morning message". Cosmetic: the message the owner reads is in Your inbox, and the
  row's state and detail are true.
