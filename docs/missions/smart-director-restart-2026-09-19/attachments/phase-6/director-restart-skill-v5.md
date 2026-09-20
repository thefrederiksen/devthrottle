# Restart a Director

**The Director does this itself now. File, Smart Restart.**

The hand-run this skill used to teach - an agent on another Director messaging every session for a
handover, writing an index, closing seats one at a time - is retired. It is not a longer path to the
same place; it is the wrong one. It also stopped being runnable at all on 17 September 2026, when a
session became able to message only the session that started it: a driver cannot ask sessions it did
not start for anything, and the old skill said so itself before this rewrite.

**Do not improvise a drain.** If the Director in front of you cannot do a smart restart, the answer
is to update that Director, or to accept that its sessions are lost - never to rebuild the hand-run
from memory.

---

## Which Directors have it

The feature is merged on `main` in `thefrederiksen/devthrottle` and is **in no release yet**. Checked
on 20 September 2026: the newest tag is `v2.8.1`, which does not contain the merge commits, and every
live Director reported 2.8.1 or 2.7.0.

So check before you promise it:

```bash
cc-devthrottle director list --fields id,name,machine,version,state
```

**If the File menu has no "Smart Restart", that Director is on a build without the feature.** Older
builds carry an item called "Drain this Director for restart...", whose window threw on opening and
never worked in a shipped build. It is gone from `main`.

## What the feature does

Two doors, one dialog: **File, Smart Restart**, and **closing the main window** on any platform. With
no sessions running nothing is asked: the window close simply closes the Director, and File, Smart
Restart says there is nothing to shut down and does nothing - it does not restart an empty Director.

The dialog says how many sessions are running and how many are working against waiting, and offers:

- **Smart Restart / Smart shutdown** - the default, and what Enter takes. Every session is asked to
  write a short handover. The time allowed is a dropdown of 5, 10, 15, 30 and 60 minutes; ten is the
  default.
- **Shut down and ignore all sessions** - the record is written first, then everything is ended at
  once, with no handovers. From the File menu this does NOT restart the Director, and it says so.
- **Cancel** - nothing happens.

The confirm button stays dead until the Director has answered whether it can do it, and every refusal
is shown in the Director's own words: the Gateway unreachable refuses a smart shutdown, because the
record has to live off this machine, and a development slot refuses a restart.

Then the progress screen replaces the session view: one row per session with its state, a count
("4 of 9 shut down"), the time left, and two buttons.

- At **two thirds of the time allowed**, sessions still mid-turn are interrupted and asked to hand
  over now.
- At the **limit**, every session still present is ended and recorded as ended when time was up, with
  its conversation id. **This replaces the old never-force rule.** The feature does force, at the end,
  deliberately.
- **Shut down now** jumps to the limit. **Cancel and keep working** stops the closing, tells the
  sessions still open that the restart is off, and brings every already-closed session back through
  the restore - against the same Director, from the handover it has just written.

From File, Smart Restart the Director asks its own launcher to restart it once it is empty. From the
window close it closes and stays closed. When the operating system is shutting down there is no ten
minutes and no dialog: the Director writes the record - names, repositories, conversation ids - and
lets the sessions end.

Handovers are written under the data root, one directory per run:
`<data root>/vault/handovers/director-restart/<timestamp>-<mark>-<director name>/<short id> - <name>.md`.
The record itself is a workspace on the Gateway, with the id `restart-<yyyyMMdd-HHmm>-<director slug>`.

---

## What a person still does by hand

### 1. Bring the sessions back. Nothing offers them yet

The engine that decides what may be offered is merged - which record belongs to this Director, one
row per mission head, what a seat that ended without a handover may be offered - **but nothing calls
it**. There is no "A restart is available" screen, no restart history window and no start-up check on
`main` today. After a smart restart the sessions stay down until somebody asks for them.

Ask with the restore that already exists:

```bash
curl -s -H "Authorization: Bearer $CC_GATEWAY_SESSION_KEY" "$CC_GATEWAY_URL/gateway/workspaces"
cc-devthrottle director list                      # the NEW id: a restarted Director gets one
cc-devthrottle director restore "<workspace id>" --director "<the new director id>"
```

Read the exit code: 0 means every seat asked for came back; 1 means a seat failed or the wait ran out;
3 means you passed `--wait-seconds 0`, so nothing is known to have come back. A seat whose start may
already have landed is never started twice - look for it in `cc-devthrottle session list` by name
before reaching for `--force-seat`.

**Restore the head of a mission, not its tree.** A lead re-seats its own workers from the briefs on
its branch. `--seat <captured session id>` takes them one at a time.

### 2. Another machine's Director

Smart Restart is a button on that Director's own window, so it restarts the machine you are sitting
at. There is no remote smart restart, and no command line door either: `cc-devthrottle director
smart-restart` does not exist.

A remote restart is still the plain restart, which destroys every session on that Director. An agent
may not deliver one - that route is the admission surface and a session key is refused there - but it
may ask whether the machine could take one, and it may ask the owner for one:

```bash
cc-devthrottle machine restart-capability <machine>              # changes nothing
cc-devthrottle machine restart-request <machine> --reason "<why>"
cc-devthrottle machine restart-request-status <machine> <request-id>
```

The request is a record the owner accepts once. Do not drain a machine by hand because it is far
away: ask for the restart, say plainly that its sessions will be lost, and stop there.

### 3. The agents the feature cannot fully serve

- **Codex has no conversation resume.** Its driver ignores the id, so a Codex session ended at the
  limit comes back blank, in its own repository, with nothing of its conversation.
- **Pi has no safe interrupt.** A Pi session mid-turn at two thirds is asked again but not cut short;
  if its turn does not end in time it is ended at the limit with no handover. If that session's
  document matters, stop its turn yourself with Escape before starting the smart shutdown. Measured
  on the rig, a Pi session stopped that way wrote its handover in under seven seconds.

### 4. Sessions held by an open question box

The dialog has an "Answer these first?" section listing sessions with a question box open for the
owner - but the property it reads is not populated by the product today, so on a real Director that
section does not appear. Look over the fleet yourself before starting: a session waiting on an answer
cannot hand over while the box holds it.

### 5. Watch the first real one

Nothing in this feature has been run against a real Director, a real Gateway or a real launcher. Every
proof behind it is unit tests and an isolated rig. Two things to expect the first time: the Gateway
has to carry the new record marks, because a Gateway that does not know a drain state refuses to store
it; and a session that appears after the record is written is ended with no trace of it in the record.

### 6. Anything that needs judgment

No model is involved anywhere in this feature, and none is planned for version one. A handover is
accepted on its size and its closing block, never on whether it is any good. When a restart has to go
well - a mission at a delicate point, a session you know writes thin documents - ask that session for
its handover yourself, read it, and then start the smart restart.

---

## Traps that are still true

- **A restarted Director has a NEW identifier.** Every restore naming the old one is wrong.
- **A launcher that restarts may update itself seconds later.** Read its version once it has settled.
- **Background jobs and scratchpad files do not survive.** A monitor watching a build dies with its
  session; screenshots in a temporary directory are gone.
- **`session done` is a flag, not a delete.** The session stays listed for a while afterwards.
- **Never start `cc-launcher` by hand from an agent's shell.** It inherits `CC_DIRECTOR_ROOT` and
  registers against a Director's instance home, where nothing else can see it.
- **Never start a Director from an agent's own process.** The sessions it hosts die within seconds.
