# Capture: multiselect-checklist

Session 8a5e12fa-330c-44da-9734-395e1c29ad54, Director :7886, state at capture: ?

## What the USER SEES (screen grid, last 25 rows)

```
  ? ?? Commit the remaining output/-contract files after QA
     ? QA: Studio + BPMN AI jobs from localhost against dev agents
     ? HTML report: both agent jobs, output/ rule proven
     ? Verify slice B compiles (resume the interrupted build)
     ? Review slice B edits for completeness
????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????  ? Next steps  ? Submit  ?
What should I do next? (pick any that apply)
?
?????
? 1. [ ] Finish Studio proof + report
  Commit the uncommitted pieces: agent sandbox.py + skill Rule 5, architecture doc, QA test cases. Build + review gate first.n progress.
  3. [ ] File the 2 new bugs
  Create DevOps tickets for the chat-first-no-data start failure and the lost-outputs-on-abandoned-page issue.
  5.e[t]tTypebsomethingageboutput loss a  urgent and fix it this sess on (drain/persist before container reap), not just file it.
     Submit
???????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
  6. Chat about this
Enter to select ? ?/? to navigate ? Esc to cancel
  10 tasks (7 done, 2 in progress, 1 open)
  ? Commit the remaining output/-contract files after QA
  ? QA: Studio + BPMN AI jobs from localhost against dev agents
  ? HTML report: both agent jobs, output/ rule proven
  ? Verify slice B compiles (resume the interrupted build)
  ? Review slice B edits for completeness
   ? +5 completed
```

## What the TRANSCRIPT knows (parsed widgets)

424 widgets - a long-running real work session. The FINAL widgets at capture time:

```
  419. GenericTool: command=Set-Location ".../TC_OUTPUTR..." (QA test run)
  420. Text: The full Studio run is going with the page held open this time, so the drains persist...
  421. UserMessage: Can you please summarize each finding and be more clear on what you want, what's good, what's bad, a
  422. Text: Let me check the in-flight Studio run, then give you a clean breakdown.
  423. Read:
  424. GenericTool: command=Get-Content .../phase_c.log -Tail 12 ... (checking the in-flight run)
```

THE MULTI-SELECT QUESTION IS NOWHERE IN THESE 424 WIDGETS - it is a pending
AskUserQuestion, invisible to the transcript until answered (same as the
single-select picker capture).

## What Brief v1 made of it (GET /brief)

```json
{
  "status": null,
  "activityState": null,
  "replyPending": null,
  "goal": null,
  "lastAsk": null,
  "didBullets": null,
  "needsYou": null,
  "needsYouSource": null,
  "condenser": null
}
```

## Correct TurnBrief (authored by the strong model - the quality bar)

```json
{
  "intent": "Mid-flight QA of the agents' new output/ rule: BPMN job proven, Studio
             rerun in progress; you asked for a clear findings summary and Claude is
             asking how to proceed.",
  "did": [
    "Proved the BPMN agent obeys the output/ rule (rail shows exactly the contract rows)",
    "Found 2 real bugs: chat-first start failure, outputs lost when the page is abandoned",
    "Started a clean end-to-end Studio rerun (page held open) - still running",
    "Drafted the next-steps plan and opened a pick-any-that-apply checklist"
  ],
  "needsYou": {
    "statement": "Claude proposes 4 next steps and wants you to pick ANY combination:
                  finish the Studio proof + report, commit the uncommitted output/-rule
                  work, file the 2 new bugs as tickets, and/or treat the lost-outputs
                  bug as urgent and fix it now rather than just filing it.",
    "answerVia": "keys",
    "selectionMode": "multiple",
    "options": [
      { "key": "1 Finish Studio proof + report",  "send": "1" },
      { "key": "2 Commit the output/-rule work",  "send": "2" },
      { "key": "3 File the 2 new bugs",           "send": "3" },
      { "key": "4 Fix lost-outputs NOW",          "send": "4", "note": "escalates a bug to this session's scope" }
    ],
    "submit": "\r",
    "evidence": "What should I do next? (pick any that apply) / 1. [ ] Finish Studio
                 proof + report / ... / Enter to select - up/down to navigate -
                 Esc to cancel",
    "urgency": "blocking",
    "confidence": "high",
    "railLine": "Pick next steps: proof / commit / file bugs / fix now"
  }
}
```

## Findings - this shape BROKE the frozen contract (v2.1 amendment)

1. **Multi-select**: "pick any that apply" with checkboxes `[ ]` and a separate Submit.
   One `options[].send` per option is NOT a complete answer anymore - the user toggles
   SEVERAL options, then submits. Contract gains `selectionMode: "single" | "multiple"`
   and a `submit` send; multi-select option buttons toggle (tap 1, tap 3) and a Submit
   button sends the final sequence.
2. **Asking while still working**: the spinner ("Preparing slice B commit... 39s") runs
   UNDER the questionnaire - the session is blocked on the user AND producing output
   simultaneously. State machines that assume asking implies idle are wrong; the
   detector/brief must allow blocking-question + active-work at once.
3. **Transcript-blind at scale**: 424 widgets of real session, and the question exists
   in none of them. Grid-only, same as the single-select picker.
4. **The grid was torn** even while parked on the question (option rows overdrawn with
   stale text). The wingman needs either re-snapshot-on-tear or enough robustness to
   read through corruption - one more reason interpretation needs a strong model.
5. **Old-Director /brief = all nulls** (endpoint absent) - the degrade ladder is what
   a real mixed fleet actually exercises.
