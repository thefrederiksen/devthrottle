# Who built this - the seats the mission ran through

**The owner asked for this, on 9 September 2026, while Phase C was running:** the QA report is to
show the sessions that were involved in building, fixing and implementing this feature, and how many
there were. This file is the establishable answer, written by the Phase C Manager so the QA seat can
render it rather than reconstruct it.

**A note on the words, because they matter here.** A **session** is one running coding agent. An
**agent** is the tool that session runs. A **mission** is why the work exists and who is on it
together. So the count below is a count of SESSIONS, and the agent column says which FAMILY of tool
each one ran. Calling a session an agent is what makes a roster like this unreadable a month later.

**Why no product names appear below.** This repository is public, and it carries a standing rule that
no assistant, vendor or model is named anywhere in it. The load-bearing fact here is not which brands
were used - it is that the seat which INSPECTED the work came from a different family than the seats
that WROTE it, and found eight defects they had all reported green. That survives without the names.
The owner knows which tools these were and can say so anywhere he chooses; this file does not put
them in a public repository to make its own point.

---

## The count

**Fifteen sessions**, across six phases, running two agent families.

| Agent family | Sessions | Which |
|---|---|---|
| The building family | 14 | Every builder, manager, architect and the quality seat |
| A different family | 1 | The Inspector, deliberately - see below |

The single seat from the other family is not an accident of availability. Law 3 of this fleet's mission workflow
requires that the seat which inspects the work comes from a **different agent family** to the seats
that wrote it, because an agent reviewing its own family's work shares too much of its judgement to
be a check on it. That one session found eight real defects, four of them severity one, in a branch
whose builders had all reported green.

---

## The roster, in the order the seats existed

| # | Phase | Seat | Session | Agent | What it did |
|---|---|---|---|---|---|
| 1 | Design | Architect | `e66d53fb` | The building family | Owns the mission. Settled Rulings 1, 3, 5 and 6 in writing before any code was written, corrected two factual errors in the mission document, ruled on every phase report and on the inspection, and is the only seat that may land anything on `main`. Live throughout; it does not build. |
| 2 | A | Manager | `ee59e5d0` | The building family | Drove Seats 1 and 2 - the Director's honest answer and the command line. Caught its own two brief defects mid-flight, and demanded the parked suite run that exposed a cross-tenant isolation regression. |
| 3 | A | Worker A | not recorded | The building family | Rewrote `SessionCommandExecutor.KillAsync` to answer a structured verdict instead of two booleans. |
| 4 | A | Worker B | not recorded | The building family | Built `POST /sessions/{sid}/stop`, the fold that writes every sentence, the refusal, the allow-list entries and the audit record. |
| 5 | A | Worker C | not recorded | The building family | Built `cc-devthrottle session stop` and `session done --undo`. |
| 6 | B | Manager | `578d8e29` | The building family | Drove Seat 3 - the controls - and stood up a local Gateway and Director to close the end-to-end gap: a real session, a real process, really stopped. |
| 7 | B | Worker D | not recorded | The building family | The shared stop function, the Cockpit control and the phone control. |
| 8 | B | Worker E | not recorded | The building family | The Director window's close, re-pointed at the stop route with a dialog that says what happened. |
| 9 | Inspection | Inspector | not recorded | **A different family** | Read the whole 62-file branch diff adversarially, reproduced rather than asserted, and found eight defects. Never fixed anything - an inspector who picks up a hammer stops being one. |
| 10 | C | Manager | `e7ea69df` | The building family | Rebased the stale branch onto fresh `origin/main`, split the eight findings across four Workers on disjoint files, and verified their proofs rather than accepting them. |
| 11 | C | Worker F | `4c74ebdf` | The building family | Findings I2 and I3 - liveness that cannot be read is neither alive nor gone, and a backend with no process identifier is not an absent process. |
| 12 | C | Worker G | `53e4e96f` | The building family | Finding I1 - a completed stop that the caller hung up on must still leave an audit row. |
| 13 | C | Worker H | `a881e597` | The building family | Findings I4, I7 and I8 - the answer a two-second poll deleted, the busy guard that was only on the button, and the phone failure rendered outside its own modal. |
| 14 | C | Worker I | `6529c15f` | The building family | Findings I5 and I6 - clients that turned an unknown outcome into a definite one, and a session name with a slash in it that stopped routing. |
| 15 | QA | Quality seat | `9997554e` | The building family | Did not build the feature. Repeats the local stack recipe independently and writes the report this mission exists to produce. |

**The owner is not on this list and should not be.** He is not a session. He settled Rulings 2 and 4 -
whether a stop may destroy uncommitted work, and who is allowed to stop whom - on 8 September 2026,
and was then left alone, which is what the workflow's first law is for.

### How the seats relate

    Owner - two rulings, then left alone
      |
      Architect  e66d53fb  ......... holds the design, lands the work, never builds
        |
        +-- Phase A Manager  ee59e5d0
        |     +-- Worker A, Worker B, Worker C
        |
        +-- Phase B Manager  578d8e29
        |     +-- Worker D, Worker E
        |
        +-- Inspector ............... a different family, on purpose
        |
        +-- Phase C Manager  e7ea69df
        |     +-- Worker F, Worker G, Worker H, Worker I
        |
        +-- QA seat  9997554e ....... did not build it, so it can photograph it

Six of those fifteen were Workers on a single task each; five have been or will be flagged for
deletion the moment their work was verified, which is why a Manager is a delivery vehicle and not a
resident.

---

## What could NOT be established, and it is a finding rather than a shrug

**Six of the fifteen sessions cannot be named by their identifier.** Workers A, B, C, D and E and the
Inspector all finished, reported, and were flagged for deletion. Their work is fully recorded -
each has a brief and a notes file in this folder - but none of those files records the identifier of
the session that wrote it, and once a session is reaped it leaves the fleet list.

Two things were tried and neither closed it:

- **The Director's own logs.** 337 log files on this machine, and not one carries the session names
  for this mission. They cannot answer it.
- **The Gateway's session history store.** It exists, it keeps records of ended sessions, and
  `GET /history/sessions` serves them. **A session's own key is refused on that route - 403.** So a
  session cannot enumerate the seats that worked on its own mission, which is precisely the question
  the owner asked.

**Two things follow, and both are cheap.**

1. **A seat writes its own session identifier at the top of its notes file.** Every notes file in
   this folder opens with what the seat built; none opens with who it was. One line, written by the
   seat that already knows the answer, and this whole reconstruction becomes a read. The Phase C
   Workers have been told to do it, so entries 11 to 14 above will be verifiable from the record
   rather than from a live fleet list that empties.
2. **Whether an agent's key should be able to read session history is a real question, and it is
   adjacent to this mission rather than inside it.** Ruling 4 opened the allow list for the stop
   because the owner judged the flexibility worth it and the trail sufficient. Reading the history of
   sessions that have ended is a different and much safer act than ending a live one. It is not this
   mission's to decide and it is not being decided here - it is recorded so that somebody decides it
   on purpose rather than discovering it the next time a report needs a roster.

Everything else in the table above is established from the mission's committed record: the phase
reports, the briefs, the notes files, the Architect's state note, and the live fleet list for the
seats that are still seated.

---

## The sentence the report should carry, and it is not a joke

**This feature writes an audit row naming who stopped a session and why. The polite path this fleet
has used for years - the deletion flag and the reaper behind it - writes nothing. That is precisely
why six of the fifteen seats that built this feature cannot now be named.**

The gap this mission was raised to fix is the same gap that ate its own paper trail. It belongs in the
report as one honest sentence, because it is the truest thing anyone found about why the mission
exists: an ending that reports nothing is not a smaller version of an ending that reports something,
it is a hole, and holes are only visible afterwards when somebody needs what fell through.

**Filed as issue #2781** - "The fleet cannot say afterwards which sessions did a piece of work" -
which records what was established, what was not, and why the obvious remedy (letting a session key
read the session-history route) is an allow-list change that this mission does not get to make on its
own. The Architect ruled: record and file, do not fix.

## The one-line fix, already working

The QA seat `9997554e` was reaped shortly after this file was written - it had repeated the local
stack recipe and was stopped before capturing any frames, because the inspection's defects reach the
frames it would have taken. **Its identifier is in the table above anyway**, written down while it was
still seated. That is the whole of the remedy, demonstrated: a seat recorded at the time is a seat
that can still be named after it is gone. The next QA seat is a new session and gets its own row.

The Phase C Workers have been told to open their notes with their own identifier, so entries 11 to 14
will be verifiable from the committed record rather than from a live fleet list that empties.
