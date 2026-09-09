# Phase B handoff - Seat 3: the controls people can see

**You are the Manager for Phase B.** Read `missions/stop-a-session.html` (the work and the six
rulings), `missions/stop-a-session/architect-state.md` (the code facts and the route decision),
`missions/stop-a-session/phase-a-report.md` (what Phase A actually built, which may differ from what
it was asked to build - believe the report and the code, not this note, where they disagree), and
`cc-devthrottle workflow instructions mission` (the conduct). This note is the phase.

- Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`.
- **You do not merge to main.** The Architect lands everything. Push the branch; that is your done.
- Do not reopen a ruling. If one is wrong, bring it to the Architect and it changes in one place.

## Phase A already built the whole engine

`POST /sessions/{sid}/stop` exists, takes a required reason, records it, and answers with the verdict
word, a finished `headline`, an ordered list of `details` lines, and the facts underneath. **You are
not building a stop. You are pointing three surfaces at the one that exists and making them say what
it said.** Read the exact answer shape in `handoff-phase-a.md` and confirm it against the code Phase A
actually landed.

## The rule that governs every line of this phase

**No surface composes a sentence.** The Gateway folded the words; a client renders `headline`, then
each of `details`, in order, verbatim. If you catch yourself writing a conditional in a view that
decides what a stop OUTCOME MEANS - as opposed to how to lay it out - it belongs on the Gateway, and
you come back to the Architect. That is house rule 7 in `CLAUDE.md` and it is the whole reason the
answer has a `headline` field at all.

## The correction this phase rests on

The mission document says the Cockpit cannot end a session. **It can, today, and that is the bug.**
`apps/cockpit/src/sessions/SessionMenu.tsx` has a Close action behind a confirmation; it calls
`killSession` in `packages/client-core/src/api/client.ts`, sends no reason, and on success shows
nothing at all - it just closes the dialog. That is the mission's complaint, already built, in a
nicer font. Your job on the Cockpit is to re-point a control and give it a voice, not to add one.

## The four things this phase builds

1. **`packages/client-core/src/api/client.ts` - one shared function.** Replace `killSession` with
   `stopSession(sessionId, reason)` calling `POST /sessions/{sid}/stop` and returning the typed
   outcome (verdict, headline, details, and the facts). Every surface goes through it. Do not leave
   `killSession` beside it as a second way out; a silent second path is exactly what Ruling 5 forbids.

2. **The Cockpit** (`apps/cockpit/src/sessions/SessionMenu.tsx`). The Close action becomes **Stop**.
   It asks for a reason before it acts - the reason is what the Gateway requires, so an empty box
   cannot be submitted, and the control says why. When the stop returns, the dialog shows the
   `headline` and each `details` line and stays open long enough to be read. It must not close
   silently on success; that is the defect.

3. **The mobile app** (`apps/mobile/src/components/useSessionManage.ts`). It calls the same shared
   function, so it comes with you. This is not scope creep - leaving it on the old function would
   either break it or leave the silent path. Give it the same reason prompt and show it the same two
   pieces, laid out for a phone. Content is the same; layout is the shell's business.

4. **The Director window** (`src/CcDirector.Avalonia/MainWindow.axaml.cs`, `CloseSessionAsync` around
   line 2960). Its close today calls `KillSessionAsync` and `RemoveSession` directly - a second
   implementation of the stop that records nothing. Re-point it at `POST /sessions/{sid}/stop`
   through the Gateway, ask for the reason first, and show the returned `headline` and `details`.
   - **`CloseAllSessionsAsync` (around line 2489) is the Director shutting itself down. Leave it
     alone.** It must not depend on the Gateway, and this mission does not touch it.
   - The Director already talks to the Gateway constantly; `src/CcDirector.ControlApi/GatewayClient.cs`
     is where its authenticated client lives. **If it turns out the Director genuinely cannot make
     this call, STOP and bring it to the Architect.** Do not answer it by restoring a silent local
     kill - Ruling 5 names that explicitly as the wrong fix, and says the right one is to let the
     Director hold the reason and forward it.

## The house rules this phase will be judged against

- **Responsive interface.** Every one of these is a click that does file and network work. The dialog
  appears at once and says it is working; nothing blocks the interface thread. `CLAUDE.md` rule 1.
- **`docs/VisualStyle.md`** governs every user interface change here. Read it before you draw
  anything.
- **Entry points only** for try/catch, and log entry, exit and failure on every public method.
- **Settings is not what this is**, but the reasoning behind `CLAUDE.md` rule 8 applies exactly: the
  Cockpit and the phone must not end up with two different stop experiences. Same words, same
  behaviour, different layout.

## Testing

`.\scripts\test-local.ps1` green - and know what it misses. **It runs NO web tests.** This phase is
mostly web, so the Cockpit and mobile test suites are the only coverage your work has, and you must
run them yourself and say so. Run `-Parked` as well if you touch anything the Gateway suite covers.

Component tests for: the reason box refusing to submit empty; the answer being rendered from
`headline` and `details` rather than composed locally; each of the three verdicts rendering; a
failure showing the failure. **Watch each one fail on purpose before you believe it.**

The screenshots are the goal, but they are not yours - a later seat that did not build this drives
the real product for those. Your job is that there is something true to photograph.

## Report to the Architect when this is pushed

One message, one line, pointing at `missions/stop-a-session/phase-b-report.md`. Write into that file
what you built, what you proved and how, what you did NOT prove, and anything that contradicts the
rulings or this note. Fleet messages truncate at the first newline - the detail goes in the file.

---

# ADDED AFTER PHASE A - read this part too

## What Phase A settled that changes your instructions

Read `missions/stop-a-session/architect-ruling-on-phase-a.md` and the end of
`missions/stop-a-session/phase-a-report.md`. Three things bear directly on you:

1. **Ruling 3 now has a FOURTH verdict word**, `stoppedNotDescribed`, for when the owning Director is
   an older version and cannot say what it found. Your surfaces must render it like any other - from
   the Gateway's `headline`, verbatim. Do not special-case it in a view.
2. **The two doors answer differently for an unknown session, deliberately.** `POST .../stop` answers
   200 `notOnFleet`; the legacy `DELETE /sessions/{sid}` keeps its 404. You are moving every client to
   the POST door, so this should not touch you - but if you find yourself relying on a 404 from the
   stop, you are on the wrong door.
3. **The parked suite caught a cross-tenant isolation regression in Phase A that the default gate
   could not see.** Take the warning: a green default run says very little here.

## The de-risking task, and it is as important as the controls

**Nothing has stopped a real session yet.** Every layer of Phase A was proved against a stub of the
one below it. The mission's goal is a QA report showing the feature working, and that report needs a
Gateway carrying this branch - which cannot be the hosted one, because the hosted Gateway deploys only
from `main` and this branch cannot merge until the report exists.

So the stack has to be stood up locally, and **you do it, not the QA seat** - because if it cannot be
stood up, the mission's goal is unreachable and the Architect needs to know that now rather than at
the end.

**What is asked:**

- Stand up a Gateway built from this branch, locally, and a Director in **slot 5 or higher** pointed
  at it. Never the owner's Directors, never the hosted Gateway. `CLAUDE.md` rule 0b is why a test
  Director is launched through the `cc-director-launch` scheduled task; rule 0 is why you never kill a
  process to tidy up.
- Stop ONE real session through it, end to end, and confirm the process is actually gone.
- **Write down exactly how you did it** in `missions/stop-a-session/local-stack-recipe.md`: every
  command, every configuration value, every thing that went wrong and what fixed it. The QA seat
  repeats this recipe independently and photographs it. A recipe that only works when you are driving
  it is not a recipe.
- **If you cannot stand it up, STOP and tell the Architect.** Do not simulate it, do not substitute a
  test that looks like it, and do not quietly narrow the goal. An honest "this cannot be done here,
  and here is how far I got" is worth more than anything you could fake.

**Your smoke test is not the proof.** It is what stops the proof from being impossible. The QA seat
still does the real run, independently, because a builder photographing its own work reaches for the
path it already knows works.
