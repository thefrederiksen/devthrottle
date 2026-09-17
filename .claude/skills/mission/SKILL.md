---
name: mission
description: How a mission is RUN in this repository - the Architect, the Manager, the Workers, and the independent Inspector, plus the four standing laws (ask up front then run alone, and NEVER guess - bring the owner in when something is genuinely undecidable; the mission branch holds the work and only the Architect lands it on main; a different agent family inspects before anything reaches main; merged to origin/main is the only done). Read this BEFORE starting or seeding any mission, and before writing any mission brief. Triggers on "/mission", "start a mission", "run a mission", "seed an architect", "write a mission brief", "mission brief", "who merges", "mission roles".
---

# How a mission runs

A **mission** is a body of work too big for one session and too long for one sitting. This document
is the run-book for how one is RUN. It is not about what a Mission *is* as an object in the product -
that is `docs/new_architecture/fleet.html`, which defines the data model,
the naming, and the attachment rules. Read that for the object. Read this for the conduct.

> **THIS FILE IS THE ONLY PLACE THE RULES LIVE. A mission brief must never restate them, and must
> never GRANT them.**
>
> Before this file existed, every brief wrote the rules out from memory. They drifted - they
> contradicted each other on whether a mission could commit at all - and each one read like it was
> handing out authority. Then one got merged to main, and a sentence granting that mission open
> authority to commit came to rest in a permanent architecture document: imperative voice, addressed
> to the reader, with the words that limited it to one branch and one week sitting in a different
> paragraph. Retrieval does not fetch the other paragraph. That is how a mission-scoped grant becomes
> a standing one that nobody ever decided to give.
>
> *The offending sentence is deliberately paraphrased here and not quoted. Quoting it would leave the
> exact imperative string in this file, searchable, one retrieval away from being read as live - the
> very failure being described. It is in git history on `backup/session-state-truth-2026-07-15` if it
> is ever needed. The same reasoning is why that brief had the text deleted rather than struck
> through: strike-through is a visual convention, and it means nothing to a grep.*
>
> A brief describes the WORK. This file describes the CONDUCT. A brief that grants a permission is
> a bug in the brief.

---

## The house

The analogy is the owner's and it is exact - use it when a question comes up that the rules below do
not cover, and answer the way the house answers.

- **The owner** wants a house. He says what it is for, answers the questions, and is otherwise left
  alone.
- **The Architect** owns the mission. Settles the design, writes the brief, hires the builder,
  decides when it is done, and is the only one who signs anything off into `main`.
- **The Manager** is the builder. Takes the work, gets it done - hiring Workers as needed - and
  **leaves when the work is finished**. A Manager is a delivery vehicle, not a resident.
- **The Workers** are the trades. One task each. They finish and go.
- **The Inspector** is an independent agent, from a **different family** to the Manager, who comes in
  after the builder has left and inspects the work. He does not build. He was not there when it was
  built. That is the entire point of him.

The Architect does not inspect his own building, and the builder does not inspect his own work. The
Architect calls the inspection. Failures go back to the builder to fix, not to the inspector to
patch.

---

## THE FOUR LAWS

### 1. Ask everything UP FRONT, then run alone - and NEVER guess

**Get your questions answered before you start, then run the whole mission without coming back - but
NEVER guess. If something genuinely undecidable turns up mid-run, bring the owner in. Running alone
is not the goal; it is what you get for having asked properly first.**

The owner is not a reviewer and must not be used as one. A mission that stops at every step to ask
"shall I do the next bit?" has moved the work into his lap and made him read a novel to say yes. He
has said so plainly, more than once. If he has to follow along, the mission has failed at its job.

- **Before you start:** think hard about what you genuinely do not know, and ask it all, in one go,
  in plain words. Scope. Product calls. Anything where guessing wrong wastes the run.
- **Then go.** No per-phase approval. No per-pull-request approval. No "just checking".
- **Bother him ONCE at the end**, with the QA report: what changed, what it does for him, what is
  proven, what is not.

**The exception, and it is real: NEVER GUESS.** If something turns up that you genuinely cannot
decide - a product question nobody has answered, a discovery that changes what the mission is for,
a choice with a cost he should carry - bring him in. Say what you found, recommend one option, and
ask. Guessing to preserve the appearance of autonomy is the worse failure by a distance. Autonomy
means "do not ask permission for the work you were sent to do." It has never meant "do not tell him
what you found."

The test: *could I have known to ask this before I started?* If yes, you asked too late. If no,
asking now is correct.

### 2. The mission branch holds the work. Only the Architect lands it.

**A seated mission has exactly one branch, and it is where the work lives. Nothing but the Architect
puts anything on `main`.**

*This section describes how a chartered mission operates. It is not a grant, and it authorises
nothing. If you are not seated on a mission the owner has chartered, with a branch cut for it, none
of this applies to you and the repository's ordinary rule stands unchanged: never commit without
being asked, and having been asked once is not being asked again.*

**Why the branch matters.** This repository has already lost a mission's worth of work to a machine
crash - fourteen commits, complete and passing, stranded on one disk because they were never pushed
anywhere. Work that exists in one place is work you are one crash away from losing. Keeping the
mission branch current and pushed is mission hygiene, in the same way that leaving the site tidy is
part of building, not a favour to anyone.

**The authority chain, stated once:**

- Work accumulates on the mission branch. That is the ordinary operation of a mission and the
  Architect who chartered it has already accounted for it.
- Landing anything on `main` is the irreversible act, and it is the **Architect's alone** - not the
  Manager's, not a Worker's, and not via a merged pull request opened by anyone else.
- This section never reaches outside a seated mission branch. It cannot be read as covering the
  shared checkout, another mission's branch, or `main`.

### 3. A DIFFERENT agent inspects before anything reaches main

**No work reaches `main` un-inspected, and the inspector is not the one who wrote it.**

Use a genuinely different agent family, not another instance of the same one - if the Manager is
Claude Code, the Inspector is Codex. The operational requirement is simply *different family*: an
agent reviewing its own family's work shares too much of its judgement to be a check on it. That is
the whole mechanism, and it works:

> On pull request 1598, an independent Codex inspector found **seven real defects across five
> passes**, and not one was a false alarm. It found that the mission's headline feature **did not
> work at all** - the value was computed correctly and nothing told the screen to re-read it - and
> then found that the *fix for that* was itself half-done, and then that the same fault existed in
> five more places. The author had verified the code, revert-tested the fixes, and watched the tests
> fail on purpose. All of it green. All of it would have shipped.
>
> That is not lore: the review-driven commits on 1598 record it, one per finding, and pull requests
> 1584, 1585, 1588 and 1596 carry the same shape. Check the branch history rather than take the
> number on trust - which is, after all, the point of this law.

- **The Architect calls the inspection**, not the Manager. The builder does not book his own
  inspection, and does not get to mark his own work as passed.
- **The Manager finishes and leaves. Then the inspection happens.** If it finds something, the
  Architect brings the Manager back - or seats a fresh one - and hands it over. The Inspector never
  fixes anything: an inspector who picks up a hammer is no longer an inspector.
- **Tell the inspector to be adversarial, and tell it not to trust the mission's own report.** A
  mission's QA report is self-testimony. It is written by the people who did the work, about their
  own work, and it is exactly as persuasive as it is unreliable.
- **Give it the sharp questions**: what does this claim that the code does not support? Where could
  a constant be substituted and the suite stay green? What is unguarded?
- **Fleet messages truncate at the first newline.** Have the inspector write its review to a FILE and
  reply with one single line. Do not ask for a review in a message; you will get the first heading
  and nothing else.

### 4. Merged to origin/main is the only "done"

Committed is not done. Pushed is not done. An open pull request is not done. The Architect drives
each piece all the way to a merged pull request, deletes the branch, and parks the checkout back on
`main`. A branch that cannot merge today was too big - split it.

---

## Who may interrupt the owner

**Three seats may ask him: the Manager, the Standalone and the Architect. Nobody else. Ever.**

This is not etiquette, and it is not something you opt into. The product enforces it: the Gateway
resolves every session's role across the whole fleet, and a session that is SUPERVISED - a Worker
with a live supervisor, or a run started by a schedule - is parked on every screen the moment it
stops working. It keeps its row in the Director and the Cockpit, it is fully readable, and it is out
of his queue. The wingman does not read it aloud either.

The Architect was in that parked set for three days in September 2026 and the owner took it back out
- "parking the architect seat is wrong. the architect is always the session i talk to". It is the
seat he addresses, so silencing it silenced the conversation.

So the rules below are not asking you to be quiet. You already are. They are about **where your
attention goes instead**, which is the part no machinery can do for you.

**If you are a Worker:**

- You report to the session that started you. Not to the owner, not to the fleet.
- Finish, then say so once, to your supervisor. Do not narrate progress at it - a message
  interrupts the receiving agent, and a worker that reports every step has just made its manager
  read a novel, which is the same failure as bothering the owner one level down.
- Blocked on something you genuinely cannot decide inside your mandate - an ambiguous requirement,
  an irreversible step, a real design fork, an authorisation you do not hold - tell your supervisor
  and say what you need. Do not guess, and do not go around it to the owner.
- You have no channel to the owner. Do not look for one.

**If you are a Manager:**

- You are the seat the owner hears from, and the only reason it is safe to quieten your workers is
  that you consolidate what they found and bring him ONE answer. If you pass their output through
  unmerged, you have not managed anything - you have forwarded your inbox to him.
- Learn what your workers are doing by READING them, not by waiting to be told. A worker that has
  stopped has finished or is stuck; open it and find out which.
- Surface to him on your own judgement - a decision you need, or an update worth having. That is
  what a manager is for and it is never clutter. Once. At the end. With the QA report.

**If you are an Architect:**

- You are the seat the owner talks to. You surface to him like a Manager does: you go red when you
  need him, you count in his needs-you total, and the wingman reads you aloud.
- Surface on your own judgement, and spend it. You hold the design, so the things you bring him are
  the forks only he can settle - not progress reports. One interruption that settles a decision is
  worth more than five that describe one.
- You recommend to the Manager and you keep the design documents true.

**If your supervisor has died** (this is for Workers - an Architect was never quiet, so nothing about
it changes): you are not supervised any more, so you now surface to the owner - correctly, because a
dead supervisor is an exception, and an exception always involves him. Your row carries a sentence
explaining that the session driving you has gone. Do not try to suppress yourself; do not carry on
guessing at the work. Stop where you are and let him decide.

**If you are a scheduled run:** nobody is waiting at a keyboard for you. Do the job, and if it needs
a person, email him - do not sit red on the roster expecting to be noticed. When you have nothing
left to report, reap yourself. This holds WHATEVER SEAT YOU OCCUPY, an Architect included: the
question a schedule answers is "was anyone there when this started?", and it outranks the seat.

## Roles, seats, and hygiene

- **One Architect per mission.** It holds the design, the brief, the merge authority, and the whole
  picture. It does not build.
- **One Manager at a time**, seated per phase. A Manager that has finished its phase is **killed, not
  kept** - a stale Manager burns context and starts inventing work. The Architect kills the Manager;
  the Manager kills its Workers. Never ask a session to kill itself. Never kill uncommitted work -
  make it commit and push first (law 2).
- **Whoever starts a session stops it the moment its work is done.** Not at the end of the mission, not
  when the owner asks - the moment its work is merged or pushed and nothing more is needed from it. The
  Architect stops every Manager, Worker and Inspector it seated itself; the Manager stops every Worker it
  seated. An idle finished session still costs tokens and clutters the owner's roster, and the owner
  restarts sessions all the time precisely to keep that cost down. Stop it yourself with
  `cc-devthrottle session stop <id> --reason "<why>"`. "Never kill a process" is about Director and build
  processes; it never means leaving a finished session running. Before you report, list the sessions and
  check that none you started is still open.
- **Re-seat the Architect too**, at clean boundaries. It is not exempt from context rot.
- **ONE worktree per mission**, cut from `origin/main`, never the shared checkout. Never
  `git checkout -b` in the shared tree - other sessions are working in it.
- **Name sessions `Mission - Role`**: `Session States - Architect`, `Session States - Manager`.
  Mission first, dash, not slash.
- **A message interrupts the receiving agent.** `message send all` reaches your own team. Never
  broadcast to the whole fleet.

---

## Keep the Architect lean. Reset the Manager and Inspector freely.

**The Architect is a coordinator, not a doer. Its context must stay small and must not grow much over
the whole mission.** It holds the design, the brief, the rulings, and the whole picture - and it holds
those in DURABLE FILES (the brief, the mission doc, a short running state note), never only in its own
conversation. Everything the Architect knows can be reconstructed from those files. That is what makes
the next rule safe.

**Reset the Manager at every boundary - it is cheap and it is expected.** A Manager's context grows
fast: it drives Workers, reads their output, argues about proofs. The moment a phase ends, or the
Manager surfaces to report to the Architect, or a back-and-forth has bloated its context, the
Architect **kills that Manager and seats a fresh one**. The Manager is disposable ON PURPOSE. Do not
nurse a large Manager context along to "save" its state - its state does not live in its head, it
lives in the branch and the files.

**How the reset works - the compact handoff:**

- The Architect keeps a short, current handoff note on disk (a few lines: the phase, the branch, what
  is done and pushed, what the next Worker task is, the acceptance row it proves). This note is the
  ONLY thing a fresh Manager needs.
- To reset: make the outgoing Manager commit and push everything first (law 2 - never kill
  uncommitted work), kill it, seat a new Manager named `Mission - Manager`, and hand it ONLY the
  compact note plus a pointer to the mission brief and this workflow. Not the transcript. Not the
  history. Just enough to keep going.
- The Inspector is even more disposable: seated fresh for each inspection, given the diff to inspect
  and the sharp questions, and gone when its written review lands. Never keep an Inspector idle
  between phases.

**Why this is a law and not a preference.** A mission runs for hours or overnight. If the Architect's
context grows with every exchange, it rots and starts contradicting its own earlier rulings; if the
Manager's context grows, it invents work and mis-remembers what was proven. The fix is structural: the
Architect stays small by pushing state to files, and the Manager and Inspector are thrown away and
rebuilt from those files as often as it takes. Killing a Manager mid-mission is not a failure or a
loss - it is the intended maintenance of the machine. A mission that ends with one giant Manager
context that ran the whole thing did it wrong, even if the work shipped.

---

## Writing a mission brief

The brief describes the WORK and nothing else. It does not grant, it does not restate these laws, it
links here.

Every brief needs:

1. **THE WHY, front and centre.** Not the tasks - the reason. What is broken for the owner, in his
   words where you have them, and what is true when this is finished. A mission without a why
   produces work nobody wanted.
2. **The design decisions already made**, as rulings, with a note on which were *inferred* rather
   than stated. Putting words in the owner's mouth is the same defect as putting a cause in a log's
   mouth.
3. **The work**, in the order it lands.
4. **What is explicitly out of scope**, so the Manager does not invent it.
5. **A link to this file** for how to conduct itself.

**Mark the status honestly and keep it current.** A brief that says ACTIVE about a finished mission,
or points at a worktree that no longer exists, is a lie the next agent will believe. When the mission
ends, say so in the document, in the past tense.

---

## Landing the work

**Slice it.** One pull request per coherent piece, in the order the work was built. A 95-file pull
request does not get reviewed; it gets waved through. Six small ones get read.

**A fix and the thing that stops it regressing are ONE unit.** Never land a fix in one slice and its
guard in another - the window between them is exactly where the defect lives, and on one branch that
gap is invisible.

**Prove each fix can fail.** Revert it, watch the test go red *with the reported symptom*, watch the
controls stay green, then restore. A test that has never been watched failing is decoration. In this
repository a green suite has certified a fully broken feature for fourteen months - assume nothing
from green.

**Say what is NOT proven.** Write the gaps into the code as gaps. An unproven claim in a comment is
worse than no comment, because the next reader spends their scepticism somewhere else.

---

## Land the mission's own record

**The paper trail is part of the work. The Architect lands it on `main` the same way and by the same
authority as the code - as the last slice, before the report. A mission that reported but never
committed its record is not finished.**

The record is the brief, the design rulings, the running state note, the handoff notes, the
inspections, the fix reports, and the evidence files the proofs rest on. If a phase produced it and a
later reader would need it to know what happened, it is record.

**Why this is a rule and not a tidiness preference.** Law 2 says work that exists in one place is work
you are one crash away from losing - and then applies it only to code. The record is exempt by
omission, and it is the part this document's own machinery runs on. The Architect stays lean by
holding its state in durable files instead of its context, and a fresh Manager is rebuilt from the
handoff note. Files that were never committed are not durable; they are one disk. The reset strategy
above is worth exactly what the record behind it is worth.

Measured rather than asserted: as of 2026-08-01, `docs/missions/` in the product repository had been
committed three times in its whole history, and every one was somebody sweeping up after missions that
had already reported and gone - most recently 49 files across two missions, in one pull request.
Nothing here made the record anybody's job, so it was nobody's, and it refilled within days.

**Write it inside the mission worktree.** A record written into the shared checkout is stranded by
construction: the worktree goes away when the mission ends, the shared tree was never the mission's to
commit, and the files sit there untracked until an unrelated agent notices months of them at once.
This is ONE worktree per mission, applied to the paper as well as the code.

**A running mission's record is not yours to sweep.** If you are clearing someone else's leftovers,
find out what is still seated first and leave those files alone - they land when that mission reports.
Define what you are touching mechanically, not by eye.

---

## The QA report - the one time you bother the owner

Written for the owner, not for the repository. Plain English, no abbreviations, no process.

Lead with what it does for him. Then: what is proven and how you know; what is NOT proven, named
honestly; what you got wrong along the way; and any decision that is genuinely his.

Do not narrate the run. He does not want to know how the sausage was made - he wants to know whether
he can eat it.
