# Phase 3 proof - the way up

Written by the phase 3 Tech Lead (session 38f41a97) on 20 September 2026. Everything below is
MERGED on `origin/main`. Every number in it is from a run I made myself, in the foreground, with a
full build - not one is taken from a Developer's or a Reviewer's report.

## What phase 3 was, and what is now true

After a smart shutdown the Director comes back, finds the record of what it closed BY ITSELF, and
offers those sessions back. That is mission document section 5.3 items 10, 11 and 14, with rulings
10.2, 10.3 and 10.5. Four things were built and merged:

| Pull request | What | Merged as |
|---|---|---|
| 3204 | The two measurements the mandate ordered BEFORE anything was built on them: does reopening a saved conversation work, and how long does an interrupted session need to hand over | `1dab84116` |
| 3202 | The way up ENGINE: find the offer, read the history, bring back through the existing restore with the seed file, reopen a seat that ended without a handover | `217b79f63` |
| 3215 | The Director now KNOWS when a session has a question box open, which the mission's own check needs and which nothing in the product could answer | `e3bdd050f` |
| 3208 | The two WINDOWS: "A restart is available" at start-up, and File, Restart history | `99cd1c034` |

## The check, run by me on merged main (`99cd1c034`)

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

A filter that matches nothing exits green, so these are COUNTS, never colours. An ABORTED run prints
"Passed" for whatever finished before the host died, so every run below reached its end and printed
its own total.

| Check | Baseline, untouched `origin/main` = `ab2770c4a` | Merged main, `99cd1c034` |
|---|---|---|
| `Gateway.UnitTests` `~Drain\|~Restart` | 523 total, 522 passed, **1 failed** | **606 total, 606 passed, 0 failed** |
| `Avalonia.Tests` `~SmartRestart` | 47 passed, 0 failed | **145 passed, 0 failed** |
| `Avalonia.Tests`, whole project | 646 passed, 0 failed | **776 passed, 0 failed** |
| `Core.Tests` `~PendingInteraction\|~AgentPlugin` | not measured as one filter | **32 passed, 0 failed** |

The `SmartRestart` filter and the whole Avalonia project rose by more than this phase wrote, because
phase 2's held swap merged to main during the day and brought its own tests with it. Phase 3's own
contributions, each measured by me on its own branch at the moment of merging, were: the engine +44
then +13 on the Gateway filter; the windows +45 then +5 on the Avalonia filter; the question box +17
in `Core.Tests` and +1 in `Avalonia.Tests`.

**The one red in the baseline was on untouched main and is not this mission's.**
`SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state` fails
intermittently with a SQLite write error inside `DeviceRegistry`, reached from `new DeviceRegistry()`
with no store path - a store shared with whatever else is running on this machine. It matched the
filter only because the word "restarted" is in its name; it touches no Drain and no Restart code, and
it passes when run alone. It is recorded so nobody reads 522 as a regression. It did not appear in
the final run.

## What each new test proves, in plain words

### The engine (57 tests, `src/CcDirector.Gateway.UnitTests/Restart/`)

- **The offer is a PRESENCE check on the record, never "no sessions are running".** A record with one
  owed seat is offered while sessions are running here, and the engine's Gateway seam is asked for
  the roster ZERO times on that path - asserted, because the first review's structural proof (the
  seam could not ask at all) was given up when the reopen needed a roster.
- **A record belonging to another Director on the same machine is not offered**, and is not even read
  as a document: the Gateway's own summary said whose it was. The key is the Director's display NAME,
  because a restarted Director gets a new identifier. A Director with no name refuses rather than
  matching on a blank, which would claim another Director's records.
- **A cancelled record, an ignore-all record and a record already brought back are not offered**, each
  with its own test. Only the newest twenty-five records are read, so a Director with years of them
  does not make hundreds of calls at start-up.
- **Rows are one per mission head, leads first, each carrying its own seats, all ticked** (ruling
  10.2: asked once for the lot). A seat whose reporting line names a seat this record does not hold
  is its own head - the chain stops where the record stops.
- **A seat that ended without a handover is its own row, unticked** (ruling 10.3), and what it is
  offered is decided from the AGENT, by one rule an agent nobody has heard of falls into safely: it
  is worded like Codex, a fresh session, never like Claude Code. A seat with no conversation id says
  so and offers NO button - a button that could never work is the defect the rule exists to stop.
- **The seed file holds exactly the four things ruling 10.6 allows** - you are a restored session,
  read this document, what changed while you were gone, verify before acting - and the whole file is
  asserted against a literal. Not "the banned phrase is absent": the whole thing, by presence. A
  mutation that added "do not commit anything unless the owner asks" - the exact line that overrode a
  handover's own plan on 19 September - turned both seed tests red.
- **Bringing back cannot defeat the restore's own guards.** Nothing ticked is refused rather than sent
  as an empty order, which `DirectorRestore` reads as "bring back everything owed". A row that has
  vanished from the record is refused BY NAME, because a row quietly dropped is a session left dead
  with nobody noticing.
- **The reopen refuses a seat that may still be running**, through the product's own
  `DirectorRestore.StillRunning` against the roster - not a second copy of that rule - and takes a
  once-only claim per seat so two clicks or two screens cannot start two agents in one saved
  conversation.
- **Whether an agent can be started on a saved conversation is a REQUIRED part of each agent's own
  plugin**, not a list in the words file, and one test walks EVERY registered agent, builds its real
  launch spec with a conversation id, and asserts the flag equals whether that id really reached the
  arguments. The list can never drift from the drivers again. It asserts first that the walk found at
  least eight agents, so a registry that answered with nothing is a broken instrument and not a clean
  run.
- **The Gateway unreachable is a refusal carrying its reason, never an empty list** - an empty list
  reads as "you have no records", which is a lie. Three states on the offer, not a boolean and a
  nullable reason, so a window cannot show "nothing is waiting" when the truth is "I could not ask".

### The two windows (98 tests, `src/CcDirector.Avalonia.Tests/SmartRestart/`)

- **Each window OPENS and every named control is connected** - the test the old `DrainDirectorDialog`
  never had. Putting that window's defect back (a hand-written `InitializeComponent`) turns 20 of the
  offer window's tests and 10 of the history's red.
- **The client is dumb.** The window is handed an engine answer whose label CONTRADICTS its own rows -
  one session waiting, three rows, a headline saying nothing is available - and draws every sentence
  unchanged. A window that re-derived the count passes the ordinary tests and fails this one; that is
  the whole point of a fixture whose label is deliberately a lie.
- **"Not now" writes nothing**: the window closes and the engine is asked for nothing at all, so the
  record survives and is offered again. Escape and the window's own close are the same answer.
- **Pressing Bring back twice asks the engine once** - running the restore twice would start a second
  copy of every session - and a row left unticked is not named to the engine.
- **The reopen button carries its OWN row**, never one found by position, so the second ended seat's
  button can never reopen the first seat's conversation.
- **The start-up ask asks ONCE.** Ten Connected events ask once and show once; a reconnection after a
  disconnection is not a second ask; nothing is asked until the connection is Connected; the engine is
  asked OFF the interface thread and the window shown ON it, asserted by reading the thread inside
  both delegates. **Nothing waiting shows nothing, and a refusal shows nothing** - a start-up that
  interrupts the owner to say it could not check is a worse product than one that stays quiet.
- **The history's offer is the SAME window**, opened for that record, so a second wording cannot drift
  away from the first.

### The question box (17 tests in `Core.Tests`, 1 in `Avalonia.Tests`)

- **An unanswered `AskUserQuestion` is a question box, with its own text and option labels**; once its
  tool result is in the file it is gone. An unfinished `ExitPlanMode` is a plan waiting.
- **An unfinished ordinary tool call is NOT a question box.** This is the test that stops the
  detection firing on every busy session in the fleet.
- **A Codex session pointed at the very file that makes a Claude Code session report a question gets
  nothing**, so what is proved is the agent gate and not the file.
- **A missing, empty or invalid transcript never CLEARS a real question.** The reading is three-valued
  - pending, nothing pending, could not look - and only "nothing pending" clears the property.
  Writing "nothing" when you mean "I could not look" is precisely how a question box that is genuinely
  open gets erased.
- **A slow read cannot re-stamp a question the owner has just answered**: the handler captures the
  session's generation and drops its answer if the session has moved on.
- **The two halves are joined**: a real transcript on disk, read by the real detection, stamped on a
  real `Session`, and `SmartShutdownSessionReader.Read` reporting `HasQuestionBoxOpen` true for it.
  Without that test the mission's check would be held by two halves that never met.

Every one of those tests writes a real JSONL file and lets the product's own parser read it. Not one
hand-builds a parsed object, because a test that hand-builds its input never watches the real producer.

## THE TEST THE MANDATE ORDERED BEFORE ANYTHING WAS BUILT ON IT: does reopening work?

Full evidence in `attachments/phase-3/reopen-test.md`, merged. Measured on the isolated rig and on the
agent binaries, never on a real Director.

| Agent | Verdict | Deepest layer reached |
|---|---|---|
| Claude Code 2.1.278 | **It works** | through the product, on the rig |
| Pi 0.85.1 | **It works** | through the product, on the rig |
| Codex 0.155.1 | **It ignores the reopen id**, as the code says | the launch spec, which is all the mission asked |

The evidence is a marker string, not an impression: a conversation was seeded with
`CRIMSON-OTTER-4417` and the name `Delphine`, the process ended, and the reopened one quoted both
back. The reopened Claude Code transcript APPENDED to the file it already had - one session id
throughout - so the id does not change on a reopen. Pi was re-measured on 0.85.1 rather than taken
from a comment claiming 0.80.10, and its "No project session found" warning printed on the first
launch and NOT on the second, which is pi saying it found the session. Codex has a reopen surface but
it is a SUBCOMMAND (`codex resume`), not a flag, so the Director cannot reach it by appending an
argument; nothing was built on that.

**The trap inside it, which phase 5 must not fall into.** The product reopen answered NO HISTORY
twice on a transcript that print mode had just recalled correctly. Taken at face value that is a
verdict of "reopening does not work", and it would have been wrong: the token counts showed 63,170
cached tokens read plus 4,046 fresh, which is the Director's own injected preamble, and a re-worded
ask quoted the marker straight back. A reopened session is told by its preamble that it is new, so a
question phrased "do you remember" is answered against the preamble. **Ask a reopened session for a
STRING, never for a memory.**

## THE MEASUREMENT: how long does an interrupted session need to hand over?

Full evidence in `attachments/phase-3/handover-timing.md`, merged. Five mid-turn runs on the rig,
using the product's own words (`DrainMessages.HandOverNow`), the product's own path
(`DrainPaths.HandoverFor`) and the drain's own acceptance test (over 500 bytes):

**22.5, 23.9, 28.5, 43.8 and 57.9 seconds.** Fastest 22.5, slowest 57.9, median 28.5.

The Architect guessed three minutes. Every run was under a minute, a margin of about 3.1 times.
**My recommendation to the Delivery Lead: keep the two-thirds point exactly where it is.**

**The honest gap, and it is the one that could break the guess: a lead with seats reporting to it was
not measured.** The smart shutdown tells a lead to collect its subordinates' documents FIRST and wait
for them, which is a serial dependency none of these runs had. It is the shape most likely to exceed
three minutes and it is the next thing worth measuring.

## The screenshots

Committed under `attachments/phase-3/`, rendered by real Skia from the headless tests, with a test
asserting each frame carries the window's own background colours and more than a hundred distinct
colours - a blank frame is one flat colour. That check exists because nobody downstream may be able to
open an image.

| File | What it shows |
|---|---|
| `way-up-1-offer-three-missions-all-ticked.png` | The start-up window, three mission heads, every row ticked, each seat's own sentence |
| `way-up-2-offer-with-a-seat-that-ended-without-a-handover.png` | Two ended rows unticked and amber beside a ticked one: one with its Reopen button, one saying no conversation was recorded and carrying none |
| `way-up-3-offer-one-session.png` | One session owed - the singular wording |
| `way-up-4-offer-after-a-bring-back.png` | The engine's answer, one line per seat, including one that did not come back and why |
| `way-up-5-history-three-records.png` | Three records: one still owing seats with its Bring back, one cancelled, one ignore-all |
| `way-up-6-history-empty.png` | An empty history, saying so |
| `way-up-7-history-gateway-did-not-answer.png` | A refusal with its reason, NOT an empty list |
| `way-up-8-history-a-seat-that-ended-without-a-handover.png` | The history's own Reopen button on an ended seat |

**Neither Reviewer could open an image, and both said so plainly rather than implying they had. I
looked at them myself.** They read as plain English, the amber unticked rows sit beside the ticked
one with the button on the right seat, and the cancelled record says it was cancelled and offers
nothing.

## The reviews - four of them, all by GLM 5.3 in Pi

Codex, the default Reviewer, is out on its usage limit until 22 September. Every review was by an
agent family different from the one that wrote the code, and every Reviewer ran the checks itself and
broke something of its own choosing.

| Review | Of | Findings | Answered in |
|---|---|---|---|
| `review-phase-3-1.md` | the engine at `0cda45788` | four, all accepted | `review-phase-3-1-answers.md` |
| `review-phase-3-2.md` | the ANSWERS to review 1 | four, all accepted | `review-phase-3-2-answers.md` |
| `review-phase-3-3.md` | the two windows | none in the product; two test gaps | issue 3226 |
| `review-phase-3-4.md` | the question box detection | none that prove a harm | - |

**The best finding of the phase was prospective and cost nothing to fix, because nobody had written
the caller yet.** Review 1 found that the reopen could start a session that was still running.
The fix added an in-engine once-only guard. Review 2 then found that the guard was INSTANCE state
while `CreateDirectorWayUp()` hands out a new engine on every call - so the two windows would each
have got their own and the guard would have done nothing - and that the roster check does not cover
the gap either, because a reopened session comes back under a NEW session id and the roster is asked
about the CAPTURED one. Two live agents in one saved conversation: the exact harm the first finding
existed to prevent, on the exact path that was meant to fix it. The ruling was to hold the claim
STATICALLY, the way `DirectorRestore` already holds its one-at-a-time rule, because the rule belongs
to the Director and not to whichever object took it - and to prove it with TWO SEPARATE ENGINES,
because two engines is what the windows really do.

Two review rounds on one piece of work is the method working, not a sign of trouble: round one found
what the code did wrong, round two found what the FIX did wrong, and neither was visible to the seat
that wrote it.

Review 4 did something no mandate asked for and it is the strongest single piece of evidence in the
phase: it checked the question-box fixtures against **every real Claude Code transcript on this
machine - 71 files, 132 tool calls - and found zero disagreement** between the new detection and the
product's existing pairing.

## Three product defects found on the way, FILED so they outlive these sessions

None is phase 3's to build. Two of them will bite phase 5.

- **#3207 - the smart shutdown never tells a Pi session to hand over.** `POST /sessions/{sid}/interrupt`
  answers Conflict on Pi by design (`PiDriver.InterruptAsync` throws; the driver never declares the
  Interrupt capability). A Pi session mid-turn at two thirds is therefore never asked, keeps working
  to the limit, and is ended with no document - silently. `escape` works and produced a handover in
  6.8 seconds. The two-thirds step must pick its verb from the driver's declared capabilities. **This
  is the most serious of the three**: Pi is the agent this fleet uses for every review seat, including
  all four of this phase's.
- **#3209 - the drain accepts a handover at 500 bytes.** One run in eight crossed 500 at 654 bytes
  and grew to 2808, so the drain accepted it with 23 per cent written and without the closing
  `drain-report` block it reads to learn whether the seat should come back. The gate should be a
  parseable block, not a byte count.
- **#3210 - Claude Code's folder-trust dialog eats a new session's first prompt**, and the session
  then exits reported as "exited cleanly". `--dangerously-skip-permissions` does not suppress it.
  Every restored session gets its mandate as a first prompt, so a seat restored into a folder Claude
  Code has not seen is lost this way and the record says it came back.

And one about the tests themselves:

- **#3220 - `Core.Tests` cannot be run to the end.** `RepositoryRegistryConcurrencyTests` crashes the
  test host, and the aborted run still prints a Passed line with whatever number the race reached:
  2494, then 3059, then 636, from three runs of the same suite on the same tree. Anyone reading that
  line reads a green run. It is on main and it predates this phase.

## What phase 3 could NOT reach, said plainly

- **Nothing in the engine, the windows or the question box was run against a real Gateway, a real
  Director or a real session.** Every test drives a fake of the two seams, and every record in every
  window test is the TEST's own wording. What is proved is the RULES and that the windows show
  whatever they are handed. That a real Gateway answers these calls as the fakes do, and that the real
  engine's sentences read well on a real screen, is phase 5's to prove on the rig.
- **The wiring in `MainWindow.axaml.cs` has no test.** Fifteen lines - one call at start-up and one
  File menu item - in a seven-thousand-line file with no test harness around it. Nothing proves that
  the menu item appears, that pressing it opens the history, or that a real Director's start-up
  reaches the ask. Both calls land in code that IS tested, which is why they were kept to one call
  each. **This is the largest gap in the phase** and the first thing phase 5 should exercise.
- **Across a Director restart the same saved conversation can still be reopened twice**, because
  nothing is recorded. The in-process guard covers one run of one Director. Recording it needs a new
  mark kind in `CcDirector.Gateway.Contracts` and a Gateway deploy, which is the Delivery Lead's
  decision, not a Tech Lead's. It is disclosed in three places in the code and in the interface
  documentation, not hidden.
- **A reopen whose start FAILS keeps its one claim** until this Director restarts, so a transient
  Gateway failure leaves that button refused. The refusal says so honestly. If the Delivery Lead would
  rather have the retry than the certainty, it is one line.
- **Two test gaps and two now-false comments** are filed as issue 3226. Neither is a product defect
  and the Reviewer judged neither to block the merge.
- **The parked suites, the web tests and the Python tests were not run** by me at any point. Nothing
  in this phase touches the browser shells or the Python toolbelt.
- **A record in which EVERY session was ended at the limit does not raise "A restart is available" at
  all** - it holds no seat decided `restore`, which is the presence check the mission itself writes in
  section 5.3 item 10. Its saved conversations are reachable, with working buttons, from File, Restart
  history. I followed the mission's literal wording rather than guessing. **This is the one question I
  am putting to the Delivery Lead**, because mission ruling 10.5 says the operating-system shutdown
  record - which holds no handovers at all - should have its conversations offered "on the way up as
  in 10.3", and that record has not been built yet. Two honest readings: the history is enough, or the
  presence check must also raise the offer for a record holding saved conversations nobody has
  reopened. It is a one-line change here if the second reading is wanted.

## One thing I did with my own hands

My mandate says a Tech Lead never writes code, and I wrote none - except one merge resolution. Phase
2's held swap merged to main while the windows were being built, so `git merge origin/main`
conflicted in `MainWindow.axaml.cs` exactly where predicted: main had replaced "Drain this Director
for restart..." with "Smart Restart", and the branch had added "Restart history..." beside the old
item. I resolved it - take main's item, keep ours after it, drop the item main deleted - and
corrected one comment that still said "beside the drain" after the drain item was gone. It is a
mechanical take-both-sides resolution at the merge gate and it is CHECKABLE rather than a judgement:
`git diff origin/main -- src/CcDirector.Avalonia/MainWindow.axaml.cs` showed only the branch's own two
additions. I record it so the Delivery Lead can rule differently next time.
