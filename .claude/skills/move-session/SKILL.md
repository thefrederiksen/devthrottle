---
name: move-session
description: Move a live session to another Director or slot. A move does not migrate a process - it writes a RIGHT-SIZED handover, starts a fresh session on the SAME agent and SAME model, verifies it picked the work up, and then DELETES the original. Triggers on "/move-session", "move session", "migrate session", "transfer session", "move it to the new director".
---

# Move Session

Relocate work from one Director to another - a newer build, a different slot, another machine.

**A move does not migrate a running process. It recreates the session, and then it DELETES THE
ORIGINAL.** The target starts completely fresh: no transcript, no memory, nothing but one document
you write. The source is destroyed at the end - not parked, not left renamed.

**Deleting the source is the point, not a tidy-up.** A move exists so a Director can be emptied and
shut down. A move that leaves the source alive has failed: the Director still has open sessions and
still cannot be updated.

**Verification is the ONLY gate on that deletion.** The source is deleted if and only if the target
has demonstrably picked the work up (step 4). Nothing else gates it - not the owner, not the clock.
If verification fails, nothing is deleted and the source keeps running. This is why the move has to
be good: the handover document is the only thing standing between the work and the bin.

Proven end to end 2026-07-28: a session moved onto a newer Director read its handover, correctly
restated the work and its constraints, ran the exact next action unprompted, and correctly handled
the failure that action turned up.

## Step 0 - Should this be moved AT ALL?

**Ask this before anything else. Most sessions on a Director being emptied should be CLOSED, not
moved.** A move recreates a session and spends a whole fresh context window doing it. That is only
worth paying when there is work that CONTINUES.

Get the handover first (step 2) - it is cheap and you want the record either way - then read its
"exact next action":

- **It names real work** -> move it. Continue to step 1.
- **It says the work is finished, merged, or that there is nothing to do** -> **do not move it.**
  Keep the document and delete the session (step 5). You emptied a slot for free.
- **It says the next action is "ask the owner something"** -> do not move it. Put the question to
  the owner yourself, keep the document, delete the session.

This was learned by getting it wrong: a session was moved whose work was finished and merged, whose
own handover said there was no required next action. A fresh context window was spent recreating a
session with nothing to do. Closing it would have emptied the same slot at no cost.

## When NOT to move

- **Never round-trip.** "Move it to the new Director now and back later" rebuilds the session twice
  and loses a little each time. Move it where you want it and leave it.
- **Never move a session that is working.** Wait for its turn to finish.
- **Every move costs a fresh context window.** Check usage before moving a fleet, and remember that
  step 0 makes some of them free.

## The control surface is the GATEWAY. A Director has no address at all.

**A Director listens on nothing.** There is no port, no `/healthz`, no loopback address and no
`CC_DIRECTOR_API` - the remove-the-network-port mission deleted the listener, and it is not coming
back. Any older instruction that still points at a Director address is describing a product that no
longer exists, and nothing will answer it.

Everything in this document goes through the **`cc-devthrottle` command line**, which is token-free
and address-free and reaches the whole fleet through your own Director's outbound connection to the
Gateway. That is the only door, and it is the same door whether the target Director is on this
machine or another one - which is precisely why the move steps below do not care where the target is.

**Naming a target Director.** Use its name or its identifier, exactly as
`cc-devthrottle director list` prints it. The identifier is what names ONE Director; a port never
could, because every named instance of one install shares an executable, and `--machine` names a
computer rather than an instance on it.

```bash
cc-devthrottle director list           # every Director on the account, with the id --director takes
cc-devthrottle session list --json     # every session the Gateway can see, with its directorId
```

If the Gateway is unreachable, agent tooling does not work and that is the accepted design - there
is no second path to fall back to. Fix the Gateway connection; do not look for a local one.

---

## Step 1 - Capture what the session IS

```bash
cc-devthrottle session list --json
```

Take from the source's record: `sessionId`, `repoPath`, `agent`, `currentModel`, `name`, and its
`directorId`. **The Director knows all of these. Never ask the agent for them** - it can get them
wrong, and a fact you can read is never a fact you request.

## Step 2 - Ask the source for a RIGHT-SIZED handover

Message the source asking for a document at a path you choose. Seven headings:

1. **What I am doing** - the goal in my own words, not a restated task title.
2. **Where I got to** - what is finished and PROVEN, kept separate from what is started or merely
   believed.
3. **The exact next action** - specific enough to act on without asking anyone. Commands and paths,
   not intentions. **If it is blocked on the owner, write the question out verbatim** - a question
   buried in a paragraph is a question nobody ever asks.
4. **Decisions and why** - what a newcomer would otherwise re-litigate or get wrong.
5. **Traps** - dead ends already hit, things that look right and are not.
6. **State** - branch, worktree, uncommitted work, pull requests, issue numbers, running background
   jobs.
7. **What I did NOT verify** - suites never run, platforms never tried, claims taken on trust.

**Heading seven is not optional.** A document listing what passed reads as full coverage and the
reader believes the stronger claim. Learned the hard way: a handover said three test projects
passed - true - while the wider suite had never been built at all, and continuous integration had
in fact failed.

**Facts you inherited are second-hand - label them.** When a Director is emptied one session at a
time, the later handovers repeat things learned from the earlier ones. Those were true WHEN WRITTEN
and nobody has rechecked them since. Separate what you established yourself from what you are
merely passing on, and put the passed-on claims under heading seven. The last handover of the
2026-07-28 emptying did this correctly with three questions for the owner: it carried them out of
five other sessions' documents and said outright that it had not confirmed they were still open. A
second-hand fact restated with first-hand confidence is how a stale belief outlives every session
that could have corrected it.

Instruct explicitly: **no secrets.** The first handover produced this way came back carrying a
virtual machine's administrator password.

### Right-sizing - the part that matters most

**A handover is a briefing, not an archive.** Big enough to continue, and nothing more. Aim for
what the next agent reads in about two minutes - roughly a thousand words. If it is longer than the
reader's first turn, it is too long.

CUT, without mercy:

- **Work finished, merged and closed.** It is in the repository history. It cannot be acted on and
  it cannot go wrong.
- **Superseded decisions.** Only the decision that stands matters; the abandoned ones are noise
  unless one is a trap.
- **Exploration that led nowhere** - unless repeating it would waste the next agent's time, in
  which case it is a trap, not history.
- **Transcripts, tool output, full file lists.** The biggest source of bloat and almost no signal.
- **Anything cheaply re-derived.** Do not paste `git status`; name the branch and worktree and let
  the reader run it.

KEEP, always:

- The live frontier - what is true right now and what happens next.
- Anything that would be got WRONG rather than merely be unknown. A gap costs a question; a wrong
  belief costs a day.
- Pointers, not contents. A path, an issue number, a command.

The test before sending: **for each paragraph, what does the next agent DO differently because it
is there?** No answer means cut it.

### If the source cannot write one

A session that is unresponsive or out of context cannot answer. Fall back to the Gateway's
generated summary: `POST {gateway}/handover` with `fromSessionId` + `toRepoPath` (+ `toDirectorId`
for another Director, `toAgent`, `extraContext` opening with the moved-session statement). It
returns `targetSession.sessionId`, and creates the target **unnamed** - name it immediately.

Know what you are trading: that route produces a MECHANICAL SCRAPE - last prompt, last reply, files
touched, commands, to-do items. It carries no intent, no decisions and no traps. Use it when
nothing better is available, not by preference.

## Step 3 - Start the target on the SAME agent and the SAME model

Sessions are created on whichever Director owns you. To land on a different one, NAME it with
`--director`, taking the identifier or name from `cc-devthrottle director list`. It names ONE
Director, which `--machine` cannot do when a computer runs several named instances:

```bash
cc-devthrottle session spawn "<repoPath>" \
  --director "<target Director id or name>" \
  --standalone \
  --agent <sourceAgent> \
  --name "<the source's ORIGINAL name>" \
  --purpose "continue <work> after being moved" \
  --prompt "<seed - below>"
```

`--standalone` above is not decoration and must not be dropped: it declares that the USER owns the
moved session, which is the whole point of a move. A session you move on his behalf is not yours to
collect - it goes red and asks HIM when it stops, exactly as it did before it moved. Owning it
yourself would quietly take it out of his queue, and the move would have lost him the session.

The target keeps the source's original name. That is what makes the move invisible afterwards. Keep
the naming convention `<repo-dir> - <short description>`; if the source's name lacks the repo
prefix, add it and say so.

**On the model - do not assume, read back.** With `--args` omitted the session inherits the
Director's default model and permission mode. That is usually right and is silently wrong whenever
the source was NOT on the default. Two things to know:

- The reported model (`claude-opus-5`) is **not** the same vocabulary as the `--model` launch flag
  (`opus[1m]`). You cannot feed one into the other; map it deliberately.
- `--args` is passed through LITERALLY and applies NO defaults, so if you set it you must supply the
  permission preset too or the session blocks on a prompt for every action:
  `--args "--dangerously-skip-permissions --model <id>"`.

Then **verify by reading back**, never by trusting the flags you sent. **Read the target's own
terminal header - not the Gateway's `currentModel` field.** That field is populated from the tool's
records and was still empty on a freshly moved target after a completed turn, so a check written
against it either fails or gets skipped as unavailable. The header is authoritative and immediate:

```bash
cc-devthrottle session buffer <targetId> \
  | grep -o "Opus[^·]*\|Sonnet[^·]*\|Haiku[^·]*" | head -1
# -> Opus 5 (1M context)     compare against the source's model
```

`agent` IS reliable on the Gateway record, so compare that from `session list --json`. Treat
`currentModel` as a bonus if it happens to be there.

If they differ, stop. Delete the target and start again. A session continuing on a different model
is not the same session.

Identity is set at spawn or not at all - if the source carried a role, mission, or workflow seat,
pass `--role`, `--mission`, `--workflow-run`.

### The seed prompt

Tell it what it is, point it at the document, make it prove it read it:

> YOU ARE A MOVED SESSION. You are the continuation of a session that ran on another Director. You
> have no transcript and no memory of that work - everything you know is in one document. Read it
> now and treat it as your own history: `<path>`. Read the WHOLE document before doing anything, and
> follow its "exact next action" section - including where it tells you to wait and NOT to act. When
> you have read it, reply with `<the demand - below>`, so the move can be verified - then get
> started on that action and nothing more.

**Make the demand answerable ONLY from the document.** "Summarise what you are working on" is the
weakest possible check: a session can produce something plausible from its own name and repository
alone, and a plausible answer passes a verification you meant to be hard. Name three or four
specific things that exist nowhere but the document - a state, a count, a constraint, the open
questions:

> ...reply with a short summary of what the work was, what state both Directors are in, what the
> next action is, and the three questions waiting on the owner

That version was used for the last move of the 2026-07-28 emptying and it is the one to copy. It
cannot be bluffed, and writing it forces YOU to check that the document actually answers it - which
is a review of the handover you just commissioned.

**If the owner is at the keyboard, say so in the seed.** A moved session reads "reply so the move
can be verified" as an instruction to report and then stop. That is right when the move is
unattended and wrong when the owner is sitting there mid-conversation, waiting - it leaves them
looking at a session that has gone quiet on them. When the owner is present, add: *the owner is at
the keyboard and mid-conversation, so pick the thread up with them directly rather than waiting to
be addressed.*

## Step 4 - Verify the pickup BEFORE touching the source

**Always take the TAIL of the buffer, never the whole thing.** A single session's scrollback came
back at 1.3 MB and had to be spilled to a file. You want the last few turns:

```bash
cc-devthrottle session buffer <targetId> \
  | tr -s ' ' | tail -30
```

(`tr -s ' '` collapses the terminal's padding, which otherwise makes the output unreadable.)

It **passes** when the target states the work, its constraints and the next action **from the
document**, then does that action. It **fails** when it guesses, asks what it should be doing, or
starts something the document assigned to someone else.

**If it fails, stop. Leave the source alone and fix the document.** The source is still alive and
still holds everything - this is the last moment at which that is true.

Parked-composer gotcha: state stuck `WaitingForInput` with the seed visible at the prompt means the
submit never fired. Send a raw Enter through the Gateway -
`POST {gateway}/sessions/{tid}/prompt` `{"text":"\r","appendEnter":false}` - then re-verify. (Empty
text with `appendEnter:true` is a 400.)

## Step 5 - Delete the source

Step 4 passed, so the work now lives on the target. Destroy the original. **Do not ask - the owner
asked for the move, and a move includes this.** Asking here is how a Director ends up full of parked
`[MOVED]` sessions and can never be shut down.

**First, relay anything that happened AFTER the document was written.** The source goes on living
between writing its handover and being closed - it answers one more question, the last other
session leaves, the owner changes what he wants next. The document is a snapshot and cannot contain
any of it, and the source is about to be destroyed, so this is the final moment those facts exist
anywhere. Send them to the target as a closing message and mark them plainly as not being in the
document:

```bash
cc-devthrottle message send <targetId> \
  "Move verified - you are the continuation and I am closing now. Two things not in the document
   because they happened after I wrote it: <late fact>, <late fact>."
```

The last move of the 2026-07-28 emptying carried two such facts this way - that the source had been
asked to move ITSELF, so the old Director was now genuinely empty and the update was the owner's
immediate next request, and that the installed copy of this very skill was stale and only
`origin/main` should be followed. Neither was in the handover. Both changed what the target did
next.

```bash
cc-devthrottle session rename <sourceId> "[MOVED -> <where>] <original name>"
cc-devthrottle message send   <sourceId> "Moved and verified. Do nothing further; you are being closed."
cc-devthrottle session done   <sourceId>     # FLAGS it; the Director reaps it later
```

The rename is for the time it remains visible, so anyone watching sees why it vanished.

**`session done` is a flag, not a delete, and the reap is asynchronous.** The session stays in the
fleet list - state `Running`, nothing obviously pending - for a while afterwards. **Poll until it is
actually absent; do not check once and theorise.** Getting this wrong once already produced a
confident wrong diagnosis (an invented rule that a hold was required first; the session had simply
not been reaped yet):

```bash
i=0; until ! cc-devthrottle session list --json | grep -q "<sourceId>" || [ $i -ge 24 ]; do
  sleep 10; i=$((i+1)); done
cc-devthrottle session list --json | grep -q "<sourceId>" \
  && echo "STILL PRESENT after 4 min - investigate" || echo "DELETED"
```

Only report the move complete once that says DELETED. If it never goes, say so plainly rather than
inventing a mechanism to explain it.

**If step 4 did NOT pass, delete nothing.** Leave the source running and untouched, delete the
failed TARGET instead, and fix the handover document. A source deleted behind a target that cannot
continue is the one unrecoverable outcome this skill exists to prevent.

## Step 6 - Report

Give the owner: the target's id, Director, repository, retained name, and the proof it picked up;
confirmation that the source is deleted and the old Director's session count is one lower; and
anything the document admitted it had not verified.

When the point of the move was to empty a Director, say how many sessions it has left.

**Collect the owner-blocked questions from every handover and put them as ONE list.** Emptying a
Director scatters each session's blockers across however many new sessions you created, and a
question sitting inside a moved session is a question nobody asks. The 2026-07-28 emptying produced
three - a tag that was never pushed so a release never built, an unanswered design question about
whether library skills are flat or a folder tree, and a browser sign-in only a human can do - and
they only reached the owner because the last handover gathered them up. Ask them together, in prose,
and say which ones you have confirmed are still open.

---

## Moving the LAST session - the self-move

The session that empties a Director is itself running on it, so the final move is the mover moving
itself. It works, and it was how the 2026-07-28 emptying finished, but three things change:

- **You write your own handover.** You are both the best-informed source there will ever be and the
  one that cannot be asked follow-up questions later. Apply the right-sizing rules to yourself
  harder than you would to anyone else.
- **You cannot verify the pickup by watching, so make the target verify itself TO you.** The seed's
  reply comes back to the owner, not to you. Use the un-bluffable demand above and read the target's
  answer before you flag yourself done - a self-move with no gate is just a session deleting itself
  and hoping.
- **You send your own closing message and then flag yourself.** Relay the late facts to the target
  first (step 5), because after `session done` there is nobody left who knows them.

**Do not then update the Director unprompted.** Emptying it makes the update possible; whether to
run it is the owner's call, and he is the one who will be looking at the first-run wizard if it
lands in a strange data home.

---

## Traps

- **The source cannot tell you what it never checked** unless you ask. That is heading seven.
- **Background jobs do not survive.** A monitor watching a build dies with the source. The document
  must say so and give the command to re-check by hand.
- **Scratchpad files do not survive.** Screenshots and working notes in the source's temporary
  directory are gone. Name how to regenerate them, or move them somewhere durable first.
- **`session hold` queues mid-turn.** "still working; it parks when it finishes" is success.
- **Do not start a Director from your own process.** Ask the Gateway to have the machine's launcher
  start it (`POST /machines/{machine}/director/start` - the launcher has no port or token of its own
  any more; the command rides the connection the launcher holds open to the Gateway) - it gives clean
  parentage. A Director started inside an agent's console hosts sessions that die within seconds.
- **The `[MOVED]` marker must never lie.** If you marked the source and the move actually failed,
  rename it back and say so plainly.
- **A parked source is a failed move.** If you find yourself leaving the source alive "to be safe",
  you have not moved anything - you have duplicated it, and the Director you were emptying is still
  full. Either verification passed, in which case delete, or it did not, in which case fix the
  document and try again.
- **A slow result is not a broken one.** When something does not happen as fast as you expected -
  a reap, a model field, a state change - poll it before you explain it. An invented mechanism that
  fits one observation is worse than saying "it took longer than I thought".

## Note for the future

Written to be lifted into the central Gateway skill library. Keep it self-contained and free of
machine-specific paths so it can be served unchanged to every agent.

---

**Skill version:** 4.2 · **Updated:** 2026-07-28
**Changes in 4.2** (from emptying an entire Director - seven sessions, finishing with a self-move.
Several of these were learned from the TARGET's side of a move, which no earlier version could see):
(1) **The verification demand must be un-bluffable.** "Summarise what you are working on" can be
answered plausibly from the session name and repository alone, so it passes a check meant to be
hard. Name specific facts that exist nowhere but the document.
(2) **Say in the seed when the owner is at the keyboard.** Otherwise the target reads the seed as
report-then-stop and goes quiet on an owner who is sitting there waiting, mid-conversation.
(3) **Relay late facts to the target before closing the source.** The handover is a snapshot and the
source goes on living after writing it; two facts that changed what the target did next existed
nowhere in the document.
(4) **Label inherited facts as second-hand.** When a Director is emptied one session at a time, the
later handovers repeat earlier ones' claims that nobody has rechecked.
(5) **Write owner-blocked questions out verbatim** in the next-action heading, and **roll them up
into one list** in the final report - otherwise they scatter across the sessions you created.
(6) **The self-move** - how the last session on a Director moves itself, why the target must verify
itself to you rather than the other way round, and why it must not then update the Director
unprompted.
**Changes in 4.1** (four fixes from the second real move):
(1) **Step 0 - should this be moved at all?** A session whose work is finished should be CLOSED, not
recreated. Found by moving one whose own handover said there was no next action, spending a fresh
context window on a session with nothing to do. Closing empties the same slot for free.
(2) **The model check now reads the session's terminal header**, because the Gateway's
`currentModel` was still empty on a moved target after a completed turn - the check as written in
4.0 could not be satisfied. `agent` remains reliable from the Gateway record.
(3) **Deletion is verified by polling until the session is absent.** `session done` is a flag and
the reap is slow; checking once produced a confident wrong diagnosis (an invented rule that a hold
was required first).
(4) **Read the TAIL of the buffer.** One session's scrollback was 1.3 MB.
**Changes in 4.0:** A move now DELETES the source automatically, gated on verification and nothing
else. This reverses 3.1's "never kill the source - the user closes it", which defeated the purpose:
the reason to move sessions is to empty a Director so it can be shut down and updated, and a parked
`[MOVED]` session leaves it just as full. Verification is the only gate, which is what makes the
handover document critical - it is the only thing between the work and the bin.
The default route is now an AGENT-WRITTEN, right-sized handover with seven headings, proven in a
real move; the Gateway `POST /handover` auto-summary is demoted to a fallback for a source that
cannot answer, with its mechanical-scrape limitation stated. Added: the right-sizing rules and the
per-paragraph test; heading seven (what was not verified) after a real document read as full
coverage while the wider suite had never run; a no-secrets instruction after one came back carrying
a password; same-agent and same-model spawn with a READ-BACK check, and the warning that the
reported model id is not the `--model` flag vocabulary; creating the target on a chosen Director
with `--director`; and the launcher launch route.
Kept from 3.1: the Gateway-is-the-control-surface preamble, the naming convention, and never
touching the source until the target has demonstrably picked up.
