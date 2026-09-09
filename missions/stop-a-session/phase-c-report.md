# Phase C report - the eight inspection defects

Written for the Architect by the Phase C Manager (session `e7ea69df`). Branch
`mission/stop-a-session`. **Nothing was merged to main** - that is the Architect's, and it is the
only "done".

**All eight findings from `inspection-1.md` are closed.** None is narrowed - in every case the
specific defect the inspection reproduced no longer reproduces, and a test that runs the production
code path fails without the fix.

**Four of the eight carry a named residual**, and I am putting that in the second sentence rather
than the last section, because a report whose summary says "all closed" and whose fine print says
otherwise is the same shape of untruth this mission exists to remove. None of the four is a piece of
the original finding left undone:

- **I1** - a tunnel that dropped before the command left now writes a row saying a stop was sent and
  its outcome is unknown. The audit gap is closed; a new and weaker claim exists in its place, taken
  deliberately because silence is the worse error.
- **I2** - on the undescribed path the row is still removed, so a live-but-unreadable process can
  lose its row. That follows the Architect's own ruling that established facts are still reported.
- **I5** - a genuinely definite Director-side failure is now announced with the weaker "outcome
  unknown" wording, because the Gateway answers it with the same status as a dropped tunnel. An
  over-hedge, in the safe direction, and a Gateway-side gap rather than a client one.
- **I6** - closed on the stop's own call path. The same unencoded-path hole exists on one other
  command-line verb, outside this finding's scope, and is reported rather than fixed.

Each is set out below with what changed, what test now fails without it, and my verdict.

---

## Rule Zero came first

`origin/main` had moved to `db141e28` while the mission ran, leaving the branch 38 commits on a base
4 behind. It was rebased onto a fresh `origin/main` **before any fix was written**, not after. The
rebase was clean. The solution built with 0 warnings and 0 errors, and I ran the suites on the new
base before hiring anyone, so that every number below is measured against a tree that was already
current.

---

## The standard this phase was held to, and whether it was actually met

The inspection's most valuable result was not a finding. It replaced the production liveness check
with the constant `false` and **all 68 tests in `SessionCommandExecutorTests` still passed**, because
every test injected its own substitute for that method. A test that cannot fail is not coverage.

**I verified the fix for that by my own hand rather than accepting the Worker's table.** I extracted
an isolated copy of the final tree, replaced the body of the production liveness method with the
inspection's own constant, and ran the new tests:

    Failed: 2, Passed: 4, Total: 6

    Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped
      Assert.Equal() Failure: Strings differ
      Expected: "stopped"
      Actual:   "alreadyStopped"

    Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped
      Assert.NotEqual() Failure: Strings are equal
      Expected: Not "alreadyStopped"
      Actual:       "alreadyStopped"

Restored, the same six tests pass. **The mutation that used to leave a suite green now reddens.**
That is the single result this phase existed to produce, and it is the one I would ask an inspector
to re-run first.

---

## The eight findings

### I1 - a cancelled request could leave a completed stop with no audit record

**Changed.** The audit append no longer belongs to the reply. Every road out of the handler that
DISPATCHED something now writes a row, including the roads where the Gateway never learns the
outcome, which are recorded as exactly that rather than as silence. The request's token still goes
down the tunnel deliberately - a caller who has gone away must not hold a Director wait open - but it
can no longer delete the record. The reason now travels down the tunnel instead of a null payload.

**What fails without it.** `A_stop_the_caller_stopped_waiting_for_is_still_recorded_with_its_reason_and_actor`,
against the real endpoint over HTTP with a real audit log and a controlled tunnel, with an uncancelled
control beside it that differs in exactly one line. Removing the append from the cancellation catch,
or handing the request token back to it, reddens it with the message *"The stop was dispatched to the
Director and no audit row was ever written for it."* Putting the null payload back reddens
`The_reason_travels_down_the_tunnel_with_the_stop`.

**Verdict: CLOSED.** With one honest limitation the Worker named and I am repeating rather than
burying: a tunnel drop cannot be told apart from a send that never left, so a tunnel that dropped
before the command left now writes a row saying a stop was sent and its outcome is unknown, for a stop
that may never have happened. That was chosen deliberately on the principle that silence is the worse
error, but **the row is not evidence that anything was stopped and nobody should read it as such.**
Closing it properly needs a delivery acknowledgement the tunnel does not have.

### I2 - an unreadable live process was treated as a process that is gone

**Changed.** Liveness now has three answers, not two: `Alive`, `Gone`, and `Unreadable`, carrying the
machine's own words about what could not be read. `ArgumentException` from `Process.GetProcessById` is
a real `Gone`; every other failure is `Unreadable`. When liveness cannot be read the stop still
attempts the shutdown, then reports `stoppedNotDescribed` with a detail line naming what specifically
could not be read. It never reports `alreadyStopped` and never sets `ProcessEnded`. Only a process
established alive can be established to have ended, so an unreadable reading before the stop skips the
re-check afterwards rather than letting a second unreadable answer certify an ending.

**What fails without it.** Six tests that run the executor with **no injected liveness seam at all**,
so the real method executes - the mutation evidence above. Also the fold tests: making the fold print
the older-Director sentence over every cause reddens three of them.

**Verdict: CLOSED.** The forbidden inference is gone. One consequence is named rather than hidden: on
the undescribed path the row IS still removed, so a live-but-unreadable process can lose its row. That
follows the Architect's ruling that facts which WERE established are still reported, and the operator
is told in words what could not be read - but nothing hunts for the process afterwards.

### I3 - a running remote session could be reported already stopped after cancellation failed

**Changed.** `ISessionBackend` gained a default-implemented member reporting the last shutdown
failure, defaulting to null so no other backend changes. The remote workflow backend now sets it where
it previously only wrote the words into its own terminal buffer. The executor reads it after the kill,
and a backend that reported its own shutdown failed is Ruling 3's third failure - the stop fails and
**the row is deliberately left in place**, so the operator can see the session and try again. A
session with no process identifier is no longer an assumed absence either: it is
`stoppedNotDescribed`, saying that nothing could be checked.

**What fails without it.** Removing the executor's read of the backend failure reddens with
`Expected: Error / Actual: Ok`. Restoring the backend's swallowed cancellation reddens
`Assert.NotNull() Failure: Value is null`. Treating a missing process identifier as an established
absence reddens with `Expected: Not "alreadyStopped" / Actual: "alreadyStopped"`.

**Verdict: CLOSED.** Proven against a stub client only; no real workflow run was cancelled or refused.

### I4 - the Cockpit destroyed the stop answer on the next roster update

**Changed.** The answer no longer belongs to a component mounted inside the row it describes. A
provider is mounted in `AppShell`, above the router outlet, so it outlives every roster refresh and
every route change. I confirmed that placement in the committed code myself rather than taking it from
the notes.

**What fails without it.** Six tests driving the **real** `SessionRoster` and the real session page:
stop the session, let the roster refresh with the stopped row absent, and require the headline and the
Done button still to be there. Wrapping either placement in its own provider - putting ownership back
inside the row - reddens them with `Unable to find an element with the text: stopped 9c41e7a2 -
process 51884 ended, row removed`.

**Verdict: CLOSED.** The Worker also established, by reading and then by a control test, that the
phone does **not** share this defect, and correctly did not change the phone for it - the test it added
would redden if someone later hung the session screen off the roster.

### I5 - clients turned an unknown outcome into a definite one

**Changed.** The shared client error now carries the HTTP status it was refused with, and null where
there never was one. The command line keeps `Not stopped:` for a 4xx - every 4xx on this route is
decided before the Director is asked, so nothing was carried out - and says `Outcome unknown:` for
everything else, adding one line of its own saying it cannot tell whether the session is still
running. The unknown wording is the DEFAULT, so a status nobody has thought about cannot inherit a
claim. The Director window uses one wording that claims nothing, because the client it is handed
raises one exception type and carries no status. The shared web client's opposite claim - `The session
was stopped, but...` on a malformed body - is gone: a 200 does not establish that anything was
stopped, since three verdicts arrive on that status and one of them stopped nothing at all.

**What fails without it.** Forcing the prefix back to `Not stopped:` for every failure reddens four
command-line tests; the desktop test now drives the dialog with the Gateway's own *"it is not known
whether the command was carried out"* sentence and reddens if the window contradicts it; restoring the
web claim reddens two.

**Verdict: CLOSED**, with one over-hedge recorded in the safe direction: the Gateway answers a
Director-REPORTED failure ("the process would not die") with the same 502 as a tunnel that dropped
mid-command, so the command line announces a genuinely definite failure with the weaker prefix. The
Director's own definite sentence prints directly underneath, so nothing is lost to the reader. **That
is a Gateway-side gap, not a client one**, and it was reported rather than fixed silently.

### I6 - a repeat stop by a name containing a slash failed instead of answering

**Changed.** A new helper escapes one caller-typed value so it stays one segment of a path, and the
stop uses it. It is deliberately not applied inside the request helper, because the separators
BETWEEN segments are part of the path.

**What fails without it.** A test that stands up a real loopback HTTP server routing by segments the
way a route template does, and runs the whole command against it with nothing stubbed but the roster
fetch. **I read this test rather than trusting its description**, because the original 25 green tests
were blind to this defect precisely by stubbing the request helper. It is real routing. A second test
gives the rig its teeth by sending the raw path and asserting 404, then the escaped form and asserting
200 - so a server that answered anything at all could not let the first test pass. Restoring the raw
path reddens both, one with `no route matches POST /sessions/Mission/Worker-I/stop` and one with
`URL can't contain control characters`.

**Verdict: CLOSED for the stop.** The Worker audited all twelve interpolations in that module: eleven
are safe because the value is a resolved identifier, never a typed string. One other caller-typed
interpolation exists and is **reported, not fixed**, because it is outside the stop's call path - the
`--machine` target. A machine name cannot contain a slash on Windows, but `?` and `#` would acquire
URL syntax there exactly as they did here, and the fix is the same one-line helper.

### I7 - Enter could send repeated stops while the controls said Stopping

**Changed.** The busy guard moved onto the action itself on both web surfaces, not only onto the
button, and the phone hook regained the guard the previous remove operation had.

**What fails without it.** Removing the guard from either shell's stop action reddens with
`expected "spy" to be called 1 times, but got 3 times`. The Worker avoided the trap Phase B recorded -
a test that awaits the second press deadlocks under its own mutation instead of going red - by never
awaiting the second press: the stop is held on a promise released by hand, the extra presses are
fired, and the call count is asserted synchronously. Both mutations reddened in a couple of hundred
milliseconds; neither run hung.

**Verdict: CLOSED.**

### I8 - the phone's stop failure rendered outside its own modal, and Cancel deleted it

**Changed.** The failure now renders inside the dialog the operator is looking at, in the Gateway's
own words, with the typed reason still in the box - which is what the Cockpit already did. Backing out
no longer deletes the explanation.

**What fails without it.** Tests that query **within the dialog element** rather than the whole
document, which is why the previous test missed it. Moving the error back outside the modal, or
restoring the clear-on-cancel, reddens them with `Unable to find an element with the text: the
Director on SORENLAPTOP could not be reached`.

**Verdict: CLOSED.**

---

## Two fixes I made myself

Both were raised by a Worker in files belonging to Workers I had already released, and both were
stale assertions of exactly the kind this mission exists to remove. Neither changes behaviour.

- **The stop command's docstring said three verdicts when the fold has four.** The behaviour was
  already right - the exit code follows the 200 and the headline prints as the Gateway wrote it, so a
  fourth verdict works today and a fifth would too. The replacement says why that number must never
  become something the client branches on.
- **The undescribed-stop log line said "older version?"** when, after I2 and I3, that verdict has two
  more causes and the line could no longer know which. It now names no cause and carries the
  Director's own body instead, which is the only thing at that point that knows.

## One defect I found that the Workers missed

Running the shared-client suite on the final code **while the parked gate was building underneath
it**, a new test failed. It then passed five consecutive runs on a quiet machine. That shape is a
race, not a flake to be waved away, and the cause was already written down elsewhere on this branch:
a loopback handler that answers without draining the request body leaves it in the socket, Windows
aborts the connection before the client can read the answer, and the asserted 400 arrives as a
connection error. The Worker had written that exact fix, with that exact reasoning, into its OTHER
handler and had not carried it across. Fixed, with the reasoning recorded in the test, because an
intermittent test nobody can explain is how a real defect later gets dismissed as noise.

---

## What went wrong in the running of this phase

**Four separate incidents, one structural cause: five seats working in one shared worktree.** Recorded
as one finding rather than as four mishaps, because that is what it is.

1. **My own error.** My force-push clobbered the QA seat's notes seconds after they landed. I
   recovered them from the local object store, cherry-picked them back, and pushed. The four Worker
   briefs were then amended to say fetch-and-rebase, never force.
2. **The Architect's error**, recorded at its own request and not softened. Its `git add -A` in the
   shared worktree swept in-progress Phase C work into a commit whose message describes documentation.
   It also **deleted three lines of a Worker's stop handler, broke the build with CS8321, and blocked
   another Worker** until the first restored it. The Architect initially told the owner it had cost
   nothing and has since corrected that.
3. **My own near-miss.** I read the Gateway endpoint in the working tree, saw a comment saying the
   reason goes down the tunnel above a call passing null, and was about to report it as a
   false-comment defect. It was a Worker's live mutation, mid-test. **Reading a shared worktree while
   Workers mutate in it gives false readings**; from that point I judged committed state only. A
   Manager reporting a Worker's mutation as a defect is the same class of error as a green suite
   certifying broken code.
4. **A Worker's own catch, and the best evidence in this phase that the discipline is real.** Two of
   its mutation runs were thrown away rather than reported: a failed build meant `--no-build` ran the
   PREVIOUS mutant's binary and printed a plausible, entirely fabricated result. **A test result is
   only real if the build that produced the binary succeeded.** Another Worker moved its mutation runs
   into an isolated copy of the tree after a run died on a build output file locked by a third seat's
   live test host. Neither killed anything.

Two further notes. A commit on this branch carries a stray character in its subject, picked up from a
shell here-string; the Worker corrected the record forward rather than rewrite history on a branch
five seats share, which is right, because force-pushing here had already cost us once. And two Workers
hit a weekly usage limit mid-phase and resumed after the owner switched accounts; neither lost work.

**The placeholder habit tried to recur a third time and was caught.** One Worker's notes carried a
literal `GATE-RESULT-PLACEHOLDER` in its parked-suite row. I told it not to commit that - run it, or
say plainly it was not run. It replaced the row honestly and additionally flagged which of its other
rows were stale relative to the final code. I took the parked gate for myself so there would be one
authoritative run on final code rather than two Workers colliding for eighty minutes each.

**On the roster the owner asked for.** He asked mid-phase that the report show every session that
built this feature and how many. That is written up separately, and the count is fifteen sessions
across six phases. It collided with the standing rule that no assistant or vendor is named in this
public repository; I raised it rather than decide quietly, and on being told to carry on I took the
reading that respects the standing rule - the count, the phases and the relationships stay, the
product names come out. The load-bearing fact survives without them: **the seat that INSPECTED came
from a different family than the seats that WROTE, and found eight defects every builder had reported
green.** One edit reverses it if the owner wants them named. Six of the fifteen seats cannot be named
by identifier at all, which is filed as issue #2781 and is the mission's own subject looking back at
it: this feature writes an audit row naming who stopped a session and why, the polite path writes
nothing, and that is exactly why those six are unnameable.

---

## What is NOT proven, named as gaps

Consolidated from the Workers' own notes and my own runs, rather than copied.

- **No end-to-end run in this phase.** Phase B stopped a real session through a locally built Gateway
  and Director. Nothing in Phase C re-proved that against the fixed code. The QA seat's live run is
  the thing that would.
- **The unreadable liveness answer is proven on Windows only**, and the state is MANUFACTURED by
  locking a process's access control list rather than reached by the production route. No process on
  this machine makes the read throw naturally. The exception that arrives is the same; the road to it
  is not. On a non-Windows host the test fails loudly saying it is unproven there rather than skipping
  into a false green.
- **A tunnel drop cannot be distinguished from a send that never left**, and is recorded as unknown
  anyway. See I1.
- **The timeout and tunnel-drop tests inject the router's already-synthesised failure**; they do not
  sleep through a real deadline. The join between a genuinely expiring timeout and this handler is
  exercised by nothing.
- **The Director is a stub in the Gateway tests, and the Gateway is a stub in the client tests.** No
  test in this phase drives a real agent process, a real tunnel, or a real database.
- **No pixels, no real browser, no desktop interaction.** Component tests only.
- **The remote backend change is proven against a stub client.** No real workflow run was refused.
- **Two path-containment symbolic-link tests remain unprovable on this host**, unchanged from Phases A
  and B: they need Developer Mode or elevation to create a file symbolic link, and fail loudly rather
  than skip. This branch touches nothing under path containment.

**The first parked run was KILLED, and that is recorded rather than quietly re-run.** The full
`-Parked` gate was stopped by the operating system for low memory partway through, while the two
parked suites were executing. **A killed run is a broken instrument, not a pass** - and it is exactly
the shape of thing that gets written up as "green" by someone reading only the nine PASS lines above
the point where it died. The nine non-parked suites had already printed complete results with full
counts before the kill, and those stand; the two parked suites had not, so they were re-run on their
own, sequentially rather than in parallel, which is what the memory pressure demanded. The numbers in
the table are from that second run.

**What the parked run does and does not cover, stated precisely rather than waved at.** It ran against
`e1af3b5b`. Three commits landed afterwards, and **not one of them changes an executable .NET line** -
they are five documentation files and one Python test file, and the gate runs no Python tests at all.
So the parked result is valid for every .NET line it covers, with nothing to re-run. The Python file
that did change is the loopback-handler drain described above, and I ran that suite myself three times
after the fix. Phase B had to say it did not re-run eighty minutes of suites for a comment; this phase
can say the stronger thing, because the difference was measured rather than assumed.

---

## Housekeeping

- All four Workers were verified, told what was verified, and flagged for deletion.
- The mission record is committed on the branch: the four briefs, the four notes files, the roster,
  and this report.
- Nothing was merged to main and no pull request was opened.
- No process was killed that this phase did not start.

---

## The suites, every number from a run I watched

**Written at 12:31 on 9 September 2026, at the Architect's instruction to push everything
immediately because its seat is being replaced. `Gateway.Tests` was STILL RUNNING when this was
written, and its row says exactly that rather than carrying a token.** The incoming Architect
inherits a fact, not a gap.

### The nine suites of the default gate

These printed complete results with full counts inside the `-Parked` run that was later killed. They
are real results from a real run against `e1af3b5b`, and they independently match the run I did on
the freshly rebased base before hiring any Worker.

| Suite | Result |
|---|---|
| CcDirector.Core.UnitTests | 227 passed, 0 failed |
| CcDirector.Gateway.UnitTests | 4233 passed, 0 failed, 8 skipped, 4241 total, 2 m 51 s |
| CcDirector.Avalonia.Tests | 421 passed, 0 failed |
| CcDirector.Engine.Tests | 63 passed, 0 failed |
| CcDirector.HostedAgent.Tests | 88 passed, 0 failed |
| CcDirector.Launcher.Tests | 191 passed, 0 failed |
| CcDirector.Terminal.Avalonia.Tests | 25 passed, 0 failed |
| cc-director-setup.Tests | 25 passed, 0 failed |
| cc-director-setup-engine.Tests | 541 passed, 0 failed |

Note the Gateway unit suite finished **inside** the 120-second-per-suite ceiling here at 2 m 51 s
measured, having been stopped by that ceiling in the default gate earlier in the day. That suite is
borderline rather than slow, exactly as Phase B established; it is not this branch's to fix.

### The two parked suites

| Suite | Result |
|---|---|
| CcDirector.Core.Tests | **4396 passed, 0 failed, 8 skipped, 4404 total**, 8 m 1 s |
| CcDirector.Gateway.Tests | **NOT YET REPORTED - still executing when this file was written.** Ten test-host processes were alive. No number is claimed for it here, and nobody should read its absence as a pass. |

**`Gateway.Tests` is the suite that matters most to check, and it is the one still outstanding.** It
is where the host-bound route tests and the cross-tenant isolation tests live, it is the only suite
that runs them, and in Phase A it was the suite that caught a cross-tenant regression the default
gate could not see. Phase C changed the stop route substantially - the audit row on every dispatched
road, and the verdict shape. **Whoever picks this up runs it and reads it before this branch is
landed.** One command:

    dotnet test src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj -c Debug

The Worker who closed I1 ran its own 26 `SessionStopEndpointTests` against the real endpoint over
HTTP and reported 26 passed, unmutated; that is a subset of this suite and not a substitute for it.

### The web and command-line suites, all run by me on the final code

The local gate runs no web tests and no Python tests at all, so these are the whole of that coverage.

| Suite | Result |
|---|---|
| `@devthrottle/client-core` | 1042 passed, 98 files, exit 0 |
| `@devthrottle/cockpit` | 326 passed, 37 files, exit 0 |
| `@devthrottle/mobile` | 62 passed, 11 files, exit 0 |
| `npm run typecheck`, all four workspaces | exit 0 |
| `cc-devthrottle`, full suite from its own directory | 273 passed, **2 failed** |
| `cc_shared`, full suite | 129 passed, **1 failed** |
| `tools/test_shipped_tools_contract.py` | 36 passed |

**The three failures are pre-existing and none is this branch's.** The two in `cc-devthrottle` are
the email and spawn help-rendering tests, failing inside the installed command-line dependencies
while rendering help; I confirmed them on the freshly rebased base **before any Worker started**, so
they are a baseline rather than an inheritance. The one in `cc_shared` is a missing markdown parser
dependency, and this branch touches nothing in that area.

---

## For whoever picks this up

1. **Run `CcDirector.Gateway.Tests` and read it.** It is the single outstanding item and the section
   above says why it is the one that matters.
2. **Re-run the constant-substitution mutation** if you want one check that this phase did its job:
   replace the body of `DefaultProcessLiveness` with a constant and watch
   `SessionCommandExecutorLivenessTests` go red. It is the mutation that left 68 tests green before
   this phase.
3. **Do not read the nine-suite table as the whole gate.** It is the default run, which parks two
   suites and runs no web or Python tests.
4. **Nothing is merged and no pull request is open.** The branch is 60 commits ahead of `origin/main`
   and zero behind.
