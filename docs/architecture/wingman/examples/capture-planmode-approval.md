# Capture: planmode-approval

Session 2d118a13-5f79-44fa-a222-71efac40d1da, Director :7887, state at capture: WaitingForInput

## What the USER SEES (screen grid, last 25 rows)

```
   - No real code; dummy files only
   - No credentials or secrets, ever
   - Force-push and reset are allowed and expected
   - Anything beyond README/CONTRIBUTING may be wiped without notice
 3. Workflow ? git status before a run, execute the scenario, git status after; reset with git clean -fdx + git reset
 --hard 428ade4.
 4. What not to file ? no issues/PRs for debris; real bugs found during QA go to the cc-director repo.
 ASCII-only text (per global instructions), plain markdown, no badges needed.
 Files
 - New: CONTRIBUTING.md (~30 lines)
 - Optional one-line addition to README.md Contents table referencing it ? only if desired; default is to leave README
 untouched (it has uncommitted changes already).
 Verification
 - git status shows exactly one new untracked file, CONTRIBUTING.md
 - Render-check the markdown (read it back / preview)
 - No commit ? per standing rule, do not commit unless explicitly asked
????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
 Claude has written up a plan and is ready to execute. Would you like to proceed?
 ? 1. Yes, and use auto mode
   2. Yes, manually approve edits
   3. No, refine with Ultraplan on Claude Code on the web
   4. Tell Claude what to change
      shift+tab to approve with this feedback
 ctrl-g to edit in  Notepad  ? C:\Users\<user>\.claude\plans\plan-how-you-would-hazy-moler.md
```

## What the TRANSCRIPT knows (parsed widgets)

```
  0. UserMessage: Plan how you would add a CONTRIBUTING.md to this repo (just a short plan), then exit plan mode so I 
  1. Read: 
  2. Bash: ls -A "D:/ReposFred/devthrottle-qa-scratch"
  3. Text: This is a tiny scratch repo (just a README), so the plan is short:
  4. Write: # Plan: Add CONTRIBUTING.md  ## Context  The repo (`D:\ReposFred\devthrottle-qa-scratch`) is a throw
  5. GenericTool: query=select:ExitPlanMode, max_results=1
  6. GenericTool (pending): See terminal to approve or modify the plan
```

## What Brief v1 made of it (GET /brief)

```json
{
  "status": "ok",
  "activityState": "WaitingForInput",
  "replyPending": false,
  "goal": "Plan how you would add a CONTRIBUTING.md to this repo (just a short plan), then exit plan mode so I can approve.",
  "lastAsk": "Plan how you would add a CONTRIBUTING.md to this repo (just a short plan), then exit plan mode so I can approve.",
  "didBullets": [
    "Acknowledged the repo is tiny with only a README",
    "Started planning to add a CONTRIBUTING.md file",
    "Decided to keep the plan short due to repo size",
    "Prepared to exit plan mode after presenting plan"
  ],
  "needsYou": "This is a tiny scratch repo (just a README), so the plan is short:",
  "needsYouSource": "fallback",
  "condenser": "openai:gpt-4.1-mini"
}
```

## Correct TurnBrief (authored by the strong model - the quality bar)

```json
{
  "intent": "You asked Claude to plan adding a CONTRIBUTING.md (plan mode), then
             present the plan for your approval.",
  "did": [
    "Explored the repo (tiny scratch repo, one README)",
    "Wrote a short plan: new ~30-line CONTRIBUTING.md (purpose, workflow, reset story,
     what-not-to-file); README untouched; verification steps; NO commit per your rules"
  ],
  "needsYou": {
    "statement": "Claude's plan is ready: add a ~30-line CONTRIBUTING.md, leave the
                  README alone, verify, no commit. Approve to execute (auto or
                  manually-approved edits), refine it, or tell it what to change.",
    "answerVia": "keys",
    "options": [
      { "key": "1 Yes, auto mode",          "send": "1" },
      { "key": "2 Yes, approve each edit",  "send": "2" },
      { "key": "4 Tell Claude what to change", "send": "4" }
    ],
    "evidence": "Claude has written up a plan and is ready to execute. Would you like
                 to proceed? / 1. Yes, and use auto mode / 2. Yes, manually approve
                 edits / 3. No, refine with Ultraplan... / 4. Tell Claude what to
                 change  (plan file: ~/.claude/plans/plan-how-you-would-hazy-moler.md)",
    "urgency": "blocking",
    "confidence": "high",
    "railLine": "Approve CONTRIBUTING.md plan?"
  }
}
```

## Findings

- PARTIALLY transcript-visible, unlike the picker: the pending ExitPlanMode tool shows up
  ("GenericTool (pending): See terminal to approve or modify the plan") AND the plan body
  is in the Write widget. The menu OPTIONS are screen-only. A good brief fuses both: plan
  summary from the transcript, options from the grid.
- The wingman should summarize THE PLAN in the needs-you statement - the decision is about
  the plan's content, not about the menu mechanics.
- v1's fallback grabbed a mid-reply paragraph as needsYou ("This is a tiny scratch repo
  (just a README), so the plan is short:") - confidently wrong, Exhibit B for D6.
- BOOT GOTCHA (cost a capture retry): a prompt sent seconds after session creation can land
  in the composer with the Enter swallowed - the session then sits WaitingForInput with the
  prompt UNSUBMITTED. A turn-brief pipeline must treat "prompt visible in composer +
  waiting" as its own state, not brief it as a completed turn.
