# Phase 4 proof - Smart Director Restart

What phase 4 owes the Delivery Lead as a whole: what was built, the checks and their counts, the
reviews and what became of every finding, and what phase 4 could not reach.

Written on 20 September 2026 by the Developer seat that answered the command line review, standing in
for the two Developers that built the halves; both are gone and cannot be woken. It is assembled from
the record on `main` - the mandates, the two proofs, the two reviews and the two answer files beside
this one - and from the runs this seat made itself. Where a number came from somebody else's run it
says so.

Phase 4 was the one phase of this mission with two independent halves, no Tech Lead, and one Delivery
Lead over both. Mission document section 6: "The command line door, and dev report inheritance in the
dev-reports service." Both are now merged to `main`.

## The two halves, and where each landed

| Half | Mission item | Merged as | Its own proof |
|---|---|---|---|
| Restored sessions inherit dev reports | 5.3 item 13 | pull request 3199, merge commit `f0b98cd7d` | `proof-phase-4-dev-reports.md` |
| The command line door | 5.3 item 12 | pull request 3237, merge commit `59dcf76f6` | `proof-phase-4-command-line.md`, plus `review-phase-4-2-answers.md` |

They shared no file and were built in separate worktrees. The only thing they have in common is the
phase and this page.

## Half one: restored sessions inherit dev reports

**Why it exists.** A dev report belongs to the session that published it. So a session brought back
after a smart shutdown, republishing the same file, would get a NEW report at a NEW link, and the link
the owner already had would freeze at the last version the old session wrote. The restart would
quietly break every dev report link the owner was holding.

**What was built.** One Gateway route, `POST /gateway/workspaces/{id}/restore/dev-reports`, one new
rule class (`DevReportInheritance`), one store operation (`DevReportStore.PassToSession`), and one call
from `DirectorRestore` per seat. **No schema change**: a report row, and each note and answer on it,
already carries its session id in an ordinary column, so passing a report is an update of existing
columns.

**The join cannot be forged, and that is the heart of it.** The old session id is the seat's captured
session id and the new one is its restored session id; both are read from the STORED workspace, which
is the join the record already held. There is no field in which a caller can name either session, and
the restored id can only be written by a restore mark carrying the start token of the Director that
started the seat. The pass proceeds only when every one of these is PRESENT - captured workspace, live
restore lease held by the asking Director, the seat exists, the seat names what it came back as - and
anything else refuses with its reason.

**Its check, from that half's own proof** (the Developer's run, not this seat's):

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~DevReport|FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Passed | Failed |
|---|---|---|
| Before, untouched `origin/main` at `0346b3992` | 650 | 0 |
| After | 665 | 0 |

The 15 added are 11 rule tests and 4 restore tests. Also run there: `SessionKeyGuardTests` 287 passed;
the hosted project, filter `WorkspaceRestoreRouteTests|DevReportRoutesHostedTests`, 38 passed. Six
mutation rounds showed the new tests can fail.

## Half two: the command line door

**Why it exists.** A Director is emptied and restarted from its own File menu, and that menu is a
window. One broken window must never again leave a Director impossible to empty.

**What was built.** Three commands, and one door underneath them:

    cc-devthrottle director smart-restart          start it, and watch it to the end
    cc-devthrottle director smart-restart-status   where it stands, asked once
    cc-devthrottle director restart-history        the restart records, newest first

Three host-level tunnel verbs - `smart-restart/start`, `smart-restart/progress`, `smart-restart/history`
- hand straight to what phases 1 and 3 already built: `ISmartShutdown` for the start and the progress,
`IDirectorWayUp.ReadHistoryAsync` for the history. Nothing was re-implemented. `SmartRestartWire` is
the ONE translation from the engine's records into the wire shape both sides share, and it copies
words rather than choosing them: the phase label, each row's state label, the count sentence, the
reason a start was refused, the sentence saying how it ended and every label in the history are all
computed once on the Director and printed verbatim. The command line chooses layout and when to ask
again; it never words a state.

**Who may do which.** Starting is the OWNER'S, exactly as `POST /machines/{machine}/director/restart`
is: `SessionKeyGuard` refuses a session key before the allow list is reached, with its own sentence
naming `cc-devthrottle machine restart-request` as what a session may do instead. The two READS are
open to a session key, because they read the same account's own records that `GET /gateway/workspaces`
already serves - asking how an emptying is going, or what was emptied last week, is not asking to empty
anything. That widening is a deliberate admission decision, listed as two literal shapes and never as
a prefix, and it was put to the Delivery Lead in the proof.

**A start is answered as soon as the run is TAKEN**, like the restart cycle and the restore beside it:
the run closes sessions for as long as the owner allowed - up to an hour - and no request stays open
that long. The command watches through the progress verb and ends by printing how the run ended.

**A third command exists that the mandate did not ask for**, `smart-restart-status`. Without it
`--json` and `--no-watch` are dead ends: they print an acceptance and there is then no way to learn how
the run ended, so "it prints how it ended" would be unreachable in exactly the modes a script uses.
The Developer that added it named it as its own to be overruled on, and it has not been overruled.

**Its check, run by this seat on the merged result**, baselines measured on untouched `origin/main` at
`297c090a5` in a throwaway worktree of its own:

| Check | Before | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 617 passed, 0 failed | 637 passed, 0 failed |
| `tools/cc-devthrottle/tests` (pytest, from `tools/cc-devthrottle`, `FORCE_COLOR=1`) | 3401 passed, 3 skipped | 3448 passed, 0 failed, 3 skipped |

Also run on the merged result: the new hosted route tests, 7 passed; the whole
`CcDirector.Gateway.UnitTests` project, 6856 passed, 0 failed, 8 skipped; the Avalonia smart restart
screens, 147 passed; `tools/test_shipped_tools_contract.py`, 52 passed. Eight mutation rounds showed
every new test can fail; they are listed in `review-phase-4-2-answers.md`.

**A warning about this machine's Python, because the first measurement of that project was a false
red.** `cc-devthrottle` declares a floor of `click>=8.2.1`; the Python on PATH here has click 8.1.8,
below that floor, and on it about 1500 of those tests fail on untouched `origin/main` for reasons that
have nothing to do with this work. Every number above was taken in a throwaway virtual environment
(Python 3.11.6, click 8.5.0, typer 0.27.2, rich 15.0.0, pytest 9.1.1). One thing has changed since the
half's own proof was written: **typer 0.27.2 no longer pulls click in**, so installing typer, rich,
requests and pytest alone leaves the environment with no click at all and every run dies on import.
Install click explicitly. The underlying problem - a shipped tool whose declared floor is not installed
on the machine it ships from - is not this phase's and was not fixed here.

## The reviews, and what became of every finding

Both halves were reviewed by a Reviewer running a different agent from the one that wrote the work, and
in both cases the seat that built the work was gone, so a Developer stood in for it and answered - the
method's law 11. Neither review blocked its merge.

**Review 1, the dev reports half** (`review-phase-4-1.md`, answered in `review-phase-4-1-answers.md`).
Two findings, both low, both answered the same way: **the code behaviour is unchanged and the record is
corrected.**

1. The start-of-run retry ask for a seat already back is reported to nothing but the Director's log.
   Declined as a code change, accepted as a correction to the proof and the code comments - the remedy
   would tell the owner nothing, because no product surface reads the restore's result at all. That
   wider gap is named rather than closed.
2. A note the settle pass already refused for the ended session moves with the report but stays
   refused, so the restored session never receives it. Answered in the record.

**Review 2, the command line half** (`review-phase-4-2.md`, answered in `review-phase-4-2-answers.md`).
Six findings - two medium, four low. Five accepted, one accepted in part, none declined outright:

1. *(Medium)* **The three Gateway routes had no test anywhere.** Accepted. The guard was tested as a
   pure function, the Director's dispatch on a real host, the translation over what the engine raised,
   and the command line against a stubbed Gateway - and nothing reached the three route handlers, so a
   route mapped one word off, a broken relay, a wrong status code or a miswritten tenant resolution
   would have shipped green through every suite the phase ran. A new hosted test class boots a real
   Gateway with real credentials and a Director really on the tunnel, registered at an endpoint nothing
   listens on so any working answer could only have come over the tunnel. Seven tests: the session key
   refused the start through the real authentication with the named sentence and the Director never
   asked; the owner's device key reaching the engine with the order it asked for; both reads open to a
   session key and answered by the route; another account finding none of the three; an unreadable
   answer as a 502 that says so; an unreadable order as a 400 with nothing asked.
2. *(Medium)* **The documented success exit code was racy and unreachable on the normal path.**
   Accepted in part. Accepting the restart is the launcher stopping the very Director being polled, so
   the usual end of a run that WORKED is exit 3; exit 0 needs a poll to land in a window that may be
   zero length. The documentation and the command's help were corrected to say so and to name the
   restart history as what tells a successful run from a dead Director. Declined: having the command
   ask the Gateway what a silence meant, which would be the client ruling on what a new Director id
   under an old name means - critical rule 7. If a single code for a successful restart is wanted, the
   honest place for it is a Gateway verdict on the record.
3. *(Low)* `--count` did not apply to `--json`. Accepted; it now narrows the same shape.
4. *(Low)* A malformed answer one level down was silently degraded. Accepted; an `entries`, a
   `sessions` or a `seats` that is not a list of objects is refused with the reason rather than
   becoming an empty history or a run with no sessions.
5. *(Low)* `--machine` without `--director` was silently ignored. Accepted; it is a usage error on all
   three commands, while the flag still narrows a name beside `--director`.
6. *(Low)* The history was capped at 25 records and reported the cap as the Director's whole history.
   Accepted. The cap stays - it is right for the start-up read, and reading past it on this door would
   make the history and the way up disagree about what exists - but it is now carried rather than
   hidden: where older records exist the Director's own sentence says how many are shown, how many
   exist, and that the rest are kept on the Gateway.

## What phase 4 could NOT reach

Stated plainly, because this is the part that matters to phase 5.

- **Nothing in the command line half has ever spoken to a real Director, a real Gateway process outside
  a test, or a real launcher.** The new hosted tests boot a real Gateway in-process and a stand-in
  Director on the tunnel, which closes the route gap; they do not close the end-to-end one. The
  isolated rig (`scripts/restart-qa-rig.ps1`) was not used. **Phase 5 should cover these three commands**
  - that was the Developer's recommendation, it is this seat's, and nothing since has made it less true.
- **A start that actually RUNS through the host is untested.** `ControlApiHost` has no seam for a
  Gateway client, so the host tests cover every answer a Director with NO Gateway can give - which is
  every refusal - and none of the accept path. The accept path's own pieces are tested separately: the
  engine's start is phase 1's, and the translation over what that engine really raised.
- **The exit code a successful restart really produces has not been measured**, only reasoned from the
  launcher's own contract. The documentation now says what the reasoning says; one real run would
  confirm or correct it.
- **No product surface reads the restore's result at all** (found while answering review 1). A seat
  whose dev reports failed to pass is recorded in the restore's own outcome and in the Director log,
  and nothing shows either to the owner. That is a gap in the restore, not in this phase's work, and it
  is named here so it is not lost.
- **`Gateway.Tests` was run only under filters**, never whole; `Core.Tests`, the web tests and the other
  Python toolbelts were not run. This work touches none of them, which is a reading rather than a run.
- **The minutes are not validated in Python.** `--minutes 7` is sent and refused by the Director in the
  engine's own words. That is deliberate - the allowed times live in one place - but it costs a network
  call to be told no.

## What the Delivery Lead may still want to decide

Carried forward from the command line half's proof, unchanged by the review:

1. The third command (`smart-restart-status`), or folding it into a flag. It is merged as a command.
2. The two reads being open to a session key. Merged as open; closing it is deleting six lines and two
   tests.
3. Whether phase 5's real run must cover these commands. It should.
