# Review: pull request 4737, a failed schedule tells its owner in Your inbox, never by email

Reviewed 7 November 2026 by a separate review session (fleet session ced314dd, "Factory Inbox - Reviewer - schedule
failed to Your inbox"). Commit `6ca00683d`, cut from origin/main `805b70af6`. Origin/main has since moved one commit
(`45524ef8a`, pull request 4699, the step F proof report), which touches none of the ten files this commit touches; a
trial merge reports no conflict. The worktree was not edited; the tree is clean.

## Verdict: CHANGES, one finding

The code is right and nothing on this path can email any more. One sentence of the shipped command-line reference
still says it does, and that reference is what agents read before they call the tool. Fix finding 1 and this is an
APPROVE; no second review is needed for a one-line edit of that sentence.

## Scope

What was read to answer the brief:

- The whole commit: `Running/ScheduleFailureNotifier.cs`, `GatewayHost.cs`, `Inbox/YourInboxStore.cs`,
  `Inbox/YourInboxWords.cs`, `Api/YourInboxEndpoints.cs`, `CronJobDto.cs`, the three test files, and
  `docs/factory-inbox/owner-emails.md`.
- On the same tree, what the change rests on: `Inbox/YourInboxFold.cs` (the from-label, the links and the reply
  target), the reply route and `OpenFactory` in `YourInboxEndpoints.cs`, `GatewayMintedKeyEntity`, the
  `YourInboxMessageEntity` columns, `CronFailureNoticeStore`, and the morning summary's own `ForMorningSummary` and
  `ForNoFactoryProblems` for comparison.
- Searches over the branch for anything that still emails or still says this path emails: `ScheduleFailureMailer`,
  every caller of `LicenseOwnerMail`, "Schedule failed", "owner's email", and the clients' inbox code for a branch on
  the sender kind or seat.

Runs made by this review, in the foreground in this worktree, with `~/.dotnet` first on the path,
`EnableWindowsTargeting=true`, `DOTNET_USE_POLLING_FILE_WATCHER=1` and `LOCALAPPDATA` set:

| Suite | Filter | Result |
| --- | --- | --- |
| Gateway.UnitTests | `ScheduleFailureNotifierTests`, `YourInboxEndpointTests`, `YourInboxStoreTests`, `YourInboxFoldTests`, `YourInboxWordsTests` | 76 of 76 passed |
| Gateway.Tests | `CronNotifyTests` | 23 of 23 passed |

## Finding

1. **`docs/cli-reference.md`, the `schedule` section (line 1820 on this branch) still says the notice goes to "the
   account owner's email on the hosted Gateway".** The paragraph "A failed schedule tells its owner by default (issue
   #4436)" lists the channels: the fleet notification channel, the owner's phones, the webhook, and the account
   owner's email. After this commit the fourth channel is a message in the account owner's Your inbox, and no email is
   sent. That file is the reference every agent is told to read before it calls a `cc-*` tool, so it is a display
   word the Gateway's own documentation owns, and it is now false. Change the clause to say a message in the account
   owner's Your inbox, from the schedule's factory when it has one. The matching sentence in `CronJobDto.NotifyOn`'s
   summary was already changed in this commit; this is the one that was missed.

## What was checked and holds

**Correctness.**

- The `ScheduleFailureMailer` delegate is gone from the notifier and from the host; the notifier's only writer is the
  `ScheduleFailureInboxWriter`, wired to `YourInbox.Add`. There is no mail sender field left to fall back to, and the
  search for the delegate's name finds it only in the inventory document, where it is named as history.
- `LicenseOwnerMail` keeps its three callers on the branch (the two license checks and `LicenseAlarmService`); the
  schedule failure wiring was the fourth, and it is the only one removed. The license alarm email is untouched.
- A schedule with a factory becomes a row with sender kind `factory`, that factory (trimmed), seat `schedule failure`,
  state unread, sent at the scan time; the fold shows it as "Booking - Schedule failure" and offers "Reply into the
  Factory inbox of Booking". A reply posted to it goes through the same `ReplyIntoFactoryAsync` as a morning summary's
  reply, so it lands in the Factory inbox as a typed message from the owner's device. The endpoint test proves that end
  to end against the real routes.
- A schedule with no factory becomes a `gateway` row with the same seat; the fold's label is "Schedule failure -
  schedule in no factory", the reply target is null, and the reply route answers 409 `no_reply_to_gateway` with the
  seat's own sentence. The morning report's sentence is unchanged for its seat, and the existing morning report test
  now asks for it by seat.
- Factory ids are matched ignoring case where it matters: the title map the fold reads is built with
  `OrdinalIgnoreCase`, and `OpenFactory` compares with `OrdinalIgnoreCase`, so the spelling in `CronJobDto.Factory`
  finds its factory.
- The hourly limit is not touched: `CronFailureNoticeStore.Claim` is called exactly as before, and the limit test now
  also counts the inbox rows (one inside the hour, two after it, the second saying "12 times"). The push and the fleet
  notice are unchanged lines. Notify Off still returns before any channel is reached, and the test asserts an empty
  inbox.
- A failing inbox write is caught, logged as `Your inbox message FAILED`, and does not stop the push or the fleet
  notice, which are sent before it; the test proves both still arrive. `ConsiderAsync` still returns true, so the scan
  count and the `_seen` bookkeeping are what they were.
- The row cannot exceed the table: the detail is capped at 4,000 characters before the body is built, and
  `ForScheduleFailure` cuts a body over 20,000 and a subject over 200 with a sentence saying so. The subject is also
  flattened to one line, which `Send` would otherwise refuse. The detail cut test proves the body fits and still ends
  with the reply line.
- The message id in the log line is real: `GatewayMintedKeyEntity` mints the id at construction, before `Add` is
  called.

**Tests.** Every test uses the in-process test database and an injected clock (`_now`), fakes for the fleet channel,
the push and the scope, and no port, process or sleep. Names follow the file's `Scenario_Result` convention. The
first test now also asserts the body says "To stop these messages, set the schedule's Notify to Off" and contains no
form of the word "email".

**Logging.** Entry and outcome are logged where the work happens: `ForScheduleFailure` logs the factory and body
length, `Add` logs the row id, factory and seat, and the notifier logs the write with the message id, sender kind and
factory, or the failure with the exception message.

**Plain English.** No abbreviation was added in prose, comments or the inventory row; the only short forms are the
pre-existing "UTC" in the body and parameter names in code.

**The Gateway owns every display word (rule 7).** Every sentence the owner reads is a Gateway constant or fold:
the subject, the body lines, the reply line, the from-label for both senders, the reply target label, the refusal
sentence by seat, and the cut sentences. The clients' inbox code was searched for a branch on the sender kind or the
seat and has none; the endpoint test asserts the label and the null reply target on the wire.

## Observations, not findings

- A schedule whose factory is archived, or whose factory id is no longer registered, is still told from that factory
  with a Reply offered; the reply is then refused in the factory's own words (`factory_archived`, `factory_not_found`).
  That is what a Boss's message from such a factory does today, so it is consistent, and the morning report's
  no-factory message covers the open problem the next morning. Worth knowing; nothing to change here.
- The factory floor's desk note "Failures also reach you by email, from N seats" (`FactoryFloorFold.cs` line 140) is
  built from the factory's own published `notify` arrows, not from this notifier, so it is outside this change. The
  Factory Inbox mission's email-off inventory may want to look at what those arrows mean now.
- `YourInboxEntities.cs` line 22 still says a sender is `session` or `factory`; the `gateway` kind was added by an
  earlier pull request. Pre-existing, not this change.
