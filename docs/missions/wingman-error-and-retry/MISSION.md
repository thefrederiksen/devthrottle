# Mission: Wingman error and retry

Written 2026-09-19 by the Architect seat (session 115, SOREN_NORTH). The owner has said go.
This document is the Delivery Lead's whole mandate. The Architect seat is shut down after the handover;
take your answers from here, and from the owner only when this document cannot give one.

This document names no model provider and no hosting detail, so it can be copied into the product
repository's record as it stands. Keep it that way: the product repository is public.

## 1. The mission

When the Wingman fails to read a stopped session, the session card says so, the Gateway tries again
on a visible schedule, and a good answer with a bad button list still gets narrated.

## 2. The why

The owner runs the fleet by ear. A red session is one that needs him, and the Wingman's narration is
how he hears what it needs without opening a terminal. On the evening of 19 September, ten of his
eleven red sessions had no voice, and nothing on the card told him anything had gone wrong. In his words:

> "We can't just have it go red and not tell anything to the user."

Measured on the live fleet (107 readings, 29 sessions): 68% of readings succeeded, 15% got no answer
from the model inside the deadline, 16% were refused by a rule about the button list. A failed reading
is saved against the screen and never asked again, and a red session's screen does not change until
the owner acts - so every failure on a red session is permanent silence. Full investigation:
`D:\ReposFred\devthrottle_internal\docs\incidents\2026-09-19-voice-missing-on-red-sessions.html`.

This is the second day running this was reported. The 18 September repair softened one refusal rule
and the refusals moved to another. This mission deals with the class, not the instance.

## 3. The goal

All of these are true on the live hosted Gateway, shown in one QA report:

1. A session whose Wingman reading failed shows a **"Wingman error"** tag on its card, in the Cockpit
   and in the mobile app, with a line saying when the next retry is and which one it is
   (for example "retry 2 of 8 in 40 seconds").
2. The Gateway retries a failed reading by itself on the schedule in section 4, and a retry that
   succeeds clears the tag, writes the narration, and produces voice with no person involved.
3. When the schedule is used up, the card says there was a problem and nothing more is scheduled.
4. A button to ask again is on the card from the first failure, and a press makes one attempt at once.
5. A model answer whose button list breaks a rule is narrated and spoken, with no buttons shown, and
   is not an error.
6. No card ever says an attempt is coming when none is booked.

The proof is the flow AND the failure cases, plus the fleet measurement in section 7 run before and after.

## 4. Decisions - the owner's words

The raw record, including the part not suitable for a public repository, is
`D:\ReposFred\devthrottle_internal\docs\missions\wingman-error-and-retry-2026-09-19\OWNER-RULINGS.md`.

**Show the failure.**
> "First of all, we should show this error on the session. [...] We need to show wingman error. I just
> show a little tag wingman error."

**Retry, and show the retrying.**
> "So what we can do is we can retry. And... We should actually write on the card, show when we're
> retrying, how long in between."

**The schedule.** His dictated words, with his own correction:
> "So we do single minutes, we do three tries. And then we wait every five minutes and we do three
> tries. And then after that [...] 30 minutes and we do retries. And then after that, we can just say
> that there was a problem and then we put a button on there."

The Architect put this reading back to him: **three tries one minute apart, three tries five minutes
apart, two tries thirty minutes apart, then stop** - eight retries over about an hour and twenty
minutes. Also put to him: **one schedule for every kind of failure** (no split between a timeout and a
refusal), **the button shown from the first failure** rather than only at the end, and **a bad button
list drops the buttons and keeps the narration, and is not an error**. His answer to all of it:

> "I agree with you and want you to just start the implementation of this. and get it deployed as soon
> as possible."

**Production deploy is granted for this mission**, by that sentence (method law 18). It is granted for
this mission only, and only through the `deploy-hosted-gateway` workflow - see section 9.

**He asked for simpler if simpler exists:**
> "Something like this, or if you can find this simpler solution, let me know."

So where the design below can be made smaller without losing a goal, make it smaller and say so in the report.

## 5. Design

Everything is in the Gateway and the shared client package. No Director change is expected. All paths
are in the product repository, read at `origin/main` 736d9afe2.

### 5.1 A failed reading is asked again (the root cause)

`src/CcDirector.Gateway/Wingman/TurnVerdictService.cs`, the reuse path near line 1496: a stored FAILED
reading on an unchanged screen is returned as-is, with the words "the last answer about this unchanged
screen failed, and it is not asked again". That rule was written to cap cost. It stays true for a
reading that SUCCEEDED (an unchanged screen is never paid for twice). For a failed reading it is
replaced by the schedule: the reading is asked again when its next retry is due, and not before.

The schedule is per stop: 1, 1, 1, 5, 5, 5, 30, 30 minutes after the previous attempt. It ends on the
first success, on a new turn, when the session goes back to work, or when voice and ownership mean no
narration is owed (a session another live session owns is not narrated - that rule is unchanged).

**One mechanism.** `WingmanVoiceService.cs` already holds a retry ledger (`ModelRetries`,
`MarkAbandonedIfNothingPending`, `NarrationAbandoned`) and the comments describe a judge re-attempt
ladder of ten, thirty and ninety seconds. Read those first. The schedule above REPLACES what is there;
it is not a second ladder beside it. If two retry mechanisms exist after this mission, the mission is
not done. The in-reading second attempts (the judge's 30s-then-60s, the narration's immediate second
try) are inside one attempt and may stay.

A rate limit that names its own wait is honoured: the next retry is the later of the schedule and the
wait the provider named.

The idle sweep passes every session about every 45 seconds; it is the natural clock for "is a retry
due". Do not add a timer per session if the sweep can carry it.

### 5.2 A bad button list costs the buttons, not the reading

`src/CcDirector.Core/Wingman/TurnVerdictContract.cs`, lines 656-680 and the per-option checks above
them: exactly one option, more than one option marked recommended, an empty `send`, an over-long key
or note, too many options. Today each refuses the WHOLE answer. After this mission each of them drops
the WHOLE option list - `menu` and `options` become "none" - and the rest of the answer stands: state,
label, agentRecommends, and so the narration call runs and voice follows.

Nothing is repaired and nothing is guessed: no button is shown that the model did not clearly choose.
The reason the buttons were dropped is recorded on the stored reading and in the trace, so it is
answerable by query, and it is visible in the Wingman debug view. It is NOT a Wingman error on the card.

Rules that refuse for reasons outside the option list (an unknown state word, an unreadable reply) stay
refusals. They are failures, they show the tag, and the schedule retries them.

The existing tests assert the old refusals. Updating them is expected.

### 5.3 The card

The Gateway already sends a `voiceDisplay` object per session (`VoiceDisplayFold.cs`,
`VoiceDisplay.cs` in Contracts) with kinds including `gaveUp` ("Voice did not arrive", no button, and
the message "The Gateway is still trying") and `notNarrated` ("Turn not narrated", with a button).
On 19 September seven sessions showed `gaveUp` while nothing was booked for them. That sentence was false.

After this mission the Gateway sends, for a session whose reading failed: that it failed, a short plain
reason, which retry is next and when (an absolute time, so the clients count down themselves), how many
remain, and whether the schedule is used up. The clients draw:

- a small **"Wingman error"** tag on the session card - Cockpit and mobile, which share
  `packages/client-core`;
- one line: "retry 2 of 8 in 40 seconds", or when used up "The Wingman could not read this stop.
  Nothing more is scheduled.";
- the ask-again button, from the first failure. A press makes one attempt now and does not reset or
  consume the schedule.

The tag must not depend on voice mode being on. The owner's words were "it now has to run every time":
a reading that failed is an error whether or not he is listening.

Fold `gaveUp` and `notNarrated` into this one state if that comes out simpler; two near-identical
silent states with different buttons is part of what made this hard to see. Whatever survives, goal 6
holds: no text claims an attempt that is not booked.

The desktop Director's own session list is out of scope unless it reads the same field at no cost.

### 5.4 Out of scope

- Why the model sometimes does not answer. Not known. The Wingman debug view (administrators only)
  holds the prompts and timings; a session's key is refused there and that is correct - do not work
  around it. If the QA run shows a pattern, put it in the report as a finding, not a fix.
- Changing the model, the provider, the deadlines, or the prompt.
- A no-model fallback that reads the raw last message aloud. Asked on 18 September, not ruled. Do not build it.

## 6. Phases

One phase. The three changes touch the same files and ship in one deploy. No Tech Lead is needed unless
the Delivery Lead finds the work splitting; if it does, the natural split is Gateway (5.1 + 5.2) and
clients (5.3), with the wire shape of 5.3 agreed first.

1. Developer builds 5.1, 5.2, 5.3 with tests, in its own worktree cut from `origin/main`.
2. Reviewer on a different agent family from the one that built it, sent by the Delivery Lead. Tell it plainly:
   report only what materially matters - correctness, data loss, security, a user-visible break.
3. Merge to main. Deploy through the workflow. QA on the live fleet. Report.

## 7. The check

**Code decides first.** In the Developer's worktree:

    pwsh scripts/test-local.ps1 -Fast

and the full `pwsh scripts/test-local.ps1` before merge. The Gateway suite holds a machine-wide lock; a
run refused at the lock is not a failed test - run it again when the lock is free. Do not wait on the
50-minute continuous integration job to learn what the local gate already says.

New tests this mission owes, each explained in plain words in the pull request:

- a failed reading on an unchanged screen IS asked again when its retry is due, and is NOT asked before;
- the schedule is exactly 1, 1, 1, 5, 5, 5, 30, 30 and then stops; a new turn ends it; a success ends it;
- a successful reading on an unchanged screen is still never asked twice (the cost rule survives);
- each option-list rule drops the options and keeps the reading, and the reason is recorded;
- a reading refused for a non-option reason still fails and is still retried;
- the display says "nothing more is scheduled" exactly when nothing is booked, and never otherwise;
- a button press makes one attempt and leaves the schedule as it was.

**Then the live fleet.** The same measurement the investigation used, from any session on the account:

    GET {CC_GATEWAY_URL}/sessions                          -> voiceDisplay per session
    GET {CC_GATEWAY_URL}/sessions/{id}/turn-verdicts?count=25

with `Authorization: Bearer {CC_GATEWAY_SESSION_KEY}`. A working script is
`D:\ReposFred\devthrottle_internal\docs\qa\voice-watch-2026-09-18\capture.py`. Before the deploy, on
19 September: 10 of 11 red sessions silent. After: every red session the owner owns either has voice,
or shows the tag with a retry booked, or shows the tag with "nothing more is scheduled" - and no other state.

The QA report must show real cards, by screenshot, from the Cockpit and the mobile app: a failure with
a retry counting down, a retry that succeeded and cleared the tag, a used-up schedule, a button press,
and a bad-button answer narrated with no buttons. Failure cases can be produced on a local Gateway with
a judge that is made to fail; say in the report which shots are local and which are live.

## 8. Merge plan

One pull request if the diff stays reviewable, two (Gateway, then clients) if not. Merge on green local
gate plus the review answered. The branch lives less than a day. The repository is at its cap of open
pull requests often - finish this one rather than parking it.

## 9. Where it ends

**In production**, by the owner's grant in section 4. The ONLY way to deploy is the
`deploy-hosted-gateway` skill (the Cockpit and the mobile app ship in the same image). No hand-rolled
`az` command, no portal, no manual swap, ever. A deploy costs a short live outage; the owner has
accepted that for this mission. A watch job that fails on the outage budget usually means the deploy
LANDED - check what is live before assuming it did not. If the workflow cannot do what is needed, say
so and stop; do not work around it.

Then: the record (this document, the review, the answers to findings, the QA report) merged under
`docs/missions/wingman-error-and-retry/` in the product repository, and one page for the owner,
published with `cc-dev-reports open <file.html>` (hand-written HTML in the dev-report shape; the
contract is `packages/client-core/src/devreports/CONTRACT.md`).

## 10. Questions

None open. Both were asked and answered on 19 September (section 4).

## Standing rules every seat on this mission carries

- Never put the name of any assistant, model vendor or agent on anything: no co-author trailer, no
  "generated with" line, in commits, pull requests, issues, comments, code or documents.
- ASCII only in code, output and documents.
- Never name a model provider in the public repository.
- One worktree per workstream, cut from `origin/main`. Never build in a shared checkout, and never run
  a whole-tree git command (`reset --hard`, `clean`, `checkout .`) anywhere another session may be working.
- Every seat is a visible session in the Mission, named `Wingman Error - <Role> - <what it does>`, opened
  in the foreground. No hidden sub-agents, nothing in the background.
- Merged to `origin/main` is the only done.
