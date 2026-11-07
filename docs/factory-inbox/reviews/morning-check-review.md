# Review: the morning check of 7 November (commit 4fe474cbb, pull request 4699)

Reviewer: a separate review session (Factory Inbox - Reviewer - morning check record), in the worktree
`/home/soren/ReposFred/devthrottle-fi-qa` at commit 4fe474cbb, on devlinux, 7 November 2026, from 13:55 Coordinated
Universal Time. Only the newest commit was reviewed; the earlier commits of this branch were approved in
`end-to-end-proof-review.md`. Nothing in the report, the screenshots or the mission record was changed. The only
file this review adds is this one.

**Verdict: APPROVE.** Every claim I could check holds: the kept row, the live version, the archive, the stopped test
Director, the four screenshots against their captions and the check table, the code the report names, and the
inventory it says missed a sender. The evidence is honest about the goal: the morning email stopped, other paths
still email the owner, and both documents say so in their first sentence on the subject. The observations below
make no sentence of the commit false.

## What was reviewed

- `docs/qa/factory-inbox/phase-6-end-to-end/README.md`: the new sentence in the summary, step 6 ("The morning
  message - run by the Delivery Lead at 13:17 on 7 November", the check table, the screenshot table, "What still
  emails the owner"), and the changed line in "Evidence: what this run did NOT prove".
- The four screenshots `06a` to `06d`, each opened and read against its caption and against the check table.
- `docs/factory-inbox/MISSION-FINISH.md`: the changed row for pull request 4699 and the new section "The morning
  check (7 November, 13:17 UTC) and the status".

## What I ran, and what it showed

| Claim in the commit | What I did | Result |
|---|---|---|
| Check 4: the kept row `792303c9-2ac2-4ac3-9187-b64ba25944ad`, slot `2026-11-07T08`, state `sent`, 13:06:16, factories including `factory-inbox-test`, 67 problems, the detail sentence; every earlier row's detail is `-` | `cc-devthrottle factory email list` and `factory email show` of that row | Holds, word for word. The detail reads "Written to Your inbox as 10 morning messages: one per factory with news, and one for open problems in no factory when there are any. No email was sent." All nineteen earlier rows show `-` as the detail. |
| Live commit `7c1019b`, version 2.36.1, from `/healthz`; main had moved on since `9cbadd4` | Read `https://gateway.devthrottle.com/healthz`; `git merge-base --is-ancestor` on both commits | Holds. The probe answered version 2.36.1, commit 7c1019b; 9cbadd4 is an ancestor of 7c1019b, and 7c1019b is on origin/main. |
| Check 6: the test factory archived at 13:20 by the owner | `cc-devthrottle factory list --json` | Holds. `archivedAtUtc` is 13:20:22 on 7 November, `archivedBy` is the owner. The screen's "08:20" is the same moment in New York time. |
| Check 6: the test Director's container is not running; the command-line tool link is unchanged | `docker ps -a`; `readlink ~/.local/bin/cc-devthrottle` | Holds. No container named `fi-inbox-test-director` (the name in `fixture/launch-in-container.sh`) exists, running or stopped. The link points at `/home/soren/.local/share/cc-director/pyenv/bin/cc-devthrottle`. |
| "Schedule failed" notices come from `src/CcDirector.Gateway/Running/ScheduleFailureNotifier.cs` (issue 4436) through a mailer delegate, and the email-off inventory does not name it | `git show origin/main:` of that file; `git grep` over `docs/factory-inbox/owner-emails.md` on origin/main | Holds. The class takes a `ScheduleFailureMailer` delegate, its subject is `Schedule failed: {schedule name}`, and the file cites issue 4436. The inventory never names the class, the delegate or the subject. See observation 1 for where that email actually leaves. |
| The website's daily report is `devthrottle_internal`, `website/api/morning-report.js`, and is not in the inventory | `ls` of the local clone; `git grep` of the inventory | Holds. The file exists; the inventory does not name it. |
| No morning email for 7 November, the emails of 3 to 6 November at 13:05 or 13:06, "about fifteen" schedule-failed emails a day, the 12:01 example | Not checked | I have no access to the owner's mailbox. The kept row's "No email was sent" and the deleted sender (approved earlier) are the two pieces of evidence I can stand behind; the mailbox is the Delivery Lead's. |
| Check 2: the phone shows the same message, identifier `3789dd2c-7f07-42c3-aabf-fc50371974ad` | Not checked | No screenshot shows the identifier and there is no command-line route that lists Your inbox. The phone screenshots show the same title, sender, time and body as the Cockpit one, which is what the check is for. |

## The screenshots against the text

| Screenshot | Caption and check | Result |
|---|---|---|
| `06a-cockpit-morning-message.png` | Check 1: the Cockpit list and the open message | Holds. Title "Morning summary: Factory Inbox test - Saturday 7 November", from "Factory Inbox test - Morning summary", 08:00; the body opens with "12 messages came in; 12 decisions made (Delegated 7, Noted 4, Declined 1)."; one line per message with its decision; the step 4 line carries the Boss's reason in full; then "Factory Inbox test - 0 runs done" and "Reply to this message to leave a message in this factory's inbox."; the buttons Open Factory Inbox test, Reply, Mark unread and Archive. |
| `06a` | Check 3: ten morning messages, one per factory with news and one for schedules in no factory | Holds. The list shows DevThrottle Marketing, "Schedules in no factory: 42 problems still open", DevThrottle Sales, Citizenship, mindzie, Managed Marketing, Private, Factory Inbox test, Machine Care and Mail Desk: nine factories and one report, ten rows, one for the test factory. The tab says "All 12"; `06b` shows the other two, the step 4 "Declined" message and "Your inbox is live", both older. |
| `06b-phone-your-inbox-morning-list.png` | "Your inbox: the test factory's morning message among the others" | Holds. The test factory's row sits between Private and Machine Care, marked Read, with the same 08:00 stamp. |
| `06c-phone-morning-message-open.png` | "The message open: the summary line and the messages with their decisions" | Holds. The same title, sender and summary line as the Cockpit, and the first six message lines with their decisions and session identifiers. |
| `06d-phone-morning-message-end.png` | "The end of the message: what the factory did, and Reply" | Holds. The last lines of the list, "Factory Inbox test - 0 runs done", "No run finished in the last 24 hours.", the factory link, the reply sentence, and the four buttons. |

## Honesty of the evidence

The commit does not claim the goal is met. The summary of the report says "factories have not yet stopped
emailing"; step 6 says the goal "is met for the morning summary only"; the mission record's status line reads
"the Factory inbox and Your inbox are done and live; 'factories stop emailing' is not done." The section "What still
emails the owner" names four senders and says which repository owns each. The evidence section now says the morning
checks prove the morning summary only. That is the right shape: a reader who sees only the status line gets the true
state.

## Plain English and private data

- Added text uses two short forms: "QA" (the report's own title and the seat name, used throughout the approved
  report) and "UTC" once, in the new heading of the mission record, where the approved table above it already uses
  it. The report itself spells out Coordinated Universal Time. Neither is new to its file. See observation 2.
- No mention of any agent vendor in the added text. No token, key or credential in the text; no embedded text chunk
  in any of the four screenshots.
- The owner's mailbox address appears in check 5. It is already on origin/main in ten files and was ruled allowed in
  the earlier review as the account name the product itself shows. The Cockpit screenshot shows it truncated in the
  rail, as the earlier screenshots do.
- The screenshots show the names of the owner's other factories and the first words of their morning summaries. The
  names were ruled not customer data in the earlier review; the visible snippets are "FACTORY INBOX - LAST 24 HOURS
  N..." and nothing more.

## Observations (no harm proven; for the author's and the owner's judgement)

1. **Where the "Schedule failed" email leaves from.** The report says the sender was missed because the inventory
   "searched for the named owner-email clients only". That is right in effect, and the mechanism is worth a few more
   words for whoever fixes it: in `GatewayHost.cs` on origin/main the notifier's `mail` delegate calls
   `LicenseOwnerMail`, the license owner mailer. The inventory lists that mailer in section 1 as "License Switching:
   no account has room - Kept, question for the owner". So the inventory saw the mailer and recorded one of its two
   callers. Turning the schedule-failed email off means changing that delegate, not the license mailer.
2. **"UTC" in the new heading.** The mission record's new heading "The morning check (7 November, 13:17 UTC)" uses
   the short form; the report writes "Coordinated Universal Time" in full. The file already uses "UTC" in the table
   above, so this is consistent with the file and inconsistent with the writing rule. A one-word change if the author
   wants it.
3. **The summary sentence names two senders; the section names four.** The report's opening paragraph lists the
   "Schedule failed" notices and the `devthrottle_internal` seats as what still emails; the new section and the
   mission record add the kept system alerts and the website's daily report. "Other paths" covers it, and the
   section is one scroll away.
4. **Two phone screenshots show the voice-mode strip drawn over the license strip.** In `06b` and `06d` the "Voice
   mode is on" banner overlaps "3 license problems - Open Licenses" at the top of the phone; in `06c` the two stack
   cleanly. It looks like a moment during scrolling. It is the product, not the report, and is outside this commit;
   the Delivery Lead may want it as a small defect against the phone shell.

## Not covered by this review

I did not open the owner's mailbox, read Your inbox through the Gateway, or re-run any check. I did not review the
product code beyond reading the notifier, its wiring and the inventory to test the report's claims about them.
