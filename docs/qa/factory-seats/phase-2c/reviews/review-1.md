# Review 1 - Factory Seats phase 2c: the Gateway opens every run

Reviewed 13 October 2026, on the branch `factory-seats/review-2c`, cut from
`origin/factory-seats/phase-2c-gateway-opens-runs` (two commits, `02581fb3c` and `c1a6f9a14`).
The Reviewer was opened by the Implementation Lead and read only; nothing was changed but this file.

## Verdict

The change does what the mission document says for phase 2c, and the paths it must not disturb are
provably undisturbed. Two findings are returned, one medium and one low; neither blocks the work, and
both are for the Developer who built it to accept or decline. The proof the Developer wrote is honest
about what it does not show, and every claim of it that this review re-ran held.

## Scope

**What I read.** The whole difference between `origin/main` and the branch, except the two generated
`*.Designer.cs` migration files (the migration code files, both model snapshots, the entity, the
contract records and the history store mapping were all read). The mission document from the
`factory-seats/mission` branch, phase 2c and goals 6 and 7 and the owner's decision that the boss's
role is Boss. The Developer's proof at `docs/qa/factory-seats/phase-2c/README.md` and the four composed
texts - read as claims, checked against the code, not trusted. The wiring of the brief store in
`GatewayHost`, the per-account sweep in `CronTenantSweep` (which is what makes the brief read happen in
the job's own account), the refusal check in `FactoryScheduleLink` (which runs before the brief read and
covers the archived-factory and unregistered-seat cases), and the command line's `factory record`.

**What I ran.** Three narrow commands from the worktree of the branch, on Windows, not the whole suites
and not the mission's full check:
- the two new opening test classes: 30 passed, 0 failed;
- the registry and talk endpoint tests: 113 passed, 0 failed;
- the one new command line test (`test_record_TheTalkedLineOfATalk_SendsTheBriefVersionTheTalkWasGiven`):
1 passed.

**What I could not reach.** A live hosted Gateway: no run was fired through a real `GatewayHost`, so
the host's own two wiring lines are proven only by the boot smoke test and by the tests wiring the same
expressions. A real PostgreSQL: the PostgreSQL migration was read and the model check the Developer ran
is reported, but it was not applied to a live database here. No agent was given the new text, so how a
session behaves on being handed a brief of 65,536 bytes inline, or a slash command followed by the
opening, is untested - the Developer says the same in the proof's own "what this does NOT prove" list.
Other accounts' factories were not read, so whether any other account has a boss seat stored with
another role word is not known (the Developer checked our own account: all eleven bosses are named and
roled Boss).

## Findings

### Finding 1 (medium): a failure reading the brief store at fire time starts nothing, records nothing, and tells nobody but the log file

Where: `DirectorCronSessionStarter.StartRunAsync`, the line `var seat = _seatWithABrief(job);`.

The task asked which of the two wrongs a failed brief read produces, and the answer is the better one
with a hole in it. The run is **never started without its brief** - the read happens before the create
is handed to the Director, and an exception there stops the fire before anything starts, so no silent
wrong run is possible. That is the right side, and it should stay.

But nothing is recorded. The chain:

- `_seatWithABrief` is wired in `GatewayHost` to `FactoryBriefStore.GivenTo`, which opens a database
  context and reads the brief versions table; any failure there throws.
- `StartRunAsync` has nothing around that call, and `CronEngine.FireAsync` wraps the starter call in a
  try with only a `finally` - no catch.
- `CronEngine.EvaluateDueAsync` catches the exception per job, writes one line to the Gateway's log
  file, and moves on to the next job.
- So `_history.Append` never runs: no run row is written, and `MarkFired` never runs either, so the
  schedule stays due and the next sweep tries again.

Every other way a start fails takes the other path: the starter *returns* the error, the engine writes
a run row that says "not started" with the reason, and the factory's page shows it. The starter
interface's own contract says so: it "reports the error so the engine records a not-started run". This
new database read in the fire path is the first thing that can throw straight through that contract. The
harm: while the brief store is failing, a factory whose seats have published briefs silently stops
running - no run row, no activity row, nothing on any screen the owner watches, one line in a log file
on the Gateway's machine. If the store later recovers, the missed due times do get recorded then, and
the run goes as a catch-up; if it does not, the factory simply never runs again and nothing on the
screen says why. For a product whose scheduled runs act unattended, an invisible stop is exactly what
the run history exists to prevent.

To be fair: the same hole already existed for the registry read inside the refusal check, which this
phase did not add. The brief store doubles it, and it is the read this phase made load-bearing for every
factory run.

The fix is small and belongs in the starter: read the brief inside a try whose failure is *returned*
as the starter's error - "not started: the seat's brief could not be read" - so the engine records a
run that did not start, with the reason, the way it does for a Director that is down. The run must
never start without its brief; it just must not vanish either.

### Finding 2 (low): a talk that changes the goal of a factory whose boss still has another role word now cannot complete as instructed

Where: the refusal added to `FactoryRegistryStore.Validate` (a boss seat whose role is not Boss is
refused), against the talk seed's goal-change step, which the branch did not touch.

The registry's new refusal is the owner's own ruling (13 October 2026: the boss should just be called
boss) and it is scoped correctly: a factory already *stored* with another role word is still read,
still listed, still talked to, its scheduled runs still start - `Find` reads the stored row without
re-validating, and the refusal check on a fire does not look at the boss's role. Only *registering*
is refused, and the refusal names the fix.

The interaction: a talk's closing instructions tell the seat, when the goal changes, to "register the
factory again so the page shows it", and the seed tells it to write that manifest from
`cc-devthrottle factory list --json`. For a factory stored before this ruling with a boss role word
like the quoted one, that copy carries the old role word, and the registration is refused. The goal
change - a thing the owner asked for, in a talk, with the owner present - cannot complete without a
second change the owner did not ask for in that moment: rewriting the boss's role to Boss.

This recovers in one round (the refusal says exactly what to change, the owner is right there, and the
change is one the owner has already ruled he wants), so it is low. But it is a real case of the
question the task asked - something that worked yesterday (changing a goal in a talk, by
re-registering with the stored role word) now refuses, for an account that cannot be asked first - and
the Developer and the Implementation Lead should say, on the record, that they accept it.

## The questions the task asked, each answered

**Byte for byte, for a schedule in no factory and for a seat with no published brief?** Yes. When
`ScheduledRunOpening.SeatFor` returns null - no factory on the schedule, the factory not registered,
the seat not one of its registered seats, or no published brief - `ScheduledRunOpening.PromptFor`
returns `ScheduledRunClosingInstruction.PromptFor` of the stored prompt unchanged, which is exactly the
call the starter made before this phase. Three pinned tests hold the pre-phase texts as string
constants captured from the code at the branch point and compare, and they pass. A seat whose factory
is archived never reaches the brief read: the refusal check runs first and refuses a fire of an
archived factory's schedule, recorded as a run that did not start, as before. A schedule whose seat is
no longer registered is likewise refused first, before the brief is read.

**If reading the brief fails at run time?** Finding 1: the run never starts (correct), and it is not
recorded anywhere a person can see (the defect).

**Can a draft ever be given?** No. `FactoryBriefStore.GivenTo` returns only the highest version whose
state is published; a seat with drafts and nothing published reads as having no brief, and the session
is started exactly as before. Tested both for a run and for a talk.

**Can one account's brief reach another account's run?** No. The talk route resolves the account from
the request and passes it into the read; the scheduled sweep (`CronTenantSweep`) enters each account's
scope in turn, so the starter's brief read runs in the job's own account, and the store is keyed by
account, factory and seat. The same seam already carried the refusal check.

**Can a brief's text break out of its frame?** It can, but that is not a new capability, so this
review returns no finding for it. The two marker lines around the brief are ordinary text and a brief
can contain a copy of the end marker, after which the rest of the brief reads like the Gateway's own
words. But the brief was already, before this phase, a whole file the session read and obeyed; a
published brief can tell the session anything without forging any marker, and publishing is a person's
act by design. The markers are a reading aid, not a security boundary, and the Developer's proof says
as much (decision 8). Nothing this phase built makes the trust worse than a file brief was.

**Can a slash-prompt skip the opening, or a normal prompt lose it?** No and no. A prompt that does not
begin with a slash is led by the opening, always. A prompt that begins with a slash keeps the command
first, by design and by test, because a slash command runs only as the first thing the session is
given; the opening then follows it. The shape "text appended behind a slash command" already existed -
the closing instruction has been appended behind such prompts since before this phase - so the change
adds no new risk there. What remains unproven is the one thing the proof already lists as unproven: no
agent has been given a command followed by the opening, so how the harness treats the trailing text is
not shown here.

**The registry refusal against factories already stored?** Reading, listing, talking and running are
all untouched (answered above, and Finding 2 for the one real interaction). The Factories screen still
shows a stored role word for a factory registered before the ruling; the reference and the shipped
terminology skill were updated, and the repository copy of that skill was regenerated from the shipped
file - the two bodies agree, and the guard test for that agreement is in the suite.

**The two migrations, both snapshots and the entity?** They agree. Both providers' snapshots gained the
same nullable integer property, both migration files add one nullable `BriefVersion` column to
`cron_runs`, and the entity, the contract record and both history-store mappings carry it. A nullable
addition is safe on a table that already holds rows: existing rows read null, which the contract
record documents as "a run recorded before briefs were held on the Gateway". Not verified here against
a live PostgreSQL (see Scope).

## Observations, not findings

- A talk's brief version is recorded by the talk's own agent typing `--version <n>` into its talked
  line. The Developer named the weakness plainly (decision 1): the number is self-reported by the
  session. The Gateway's answer and its log also carry it. Accepted as a phase 2c shape; the record
  should say so where the activity rows are read.
- The talk to a boss seat with a distinct stored role word no longer says the role beside "the boss".
  This is the owner's ruling, the proof names it as the one deliberate wording change, and the pinned
  word-for-word tests use bosses whose role is Boss, so the change is visible only in the proof's own
  sentence. Consistent with the mission document.
- The line endings around the brief block differ by surface (the run uses `\n`, a talk uses the
  machine's own line ending), exactly as the rest of the talk seed always has. Cosmetic.

## What the seat that built the work should do

Finding 1 deserves the fix: the read is new, it is in the one path the product promises to record,
and the fix is a try that returns the failure instead of throwing it. Finding 2 is a decision to
record, not necessarily a change to make. Everything else in this phase, this review could not
disprove, and several of its sharpest edges - no draft ever given, no cross-account path, byte-for-byte
for every seat with no published brief - it confirmed by reading the code and by running the phase's own
tests.
