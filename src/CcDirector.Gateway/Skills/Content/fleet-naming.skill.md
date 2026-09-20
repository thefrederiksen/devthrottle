# Name a session, and put it in a Mission

Two rules. They take ten seconds each at spawn time and cannot be recovered cheaply afterwards.

1. **Every session belongs to a Mission.**
2. **Every session is named `<Mission short name> - <Role> - <what this seat does>`.**

## Why this is not tidiness

The fleet map groups by Mission. **A session with no Mission is invisible there** - it shows up in a
flat list of names with no relation to anything, and the owner cannot see what is being worked on or
who is working on it together. On 2026-09-06 the owner opened the map and found twenty-two live
sessions and not one of them attached to a Mission, including a five-seat epic that had been created
that afternoon. The work was fine. The picture of the work did not exist.

Names decay the same way. In the same twenty-two there were seats called `customer acquisition`
(which had not been about customer acquisition for hours), `devthrottle_internal - billing` (named
after a repository, which every session has) and `New Studio Cube - Architect 2` (there was no
Architect 1). A name that has to be investigated is not a name.

## The convention

```
<Mission short name> - <Role> - <what this seat does>
```

- **Mission short name** is a stable handle, two or three words, the same for every seat on that
  Mission: `Linux Support`, `New Studio Cube`, `Restart Director`. It is NOT the Mission's full name -
  the map shows that beside it - and it is never the repository.
- **Role** is exactly one of `Architect`, `Manager`, `Worker`, matching the session's actual role.
  Set it with `--role` at spawn, or `cc-devthrottle session role <id> <Role>` afterwards.
- **What this seat does** is the part a person reads: `phase 2 restart only if empty`, `packaging`,
  `R-D A`. Plain words, lower case, no issue numbers.

A Mission with ONE seat drops the role, because there is nothing to distinguish it from:
`Billing - prove the paid checkout`.

Good:

```
Restart Director - Architect
Restart Director - Manager
Restart Director - Worker - phase 3 workspace object
Linux Support - Worker - packaging
New Studio Cube - Manager - R-D
```

Wrong, and why:

| Name | What is wrong |
|---|---|
| `devthrottle_internal - billing` | named after the repository, which tells you nothing - every session has one |
| `New Studio Cube - Architect 2` | a number that refers to nothing; there was no Architect 1 |
| `Restart epic - Phase 2 - restart only if empty` | a different prefix from its own siblings, and the role is missing |
| `customer acquisition` | true when it was created, false two hours later |
| `Worker` | no mission, no subject |

## Name the Mission so a stranger understands it

The Mission name says what is being achieved, not which component is being edited:

- `Restart a Director without losing the fleet`
- `Linux support - DevThrottle runs on Linux`
- `Billing - prove a real customer can buy Pro`

**Set the `why` in the same breath as creating it.** `cc-devthrottle mission create` takes only a
name, which is why nearly every Mission in `mission list` reads "no why set". There is no command for
it; PATCH the Gateway directly:

```python
# from %LOCALAPPDATA%/cc-director/pyenv/Lib/site-packages
from cc_devthrottle.mission_ops import MissionClient
MissionClient()._request('PATCH', '/missions/<mission-id>', {'why': '<why this work exists>'})
```

A Mission without its reason written down produces work nobody wanted, and the tool makes the reason
the easiest thing to skip.

## Do it at spawn, not afterwards

```bash
cc-devthrottle session spawn "<repo>" \
  --mission <mission-id> \
  --controlled-by self \   # you own it, so its report lands in YOUR inbox when it finishes
  --role Worker \
  --name "<Mission short name> - Worker - <what it does>" \
  --prompt "Read <path to its mandate file> - it is your whole mandate. Follow it."
```

**Confirmed on Director 2.0.6: attaching a session to a Mission no longer breaks its ability to
spawn.** A default spawn from an attached session works and correctly inherits the Mission. The old
workaround - spawn `--standalone`, then attach - is obsolete, and using it is now the main way seats
end up unattached. If you are on an older Director, re-test before assuming either way.

To fix what already exists:

```bash
cc-devthrottle mission attach <sessionId> <missionId>
cc-devthrottle session rename <sessionId> "<the right name>"
cc-devthrottle session role   <sessionId> <Architect|Manager|Worker>
```

`mission attach` is not visible immediately - a `session list` read seconds later can still show
`mission=None` and settle shortly after. An immediate check is a false negative; wait and re-read.

## Scheduled runs are the known hole

A scheduled job creates its own session and nothing attaches it, so `Daily Error Triage - <date>`
and its siblings arrive with no Mission and a date stuck on the end of the name. They are transient
and reap themselves, so do not hand-attach them one by one - that is work the next run undoes.

The fix belongs to whoever owns the schedule: give the schedule a Mission and let every run of it
attach there, so the recurring work has one home and its history is in one place. Until that exists,
scheduled sessions are the ONE exception to the first rule, and they are an exception because of a
gap, not because they are special.

## When your turn ends, REPORT TO WHOEVER OWNS YOU

A session that answers to another session is quiet on the owner's roster - it does not go red and it
does not reach him. That quiet is only SAFE because you report. Without it, quiet means lost: you
finish, you sit grey, and nobody is ever told.

So the last thing you do in a turn is hand your work back:

```bash
cc-devthrottle session report "<what you did, and anything they must decide>"
```

**This is part of doing the work, not a courtesy.** Your parent asked you for something; getting back
to them is the last step of it. Say what you did in one or two sentences, and say plainly anything
they have to decide. A report with no words is the 'notice me' ping this fleet rejects - they would
have to open you to find out what happened, which is the work the report exists to save.

**It is queued for them, and it keeps ringing until they read it.** Since 16 September 2026 no
session types into another (the owner reversed his 13 September ruling that a report interrupts): the
report is a record in your parent's inbox, and when your parent is not working, one doorbell line tells
it to run `cc-devthrottle message inbox`. Unread, it is rung again, then marked stuck and you are told.
Do not skip it because they "will see the roster" - they will not, and that is the whole reason this
exists.

**If nothing owns you, it sends nothing and says so.** No parent means the USER owns you, and you are
already red and in his queue the moment you stop - that red IS your report. Leave your answer in your
own session where he will read it. Running the command is still the right move: it tells you which of
the two you are, from the same fact the roster uses.

## If you seat other sessions, you enforce this

An Architect or a Manager that seats Workers is the only thing standing between the fleet map and a
flat list of names. Put the convention in the mandate you hand each seat, with the Mission id in it,
so a seat that seats further seats carries it too.

**When you rename a seat, fix every document that named it.** A mandate that names its siblings by
their old names sends the next session looking for something that does not exist. Rename, patch the
documents, and tell any running session whose mandate you changed to re-read it.
