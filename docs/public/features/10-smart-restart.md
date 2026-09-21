# Smart Restart

> **New in 2.9.0.** If there is no **Smart Restart** on your File menu, your DevThrottle is older than
> 2.9.0; updating is what gets it.

Restarting DevThrottle used to mean losing whatever your sessions were doing. Smart Restart shuts
them down nicely instead: every session is asked to write a short handover of what it was doing and
what is left, and the Director keeps a record of what was running - so when it comes back up it can
offer you those sessions again.

It is worth doing for its own sake. A handover compresses a session down to what is left to do, which
is why restarting once in a while - in the morning, say - leaves your sessions sharper than leaving
them running for days.

## The two doors

**File, Smart Restart** shuts the sessions down and then restarts the Director.

**Closing the main window** - the X, or whatever closes a window on your platform - does the same
thing and then stays closed. DevThrottle opens again when you open it.

Both doors lead to the same dialog. **If no sessions are running you are not asked anything.**
Closing the window simply closes DevThrottle; File, Smart Restart tells you there is nothing to shut
down and leaves the Director where it is.

If your computer is shutting down, there is no time to ask you anything. DevThrottle writes down what
was running - the names, the repositories and the conversations - and lets the sessions end with it.

## What you are asked

The dialog tells you how many sessions are running, and how many of those are working rather than
waiting. Then you choose one of three things.

**Smart Restart** (called **Smart shutdown** when you are closing the window) is the default, and the
one the Enter key takes. Your sessions are shut down nicely. Each one writes a short handover of what
it was doing and what is left. They get the time you allow; whatever is still running after that is
shut down for them.

**Shut down and ignore all sessions** ends everything at once and writes no handovers. If DevThrottle
can reach the Gateway, what was running is written down first, so you can see afterwards what was
closed. If it cannot reach the Gateway, the sessions are ended anyway - you asked for them to be
discarded - but nothing is written down, so there is nothing to look at afterwards, and the message
you get says only how many sessions were ended. Chosen from the File menu, this one does not restart
the Director - it ends the sessions and leaves DevThrottle open and empty, and says so.

**Cancel** closes the dialog and nothing happens.

### The time allowed

A dropdown of **5, 10, 15, 30 or 60 minutes**, and **10 minutes is the default**. It is the time for
the whole shutdown, not per session.

Two thirds of the way through, any session still in the middle of a turn is interrupted and asked to
hand over now, so that it has time to finish writing. At the end of the time, every session still
running is shut down, and the record says which ones those were.

If the Director cannot do a smart shutdown right now it tells you before you confirm, in plain words -
most often because it cannot reach the Gateway, where the record has to be kept. The other two choices
still work.

## Watching it happen

The progress screen takes the place of the session view, whichever door you came in by. It shows one
row per session with what has happened to it so far - asked to hand over, writing its handover, handed
over, interrupted, shut down - a count of how many are done ("4 of 9 shut down"), and how much time is
left. Sessions that report to a lead are listed underneath it.

Two buttons:

- **Shut down now** stops waiting and shuts the rest down straight away.
- **Cancel and keep working** stops the shutdown. Sessions still open are told the restart is off, and
  any session already closed is brought back from the handover it has just written, so you end up with
  the work you started with.

Once every session is gone, File, Smart Restart asks the launcher to restart the Director; the window
close simply closes it.

## Afterwards: picking the work back up

Two things survive the restart.

**The handovers**, one file per session, on this computer under the DevThrottle data folder:

```
<data folder>/vault/handovers/director-restart/<when>-<tag>-<director name>/<short id> - <session name>.md
```

The data folder is `%LOCALAPPDATA%\cc-director` on Windows and `~/.local/share/cc-director` on Linux.
`<tag>` is a short unique mark for that one restart, so two restarts can never land in the same
folder, and `<short id>` is the first few characters of the session's own identifier. A real folder
looks like `2026-09-20T101030-a1b2c3-DevThrottle 1`.
You can read these yourself - each one says what the session was doing and what it would do next.

**The record**, kept on the Gateway rather than on this computer, so it is still there if the machine
is not. It names every session that was running, its repository, its agent, the mission it was on and
where its handover was written.

A session that was still running when the time ran out is written down as exactly that, with its
conversation, so it is clear which sessions never got to finish.

## Coming back up

Nothing comes back on its own. Sessions are brought back because you say so, and you are asked.

When DevThrottle starts again and reaches the Gateway, if a smart shutdown has something to bring back, a
window appears: **A restart is available**. It says when the shutdown was, the reason you gave for it,
and how many sessions are waiting. Under that is a list, with a tick box on each line:

- **A mission's lead, with its sessions underneath it.** Bringing that line back starts the lead first
  and then its sessions, each one reading its own handover.
- **A line of its own for each session that ended without a handover** - it was still running when the
  time ran out, or it never answered. These are not brought back with the rest, because there is no
  handover for them to read. Each one offers instead to **reopen its saved conversation**, or, when its
  agent cannot be started on a saved conversation, to **open a fresh session in its repository** with
  none of the conversation in it. The line tells you which of the two you would get before you press it.

Three answers:

- **Bring back** starts the lines you ticked, and tells you what happened to each one.
- **Not now** writes nothing, and asks you again the next time the Director starts.
- **Don't ask again**, set apart from the other two, stops the offer for that restart. It brings nothing
  back and deletes nothing.

**You are offered a restart once.** As soon as anything has been brought back or reopened from it, it is
not offered again when the Director starts - even if some of its sessions are still waiting. A restart
you answered with **Don't ask again** is not offered again either, and neither is one more than seven
days old.

**Nothing is ever deleted.** **File, Restart history** lists every record this Director has written,
newest first - when, why, and what became of each session - and says why any record is no longer offered
at start-up: used, cleared, or too old. Whatever a record still holds carries the same offer there, so
you can bring it back later in the day, or next week. If the Gateway cannot be
reached, nothing is offered when DevThrottle starts, and the history says plainly that the records could
not be read, rather than showing you an empty list and letting you think there is nothing there.

However a session comes back, it is told that it was stopped and that it must check the state of its
work before acting on anything.

## What it does not do

- It restarts **this** Director, on this computer. It does not reach a Director on another machine.
- No part of it reads or rewrites your sessions' work. Nothing is summarised by a model; the sessions
  write their own handovers in their own words.
- An agent that cannot be interrupted safely is not cut short. It is asked again, and if its turn has
  not ended by the time the clock runs out it is shut down like any other session - without a handover.
- Not every agent can be reopened on the conversation it was having. Where that is not possible, the
  session comes back as a new, empty one in the same repository.
