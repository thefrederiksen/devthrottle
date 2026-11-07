# Review: pull request 4742, Factory Inbox, the proof that a failed schedule tells the owner in Your inbox

Reviewed 7 November 2026 by a separate review session (fleet session 62b4c925, "Factory Inbox - Reviewer - schedule
failure proof record"). Commit `d5133385d` on branch `factory-inbox/schedule-failed-proof`, whose parent is
`43172929e`; origin/main has since moved one commit (`9d7e7cf7c`, pull request 4743), which is the Mission Button
record and touches neither file this pull request touches. The pull request changes documents only: step 9 of
`docs/qa/factory-inbox/phase-6-end-to-end/README.md` with screenshots 09a and 09b, and in
`docs/factory-inbox/MISSION-FINISH.md` the table row for pull request 4737 and the section "After the morning check
(7 November)". The worktree was not edited by this review.

## Verdict: CHANGES

The proof itself stands: the run record, the two screenshots, the live Gateway and the deploy runs agree with the
step 9 text. Two sentences in the new MISSION-FINISH section say more than the evidence does, and one word breaks
the plain-English rule. Findings 1, 2 and 3 are needed before merge; 4 and 5 are recommended.

## What was checked, and what was found

Every claim in the new text was read against the screenshots and against what this review could reach itself.

**The live Gateway.** `https://gateway.devthrottle.com/healthz` answered at 16:53 Coordinated Universal Time with
version 2.37.0 and commit `4317292`. Commit `4317292` contains `7e766ac`, the merge of pull request 4737 (merged
14:54:06), so the live Gateway holds the change the proof is about. Matches the text.

**The deploy and rollback runs.** The last four runs of the deploy workflow and the last two of the rollback
workflow, with the lines their watch steps printed:

| Run | Commit | Started | Ended | Watch step's measurement |
| --- | --- | --- | --- | --- |
| deploy 42383776188 | `7e766ac` | 14:54:20 | 15:19:26, failed | pinned 15:01:05, live 15:02:04; longest unavailable stretch 467.7 seconds beginning 15:02:16, total 670.1 seconds |
| deploy 42383888485 | `4317292` | 14:55:48 | 15:23:25, failed | pinned 15:10:27, live 15:11:28; longest stretch 173.4 seconds beginning 15:11:45, total 440.4 seconds; during the watch production served `7c1019b` |
| rollback 42385335887 | to `7c1019b` | 15:13:33 | 15:18:50, success | pinned 15:14:14, served `7c1019b` from 15:14:40; no answer from 15:15:29 to 15:18:31; longest stretch 210.0 seconds |
| deploy 42386678204 | `4317292` | 15:30:11 | 15:50:47, success | pinned 15:37:21, live 15:38:34; longest stretch 117.2 seconds beginning 15:38:48, total 119.6 seconds, against the 120-second budget |

So: the first deploy of `7e766ac` did start at 15:01 (the pin), the deploy of `4317292` was started 88 seconds after
it (the text says 90), the workflow did measure 468 seconds, the rollback did go to `7c1019b` at 15:13, `7c1019b` was
unreachable for about three and a half minutes after its own restart, and the second deploy of `4317292` was alone,
measured 117 seconds, and stayed inside the budget. All of that matches the text. What does not match is in the
findings below.

**The test schedule.** `cc-devthrottle schedule list --json` returns 123 schedules and none is `cj_3852a3`, none is
named "Factory Inbox test - schedule failure proof", and none targets `fi-no-such-machine`. The schedule is gone, as
the text says. Its run record is still readable: `cc-devthrottle schedule runs cj_3852a3 --json` shows one run, fired
15:54:42 at machine `fi-no-such-machine`, ending "did not start a session: fi-no-such-machine was not reachable: DevThrottle
was not running there and could not be started", failure cause `machine-offline`, factory null. That is exactly the
run the step describes, at the time it gives.

**The screenshots.** 09a (Cockpit) and 09b (phone) both show a Your inbox message with subject "Schedule failed:
Factory Inbox test - schedule failure proof", from "Schedule failure - schedule in no factory", the body naming
`fi-no-such-machine`, the reason, "When: 2026-11-07 15:54 UTC", the line "To stop these messages, set the schedule's
Notify to Off.", and two buttons, Mark unread and Archive, with no Reply. The sender name matches
`YourInboxWords.ScheduleFailureSeat` on origin/main. Both screenshots show the Cockpit's "3 license problems" banner
and the Cockpit one shows the rest of Your inbox (six unread morning summaries), which is honest context, not a defect.
Matches the "Seen" paragraph word for word.

**Not verified by this review.** The owner's mailbox. The sentence "the owner's mailbox has no 'Schedule failed'
email after 14:56" is the Delivery Lead's observation and this review has no way to look at that mailbox; the text
is clear about who looked and when, so it is accepted as reported. The text is also honest that a schedule in a
factory was not exercised live and rests on the pull request's unit tests; that is the right thing to say.

## Findings

1. **"a clean 15-minute watch" is wrong on both counts** (MISSION-FINISH, "After the morning check", last sentence of
   the first paragraph). The deploy workflow's watch step runs for 600 seconds (`WATCH_SECONDS: 600` in run
   42386678204), not fifteen minutes: it sampled from 15:39:29 to 15:49:38. And it was not clean: the run's own
   warning reads "Longest outage 117.2s (began 15:38:48 UTC), total 119.6s, against a 120s budget ... Within budget,
   but a normal deploy is 4 to 7 seconds." The 117 seconds the sentence already quotes IS that watch's outage, which
   began fourteen seconds after the image was declared live at 15:38:34. Say what the run says: one outage of 117
   seconds beginning 15:38:48, total 120 seconds, inside the 120-second budget by under three seconds, and no further
   failed answer until the ten-minute watch ended at 15:49. If the Delivery Lead also watched by hand for fifteen
   minutes after that, say so separately and say what was watched.

2. **"as the five-minute rule says" overstates how the rule was followed** (same paragraph). The rule written on
   line 219 of the same file is: when the stall passes five minutes, roll back. The first outage began 15:02:16 (run
   42383776188) and the rollback was dispatched at 15:13:33, eleven minutes later; by then the first stretch had
   already ended at about 15:10 and the second deploy's restart had begun a new one at 15:11:45. The rollback was
   the right action, but it came about six minutes after the five-minute line, not on it. Say "rolled back to
   `7c1019b` at 15:13, eleven minutes after the first outage began; the five-minute rule asked for it at 15:07", or
   drop the clause. A record that says the rule was followed when it was followed late will be read as proof that
   the rule works.

3. **"QA report" uses an abbreviation** (MISSION-FINISH, "proven live at 15:54 (step 9 of the QA report)"). The rule
   for this repository is plain English with no abbreviations, and the brief for this review asked for none in the
   new text. The file already uses the term many times in older text, which is not a reason to add one. Write
   "the quality assurance report" or "the end-to-end proof report" and, if the link is wanted, name the file.

4. **"read from /healthz" places the 15:38:34 time in the wrong source** (README step 9, first paragraph, and the
   table row says 15:38 the same way). `/healthz` reports the commit and the server's current time; it does not say
   when a commit went live. The 15:38:34 figure is the deploy run's third consecutive healthy answer ("New image
   4317292 live in production after 68s", run 42386678204). Recommended wording: "live from 15:38:34 Coordinated
   Universal Time by the deploy run's log; `/healthz` answers `4317292`".

5. **"unreliable from 15:04 to 15:13 (the workflow measured 468 seconds)" is imprecise, not wrong** (MISSION-FINISH).
   The 468 seconds is the longest single stretch, which began 15:02:16 and ended at about 15:10, two minutes before
   the window the sentence gives; the watch that measured it ran 15:04 to 15:14 and recorded 670 seconds unavailable
   in total, and the second deploy's watch recorded a further 440 seconds up to 15:22, of which most was the
   rollback's own restart. "Unreliable from 15:02 to 15:19, with 468 seconds the longest single stretch" is what the
   runs show. Recommended, since the paragraph exists to be honest about the cost of this deploy.

## What this review did not do

It did not re-run the schedule, send a message, or touch the live Gateway beyond reading `/healthz` and the schedule
list and run record. It did not read the owner's mailbox (see above). It did not check the older text of either file;
only the lines this pull request adds.
