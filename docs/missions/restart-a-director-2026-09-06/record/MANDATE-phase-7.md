# Mandate - Phase 7 of issue 2719: the QA report, one real Director restarted start to finish

You are a Worker on the restart epic (`thefrederiksen/devthrottle` issue 2719). Your phase is
**issue 2726**, and nothing else. Read 2726 in full first:

```
gh issue view 2726 --repo thefrederiksen/devthrottle
```

**This phase is the deliverable.** The owner reads THIS. Everything else in the epic exists to make
it possible. In his words:

> "your goal is to show me a QA report showing how you can completely restart a director, start to
> finish and it keeps going exactly on all the sessions that were started before you asked it to
> restart."

## The thing you must not do

**This is not a summary written after the other phases, and a phase-by-phase green does not prove the
cycle.** On 2026-09-06 the drain was flawless, every handover was on disk, the index was complete -
and the fleet still stayed dead for two hours with nobody noticing. Six green phases proved nothing
about the whole. You are the only evidence that they add up.

So: a real run, on a real Director, with real sessions, recorded WHILE IT HAPPENS. Not reconstructed
afterwards from what should have occurred.

## THIS PHASE IS TWO RUNS, NOT ONE - decided 2026-09-07

**Run one, the RIG run: the mechanism.** Everything below, on the isolated rig you are building - own
root, own launcher, own `app\cc-director.exe` built from your tree, own Gateway on a local port. It
proves the cycle works. Its honest limit, which goes prominently in the report and not in a footnote:
**the owner's accept happens on the rig Gateway's admission surface, not on his phone against
production**, so it proves the MECHANISM and not his experience of it.

**Run two, the PRODUCTION run: the experience.** A genuine restart of **DevThrottle_1**, on
production, with the owner's own accept on his own phone. That is not a test of the feature - **it IS
the feature**, and it is the thing this epic exists to deliver.

**Sequence is fixed and not negotiable: rig first, production second, and only after the rig run
passes.** The Architect puts run two to the owner when run one passes; you do not initiate it.

**One condition on run two, and it is rule zero: THE DRIVER CANNOT BE ON DevThrottle_1.** That run is
driven from **SORENLAPTOP**. A driver that dies in the restart it is running is the exact failure this
epic exists because of - it happened on 2026-09-06 and cost two hours of a dead fleet nobody noticed.

Two product defects found while designing the rig are filed and are NOT yours to fix - #2742 (launcher
registration is last-writer-wins on an unauthenticated machine name, so a second launcher silently
removes a machine's restart capability) and #2743 (the restart command carries a `Path` the launcher
ignores, so a Director outside `instances\default` cannot be restarted at all). #2743 is why a bare
high slot does not work and why the isolated rig is necessary.

## START NOW - the rig is built BEFORE the phases merge

**You are seated deliberately early.** You cannot RUN the cycle until phases 1 to 6 are merged, but
everything expensive about this phase is setup, and setup does not need them. It currently sits after
the critical path and it does not have to. **Build the rig today and have it ready to press go.**

What you can do now, in this order:

1. **Provision the slot 7 Director** -
   `scripts\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\scripts\local-build"` (always pass
   that `-OutputDir`; the default is the dead repo-root `local_builds\`). Launch it and confirm it
   registers. It is a throwaway; nothing on it matters.
2. **Seed it to look like a working machine** - the fleet described below: two missions, a real
   Architect-Manager-Worker chain, one session mid-turn, one snoozed, one genuinely wedged, and real
   uncommitted work in a real worktree. This is the slowest part and the easiest to get wrong.
3. **Build the wedged seat and prove it is wedged** - that it genuinely cannot produce a handover, not
   that it has been told to act stuck. Prove it before you need it.
4. **Write the before-picture capture** - the script that records every live session, its mission,
   role, branch and uncommitted count at the moment of the request. You need it working before the
   run, not during.
5. **Write the report skeleton** with every section heading from this mandate, so on the day you are
   filling in observations rather than deciding structure.

**Do not run the cycle, do not drain anything, and do not restart slot 7 for real until the phases are
merged and the Manager says go.** A rehearsal that half-works will teach you the rig is fine when it
is not; keep the powder dry.

Tell the Manager when the rig is standing and seeded, because that is the moment the epic's remaining
time becomes just the merges.

## Your tree, and your Director

Worktree `D:\ReposFred\devthrottle-restart-p7`, branch `restart-phase-7`, cut from origin/main today
so you can start. Rebase onto main as the phases merge - by the time you RUN the cycle your tree must
carry all of them, and a rig built against a tree that no longer exists proves nothing. Work only
there.

**The QA run does NOT happen on DevThrottle_1.** That is the Director on this machine and it carries
every mission on this box - the whole restart epic, its Architect and its Manager, and unrelated
missions. Restarting it to prove the feature would take all of them down, and a driver that dies in
the restart it is running is the exact failure this epic exists because of.

**Provision your own Director in SLOT 7.** Slot 7 is chosen and reserved for you: slots 1 to 4 are the
owner's, slot 6 already exists on disk in the `devthrottle-session-rules-p2` worktree so another
mission has a claim on it, and slot 5 is the number every document reaches for by default, which
makes it the likeliest collision. Slot 7 is clear and above the relay floor, so it relays without
`confirmProtected`. Build to it with
`scripts\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\scripts\local-build"` - always pass that
`-OutputDir`, because the default is the dead repo-root `local_builds\`.

**Your own seat must not live on the Director you are restarting.** You drive slot 7 from
DevThrottle_1, where you are seated now. That is rule zero of the `director-restart` skill and it is
the finding that cost two hours on 6 September: the session driving that drain sat on the Director it
was restarting, died at the restart, and never reached the restore - eighteen handovers sat on disk
looking finished while the fleet was gone, and nobody noticed until the owner asked where his sessions
were. **Phase 7 is where that rule gets demonstrated rather than restated.**

## Seeding it to look like a working machine, not like a test

Several sessions across **at least two missions**, with a real reporting chain - an Architect, a
Manager, Workers. Real repositories. **At least one session mid-turn** when the request is made, **at
least one snoozed**, **real uncommitted work in at least one worktree**, and **one seat that is
genuinely wedged** (see below).

**Keep it cheap but REAL.** A hollow session has nothing to hand over, so the drain reads as flawless
while proving nothing. A fleet of idle look-alikes would make the whole report worthless, because
nothing it proves would transfer to a real machine.

## The wedged seat - the most valuable thing in this mission

**NOTHING HAS EVER REFUSED TO STOP.** Every session in the 6 September drain reached a clean stop. So
"never force" is a rule that has never once been tested, it is the single biggest unknown in this
epic, and every phase so far has quietly assumed the happy path. The plan says so and issue 2726 says
so. Slot 7 is a throwaway Director, which makes it the first and only safe place to find out.

So seed a seat that **CANNOT reach a clean stop** and prove the drain does the right thing with it.

**It must be genuinely wedged, not a seat told to pretend.** A session instructed to act stuck is
still able to answer, so it proves nothing - it would write a handover the moment the drain leaned on
it. Wedge it for real: a `RawCli` session sitting on a command that never returns is one honest route,
and there are others. Whatever you choose, the seat must be incapable of producing a handover, not
merely unwilling.

**Expected, and every part of this is asserted:**

- the drain STOPS;
- the wedged seat is recorded `blocked`, with its reason in its own words;
- **the restart does NOT happen**;
- the seats already closed stay closed and are in the record;
- the owner is told.

A partly drained Director is a normal, recoverable state. **A Director restarted over a session that
never wrote anything down is not** - that is the failure the whole never-force rule exists to prevent,
and this is the first time anyone will have seen it refuse.

**Then recover the blocked drain, in the same pass, BEFORE you clear the wedge.** This is the part
that turns never-force from a slogan into something affordable. By the time the drain blocked it had
already closed every seat that handed over cleanly - leaf-first, rolling, by design - so the expected
outcome of the rule is a half-gone fleet with no restart. Restore those already-closed seats from the
workspace **with no restart in between**, and show them carrying on the work their handovers describe.
A seat that is still alive must NOT be duplicated. The run must end recorded as
**`directorOutcome: not-restarted`** with **`seatOutcome.scope: all`** - Phase 3 split those two facts
into separate fields precisely so this combination is expressible.

**Do not read the coarse `outcome` field alone anywhere in this report.** It is derived from the pair
and it is lossy by design: a refused restart derives `blocked`, exactly like a drain that blocked on a
wedged session. Those are the two most important and most easily confused results in your entire run,
and only `directorOutcome` tells them apart. A report that says "outcome: blocked" without saying
which one it was has described nothing.

Phase 5 owns that path; if it is missing when you get there, that is a Phase 5 defect and you stop and say so
rather than doing it by hand and letting the report imply the product can.

Then clear the wedge and **run the cycle again to green**. The report needs both halves: it refuses
correctly when it should, and it completes when it should. One without the other is half a proof.

**You still do not decide what "force" means.** Nothing in this epic decides that, and this run does
not either. You are proving the refusal, not designing a way past it. If the drain does something
other than refuse cleanly, that is a finding - stop, write it up, and tell me.

## THE OPENING SENTENCE OF THE REPORT

Write this first, before the lead finding and before the run. Plainly, in one or two sentences:

**While cataloguing this defect class across two thousand catch sites, we built a fresh instance of it
into the drain we were writing to fix it.** Phase 4's review found that an unknown or missing
drain-report state fell into the permissive `drained` branch - it would have **closed a session on an
answer nobody understood**. That is not a near-miss in old code. It is new code, written today, by a
seat that had already been sent the finding.

Say what that means and do not soften it: **knowing the class, naming it, and hunting it across the
codebase did not stop us reproducing it in the thing we were building.** That is the honest state of
how well any of this transfers. A report that leads with nine old defects and buries this one flatters
us.

### The strongest instance: a person folding a state about a person

**Report this unsoftened; the Architect asked for it in his own words and it is the sharpest example
in the mission.** He wrote, of a seat that had not produced a severity breakdown after three requests:
*"a seat that cannot produce the severity breakdown of its own review has not read its own review
carefully enough to have fixed it."*

The Manager then measured it from the artefact instead of asking a fourth time. **Both review files
contained ZERO severity tokens** - no Critical, High, Medium, Low or P1 anywhere in 387,000 bytes. The
number never existed. The seat had answered three times with exactly what the file held.

The available answers were: *gave me the number*, *would not give me the number*, and **the number does
not exist**. The third folded into the second - and the second is the accusatory one. In his words:

> "That is this mission's headline defect, committed by the Architect, while policing it, against a
> seat that had done nothing wrong, on the same day I wrote the rule into eight mandates and a
> published skill."

**And it corrects the rule itself: the permissive branch is not always the LENIENT one. It is whichever
branch costs the READER least.** "They are not answering" cost less than "my question may be
unanswerable". Every other instance in this report is a program folding a state; this one is a person
folding a state about a person, and it went toward blame because blame was cheaper to hold.

Report beside it that the same seat was doing the thing properly the whole time: its round-two review
opened with *two of nine fixes fully fixed and seven carrying a residual path*, and it volunteered
unprompted that its own fix round had created a defect - carried-over and newly-introduced stated in
words, before anyone asked for the columns. **The seat was reporting composition while being accused of
not having read its own review.**

**The rule that comes out of it:** *a number we want from an author is usually a number we can take
from the evidence, and asking is how we end up with reported-by rather than measured-from.*

### Measuring the defect changed the behaviour being measured - report this, it is a finding about us

The composition rule asked every round to split its serious findings three ways: carried over, newly
introduced by the last round's fixes, and newly discovered. The prediction was that seats would
**under**-report the middle column, because it is the one that says your last fix made things worse.

**Every seat over-reported into it instead.** After a day of this culture, claiming your own fault had
become the safe move - the column with status. One seat claimed a carried-over finding as
self-inflicted, and only a check found it was the previous round's finding 8 near verbatim.

That is not dishonesty; it is the opposite, and it is dangerous for a specific mechanical reason:
**the reset ruling FIRES on newly-introduced dominating.** A column biased upward biases the rule
toward firing, so it would have reset branches that were converging. The Architect named it as his own
fault for making confession the high-status column.

**The fix, and it worked on first use: every newly-introduced claim must be CHECKABLE, not
confessional.** To put a finding in that column, cite the previous round's list and show it is not
there. **An unciteable self-attribution is carried-over until proven otherwise** - the direction that
keeps the trend honest rather than the one that keeps the seat comfortable. It also makes the column
auditable by a stranger, which it was not.

Applied immediately by the seat that had over-reported, it moved exactly one finding back to
carried-over, and the seat's own account is the evidence:

> "I reached for the confessional label because it reads better, on a finding where the evidence says
> otherwise."

**Report this as a caution on the whole report:** several numbers here were produced by a culture that
rewards self-blame, and the ones that survived a citation check are worth more than the ones that did
not. A measurement that changes the thing it measures is not neutral, and a report built on
self-reported columns must say which of them were audited.

### A true sentence defeated by the layer beneath it

The eight false safety sentences in this report were all claims the code under them did not honour.
**This one is different and worse, and it deserves its own line.** `ReadSessionCount` carries the
comment:

> "its absence beside a live Director is not *zero sessions*, it is *no answer*"

That sentence is **true**, it was written deliberately, and it is the best-reasoned comment found on
the mission - it was cited earlier in this report as the model the locator should have followed. And
the deserialization layer underneath it defeats it: `DirectorCrashJournalData.Sessions` has an
empty-list initializer, so a document containing `{}` produces a **present list of length zero**, and
the method returns a confident zero from a file that said nothing.

**So a correct guarantee, correctly documented, was already void one layer down.** Checking the
sentence against the method would have passed. The defect is that the method's input had already lost
the distinction before the method ran - which is the same lesson as the dictation endpoint's correct
positive test being fed a value that had already folded. **A guarantee is only as good as the layer
that feeds it, and neither reading the code nor reading the comment finds that.**

### A refinement to the citation rule, from its second use

In a **first** round, the newly-introduced column is definitionally **zero** - there is no previous
list for a finding to be absent from. A seat reported 1 there, then corrected itself: the defect had
been introduced by its *original* change and *discovered* by round one. Both facts are true and only
the second is what the column measures.

Report it, because it shows the rule doing what it was built for twice within minutes, on two
different seats, both correcting themselves toward *less* self-blame - which is the direction the
culture had been pushing against.

### A name collision is an instrument returning a TRUE hit about the WRONG THING

Report this as a rule, not an anecdote. Twice on this mission a search returned a real hit that meant
nothing:

- a coverage count was wrong because a bare `StopAsync` matched 156 test files across unrelated types;
- a search for the leaked field names hit `origin/main` - and the hit was a **different**
  `BlockedReason`, a voice `WaitingKind`, unrelated to the drain.

The second one mattered: reporting that hit would have told the owner, for the second time in one day,
that his own words were leaving his machine - and it would have been false. It was caught only because
the first collision had already happened.

**The rule: OPEN the hit, do not count it.** A grep is an instrument, and an instrument that returns
`true` about the wrong subject is not a weaker instrument than one that returns nothing - it is a more
dangerous one, because a hit reads as confirmation. Every count in this report that came from a
pattern match rather than from an opened file should say so.

### A base with something stacked on it is not free to rewrite

Report this as an incomplete rule rather than a mistake, because the decision it came from was right
on net and the report should say both.

Stacking was ordered deliberately: dependent phases were cut from their dependency's BRANCH rather
than waiting for merge-to-main, because serialising seven phases would have cost days and they were
not days of work. It worked - two phases were built in parallel that would otherwise have been built
in series.

**What was never said is the other half: once something is stacked on your branch, you are no longer
free to rewrite it.** A ruling later required one branch to delete a type and replace a single value
with a pair. That branch rewrote its history to do it - correctly - and in doing so **orphaned the
phase stacked on top**: the dependent's base commit was no longer an ancestor, and it could not rebase
onto main either, because the types its code was written against are zero files there. It became
structurally last, waiting on a branch that was itself under a P0.

The dependent seat attempted the rebase, found ten references to a deleted type and semantic rather
than textual conflicts, and **aborted, restoring its tree** rather than resolving by judgement onto a
moving target under time pressure. That was right: a conflict resolution made in a hurry onto a branch
that is still changing is new writing at the worst possible moment, and a mis-resolution is silent.

**The rule as it should have been given:** stack freely, but a branch with a dependent is a published
interface - rewrite it and you have signed up to rebase whatever stands on it, so either avoid the
rewrite or do the rebase yourself.

### "Fixed and unproven" is its own state - and only a mutation found it

A seat fixed a time-of-check-to-time-of-use race, and its fix was **correct**. A mutation that put the
old second reading back **SURVIVED** - because with no file changing between the two reads, the old
shape and the new one behave identically, so **nothing in the suite could tell them apart**.

The fix was right and unproven, and those look the same from a green suite. The seat closed it by
adding a reading seam so a test can make two consecutive readings disagree the way a registration
finishing its write does - then reproduced the reviewer's counterexample exactly: reading one says
nothing is running, the guard permits on it, reading two says a live Director holding three sessions,
and the assertion is that the Director is still alive afterwards.

**Report it as a third state alongside broken and working: `fixed and unproven`.** A passing suite
cannot distinguish a fix that works from a fix nothing exercises, which is the absence problem applied
to a remedy rather than to a defect.

### One field, two shapes

Every other instance in this report is one VALUE carrying two FACTS. This one is different: the
restart index writes `seats[].model` as a plain **string** when a model was reported, and as the whole
**folded object** when it was not. One field, two types, depending on state - and a reader written
against either is silently wrong against the other. Found by a harness, not by a reviewer.

### The honest limit of the method this report recommends

Phase 3's fix bounded seat counts by the size of the **fleet** rather than by the seats **owed a
restore**, so four-owed-one-restored still derived as everything came back. **The fold is in the
DENOMINATOR, and there is no branch to review.**

Every other instance in this report is a conditional somebody could read. This one cannot be found by
reading conditionals, and it cannot be found by grepping catch sites - which is exactly the method the
class sweep used across 2,001 sites and exactly the method this report recommends. **Say so plainly:
the sweep would have missed it, and a report recommending a method must state what that method cannot
see.**

### Why it keeps happening to people who are hunting it - report this, it is the mechanism

**Three separate seats reproduced this mission's headline defect inside the fix for that defect, after
each had been sent the finding:**

- **Phase 4**, in the drain: an unknown drain-report state folded into the permissive `drained` branch,
  which would have closed a session on an answer nobody understood.
- **Phase 2**, in its own pre-commit check: it grepped for a list of mutation strings the author
  happened to think of, and a mutation artifact reached a commit and disabled the route guard.
- **The class sweep**, in a save path: it made the dialog report **"Saved" over a write that did not
  happen** - a fresh state folded into a success it is not - one round after saying the same about
  somebody else's code.

**A FOURTH INSTANCE, IN THE TRACKING RATHER THAN IN THE CODE: a partial fix reported as a fix.** A
round-one finding had TWO halves - an in-place write that could truncate, and a read-to-write
lost-update race. The seat fixed the truncation, did not fix the race, and **reported the finding as
addressed**. Round two found it still open. `Addressed` was a binary hiding `partially addressed` -
the mission's own defect moved into the paperwork that TRACKS the defect. The seat disclosed it
unprompted, one message after praising the Manager for catching the same shape as a partial review
gate on the merged bootstrap. **The remedy is mechanical, not exhortative: when closing a finding,
state WHICH PARTS of it you fixed, never that you fixed it.** A finding is not an atom, and "done" is
the permissive branch.

Phase 2 explained why the class recurs, and its sentence is the most useful diagnosis in the report:

> "It was not a lapse of attention. I wrote a check whose pass condition was the absence of strings I
> had thought of, on the same day I fixed that exact shape twice in the product, and the reason it did
> not feel like the defect is that **an absence check reads as thorough while you are writing it**."

That is the mechanism. The defect is not carelessness and not ignorance - the writers knew the class,
had just fixed instances of it, and could not feel it in their own work, because enumerating the cases
you thought of feels like diligence from the inside. **Any remedy that relies on people recognising it
in the moment is answering the wrong problem.** The positive form is what broke it: asserting every
guard line PRESENT, verbatim, found a second artifact the absence check had already passed over twice.

## THE LEAD FINDING - the class itself

Every defect this mission found is the same defect. State it once, at the top, with the five
witnesses under it - not as six findings scattered through the report, because separately they read
as bad luck and together they read as a habit.

**A BINARY THAT SHOULD HAVE BEEN AT LEAST A QUATERNARY.** A reading of the world usually has FOUR
answers, and each is a different fact with a different fix:

1. **PRESENT AND UNDERSTOOD** - the ordinary case.
2. **PRESENT AND NOT UNDERSTOOD** - a value from a newer build, a typo, another version's vocabulary.
3. **ABSENT** - nothing was written where something should have been.
4. **COULD NOT LOOK** - it exists but could not be obtained: locked, corrupt, timed out, no answer.

Anything not named collapses into whichever branch is PERMISSIVE, because that is the branch nobody
writes a test for.

**CORRECTION, and the history is the useful part.** This was first published as "a binary that should
have been a TRINARY, the missing third state being I DO NOT KNOW". That framing was itself a weld: a
rule naming a COUNT teaches people to reach that count and stop. A seat followed it correctly, added
one state, and its next review found the code still folding on another - in its own words, **"I fixed
the unknown state and left the missing one."** The three-state rule collapsed answers 2 and 3, which
is the same error the rule exists to prevent, committed inside the rule against it.

The six witnesses:

1. **The locator** (issue 2730) - running or not-running, missing *could not read the registration*.
   Folded into not-running, and one consumer reads not-running as permission to START a Director. A
   corrupt file starts a second Director on a live instance home. Shipped.
2. **The safety sweep** - found something or clean, missing *never ran*. Folded into clean, which is
   why every seat was required to report even a nothing.
3. **The mutation harness** - killed or survived, missing *did not build*. Folded into survived, which
   voided every mutation claim on the mission at once.
4. **The capability handshake** - capable or too old, missing *did not answer*. Named as three states
   before it shipped; had it not been, it would have folded into too-old and condemned a machine that
   was merely unreachable.
5. **The workspace outcome** - restarted-and-restored or blocked, missing the whole
   Director-versus-seats distinction, so a recovery with no restart had no name at all.

6. **A second mutation harness**, on another seat - killed / survived, missing *did not build*, but
   folded the OPPOSITE WAY MECHANICALLY: it read a non-zero exit as a KILL. Opposite mechanics to
   witness 3, same defect - and both landed on the answer that made the evidence look STRONGER than it
   was.

**In five of the six, the fold went the permissive way** - where "permissive" includes flattering your
own evidence.

**What the mandated re-runs then found, and it is the strongest argument in this report for gating an
instrument before trusting it:** a mutant weakening an acknowledgement check from "the flag is TRUE"
to "the flag is PRESENT" **survived**. So a launcher answering `onlyIfEmpty:false` - stating plainly
that it had NOT guarded the restart - would have been accepted as a guarantee. **The epic's central
promise was reading its own refusal as consent.** It was invisible to every test and every review, and
only a mutation run under a compile gate found it. Report it: it is the reason every mutation claim on
the mission was voided and re-run, and the justification for the cost of doing so.

**THE CORRECT HANDLING WAS USUALLY ALREADY IN THE FILE.** This is the finding's sharpest edge and it
must be reported, because it changes what the fix has to be. Three times in one day the right pattern
sat adjacent to the wrong one, written by the same author:

- `ReadSessionCount` refuses to let an unreadable roster read as idle - **one method** from the
  locator resolution that folds unreadable into `NotRunning` (issue 2730).
- `SaveClaudeJson` declines to write when it cannot read - **eleven lines below** `SaveSettingsJson`,
  which overwrites and destroys the hooks (issue 2735). And its guard was written for a DIFFERENT
  reason - its comment says "Don't create .claude.json if it doesn't exist" - so the author reasoned
  about the ABSENT case in one method and about the UNREADABLE case in neither.
- A mutation harness given three verdicts by one seat while another seat's harness, written the same
  day, still had two.

**AND COVERAGE IS THINNEST EXACTLY WHERE THE DAMAGE IS GREATEST - structurally, not by accident.**
This is the mission's best predictor of where the next defect is, and it was verified three times for
three:

- Of the locator's two consumers, the TESTED one was `StopAsync` - the safe one, where a wrong answer
  costs nothing - and the UNTESTED one was `Start`, which launches a process. `StopAsync` is trivial
  to test: call it, assert nothing happened. `Start` starts a real process, which is expensive and
  awkward. **No test called `Start` on any branch**, which is why issue 2730 survived.
- **Not one test anywhere on origin/main names `ClaudeConfigDialog` or `SaveSettingsJson`** - the path
  that destroys the hooks carrying the fleet preamble (issue 2735). Reproducing it needs a locked or
  malformed file on disk, which is awkward to arrange.
- The mutation harnesses were verifying themselves, and nobody tests those at all.

**The hazard and the cost of testing it are correlated - and state it as a MECHANISM, not a
coincidence.** What is cheap to assert is what gets asserted, and side effects are what is expensive.
So coverage does not merely *happen* to miss the dangerous paths; it is pushed off them by the same
force that makes them dangerous.

**Two sharpenings from the sweep, both worse than the original claim and both worth reporting:**

- **The coverage collapses onto the pure functions.** `AutomationBrowserService` has exactly ONE
  behavioural test on main, and it tests `AccountUrl` - a pure string builder, five assertions on the
  one method that cannot break anything. `LaunchAsync`, `StopAsync`, `IsUpAsync`, `StatusAsync` and
  `RemoveAsync` - every method that touches a process - have ZERO. So it is not that the safe consumer
  was tested and the dangerous one was not; the tests gathered on the only thing with no side effects.
  Same shape again: `SessionHistoryStore` has ten test files while the three call sites that overwrite
  with it have none - the store is covered, its callers are not.
- **THE BOUNDARY IS WHERE THE CHEAP HALF OF THE STORY ENDS.** This is the most useful data point in the
  sweep and it comes from the case that breaks the pattern. `CodexHookInstallerTests` DOES test the
  failure branch - `EnsureInstalled_MalformedHooksJson_ReturnsFalse_AndDoesNotClobber` even asserts
  non-clobbering. The defect class handled correctly, WITH coverage. But the test stops exactly where
  the consequence starts: **nothing asserts what `SessionManager.CreateSession` does with that
  `false`**, which is launch the session anyway with no preamble. Producer failure is tested because it
  is cheap - write a malformed file, call a function. Consumer response is untested because it is
  expensive - launch a real session. **So a green suite that tests the producer reads as if the pair
  were covered.** That is narrower and worse than "the destructive path is untested".

**The practical consequence: hunt by what is hard to test, not by what looks wrong** - and check where
each test STOPS, not only whether one exists. Report "no test at all on the destructive path" and "the
test stops at the boundary" as findings in their own right, separate from the fold.

**But do NOT report the adjacency as the general case - the sweep checked and it does not hold.** Of
nine defects, exactly ONE had a clean correct sibling in the same file. The pattern is real and
striking where it occurs, and it is not the explanation.

**There are THREE categories, and the third one breaks the obvious remedy:**

1. **Enumeration gap** - the case was never considered. The correct handling sometimes sat a few lines
   away, written by the same author for a different reason.
2. **No vocabulary** - nothing in the type could express the third state, so it could not be recorded
   even once noticed.
3. **Fully considered, documented, and deliberately permissive - and still a defect.** The two hook
   installers state the fold and its consequence outright in their own doc comments: one says the
   caller launches the session without hook-based pointer tracking, the other that the session still
   launches, just without the preamble hook. Both run on EVERY session launch.

**Category three is the important one**, because it kills the obvious fix. "Make the unconsidered case
visible" cannot help where the case was considered and written down.

**And it is not a new class - it is the warning-wearing-a-note rule at the DESIGN level.** That doc
comment IS the note. The author saw the fold, understood the consequence precisely enough to write it
down, wrote it down, and shipped the fold - the same move as writing "never read this alone" instead
of splitting the field. So the answer to *visible to whom* is: **visible to the person the consequence
happens to, at the moment it happens.** Not in the source, where only the next author looks.

Report the concrete ruling that follows, because it is what the rule looks like when applied: **a
session that starts without its preamble is a session running WITHOUT THE STANDING RULES**, including
the no-attribution law, which is the owner's rule and not the fleet's to drop. It either refuses to
start, or starts visibly marked as running without its rules. "The session still starts" stops being a
reassurance and becomes the defect it always was.

Report all three categories with their counts; a report proposing "teach the pattern" would be
answering only the first.

**A sub-class worth its own line: an exemption whose justification is a claim about behaviour.**
`PrintBanAuditTests` lets a banned flag past, and its stated reason is that `ClaudeProcess.cs`
"launches every Claude Code session" - which the sweep showed is false. So the exemption rests on a
false premise and nothing ever re-derived it. **It reads as verified because it lives in a test.** An
exemption list justified by behavioural claims decays silently as the behaviour moves, and no test
fails when it does.

**THE MISSING STATE IS NOT ONE STATE - report this refinement, it was learned the hard way.** Phase 4
fixed the unknown-state fold, and its next review found the code still marked a seat `drained` when the
state line was **absent**. In its own words: *"I fixed the unknown state and left the missing one."*
So "I do not know" is at least four different answers, and fixing one does not fix the others:

- the value is **present and valid** - the ordinary case;
- the value is **present and unrecognised** - a newer build, a typo, a value from another version;
- the value is **absent** - nothing was written where something should be;
- the value is **unreadable** - it exists but could not be obtained (locked, corrupt, timed out, no
  answer).

Every one of these must reach the caller distinguishably, and each collapses into the permissive
branch independently. Fixing the second while leaving the third is the same defect wearing the same
coat, and it happened to a seat that had spent the day hunting exactly this.

State the rule so somebody who was never here can apply it: **when a result describes the world rather
than a decision, "I could not find out" is a real answer and it needs its own name. If it has no name
it is already inside one of the others - and it is inside the permissive one, because that is the
branch nobody writes a test for.**

Report the class sweep's count alongside this - how many other places in the codebase return an enum
where one value doubles as "could not determine" - **including if the answer is zero**.

**And report the sweep's own stated limit, which is larger than its findings.** Its 24-item shortlist
was built by matching caller NAMES against destructive-sounding verbs. **A consumer that writes or
launches under a name the pattern did not match was never shortlisted and has never been
consumer-checked.** That is the real outstanding set, it is bigger than the nine defects found, and it
is on `origin/main` in the merged inventory under a section titled "what this document does not know" -
placed first, before any count, so a reader meets the limits before the numbers.

### What the FIX looks like, done properly - report this too, not only the defects

A report that is all findings teaches nobody what to do. The Phase 0 seat produced the model, and it
should be shown as the worked example of the lead finding:

- **The harness was given a THIRD outcome.** `KILLED`, `SURVIVED`, **`BROKEN`** - and `BROKEN` is
  neither of the other two, so it cannot be read as either. That is the lead finding's fix applied to
  the instrument that exposed the lead finding.
- **The new gate was run against a KNOWN-BAD INPUT rather than trusted.** On the same non-compiling
  mutant, in one run, the new harness reported `BROKEN` while the old absence-based reading reported
  `SURVIVED` - the false result reproduced side by side with the correct one. That is how you prove an
  instrument, and it is the standard the rest of this report should be held to.
- **The mutations were re-derived against the CURRENT tree**, not re-run as originally written, because
  two later rounds had changed the code underneath the old list. A mutation list that describes a tree
  which no longer exists proves nothing. Result: 19 mutations, all 19 compiled, all 19 killed.
- **Its OTHER absence-based instruments were re-checked without being asked.** A reachability
  exoneration that rested on a grep returning zero hits was re-run with a control that must hit: 549
  control matches proved the grep actually reads those trees, so the zero means something. The
  attribution scan was fired at a planted bad line (2 hits) before its clean result on a 171252-byte
  diff was believed.
- **The harness and its mutation list are committed** at `scripts/mutation/launcher-update-mutations.py`
  so the next phase inherits the gate rather than the trap.

**The generalisation: a zero is only evidence once the instrument has been seen to produce a
non-zero.** Report every zero in this document with the control that proves the instrument fires.

### The second failure, and it is the OPPOSITE shape - report both, they need different fixes

Five seats and their Architect spent hours behind a continuous integration check that **a written rule
in this repository forbids waiting for**. `CLAUDE.md` section 5a says the gate is a green local run
plus a review by a different agent family, then merge, and that nothing waits on GitHub. Main turned
out to have no branch protection at all and no required status checks; every pull request was
`MERGEABLE` the whole time. It was found by querying the repository, not by re-reading the rule.

Everything else in the lead finding is **missing structure** - a state with no name, a fact with no
field. This one is the reverse: **the structure existed, it was written down, it was correct, and
nobody reached for it.** A missing third state is fixed by changing the type. **An unread rule is not
fixed by writing the rule more loudly** - that is a warning wearing a note, which this same mission
already ruled out as a fix.

**REPORT THIS AS ALREADY FIXED, AND DO NOT PRESENT THE REPORT AS THE FIX.** A finding written into a
report is another unread rule, so putting the remedy here would repeat the exact failure being
described. The remedy went where the action is:

- **Issue 2732** carries the analysis and the reworded rule.
- **`CLAUDE.md` section 5a** now reads as an imperative with a trigger: when the local gate is green
  and the review is in, RUN `gh pr merge` - do not open the pull request page first.
- **Every mandate on this mission** had its definition of done rewritten from a state ("merged on
  green") to an imperative with a trigger ("RUN `gh pr merge --squash --delete-branch`").
- The **blocked-ness query** is now a command anybody can run, rather than a judgement.

The rule that came out of it, and the one worth carrying past this epic: **A PROHIBITION ON AN ABSENCE
CANNOT FIRE.** "Never wait", "do not forget", "avoid" - none of them has a trigger, because the thing
they forbid is not an action anybody takes. Bolted to an action already being taken, they fire.
**That is the lead finding one level up, in us rather than in the code:** the missing state was "I am
blocked" versus "I am not done", folded into the one that feels permissive, which is to keep working -
and nobody tests that branch either.

One item is deliberately NOT fixed and is the owner's call, on 2732: **the .NET build keeps running on
these branches.** It covers what the local gate's two-minute budget skips, so switching it off would
trade a misleading spinner for a real hole. The right fix is making it complete inside the interval
between pushes - keeping the coverage and removing the lying signal.

Report that the Manager did not apply the rule either, for hours, while writing "merge on green" into
other seats' mandates. Having the rule, and even handing it to others, did not cause anyone to
evaluate their own trigger.

## What the report must contain

Written as a QA report, not a narrative. For each numbered step: what was expected, what was observed,
and the artefact that proves it.

- **The before picture.** Every session live at the moment of the request - id, name, mission, role,
  what it was doing, its branch, its uncommitted count. Everything else is measured against this list.
- **The approval.** That the request was created by a session key; that the direct restart route
  **still refused that same key**; and what the owner saw before accepting.
- **The drain.** Every handover, and the index. Which sessions were `drained`, `covered`, `blocked`.
  The secret sweep **with its control hit** - a clean sweep proves nothing until the sweep has been
  shown able to fail on a document you deliberately seeded.
- **The gap.** How long the Director was down. Measured, not estimated.
- **The after picture, against the before picture, session by session.** For each: restored, or
  deliberately not and why. A restored session must be shown **doing the work it was doing before, not
  merely alive** - quote the turn where it picks its own thread back up, and make it a fact that
  exists nowhere but its handover. "Summarise what you are working on" is the weakest possible check.
- **What was lost.** Every difference between before and after - background jobs that died, scratchpad
  files gone, anything a handover admitted it could not carry. **There WILL be some. A report claiming
  nothing was lost is a report that did not look.**
- **The blocked path, which this run is the FIRST EVER to exercise.** The wedged seat, what the drain
  did about it, and the fact that the restart did not happen. Say plainly that before this run nothing
  had ever refused to stop, so every earlier claim about never-force rested on a path no one had seen
  run. This is the single most valuable result in the report - do not bury it under the happy cycle.
- **What this run STILL did not exercise.** There will be something; name it rather than implying the
  run was exhaustive.
- **How we came to believe something untrue - report this, do not just report that it is fixed.** The
  sentence "a partly drained Director is a normal, recoverable state" was sitting in the workspace
  schema as a code comment **directly above the code that made it false**: an outcome list in which a
  blocked run had exactly one terminal state and no way to record that a recovery had happened.
  Everyone on this mission had read that sentence many times. Nobody noticed, and nobody would have,
  until a wedged session was scheduled to make it matter - it was found by reading the actual type
  definitions instead of the issue that described them. Say that plainly in the report. **It is the
  most useful thing this epic has learned about how we check our own claims**, and it generalises far
  past restarts: a claim written next to the code that contradicts it reads as documentation, not as a
  defect, and no test ever fails.

  **It happened TWICE in one day, which makes it a class and not an anecdote - report both.** The
  second: `LegacyWorkspaceImport`'s header said "The bytes stay on disk under the new name; nothing is
  deleted", and thirteen lines below it `RenameAside` did
  `if (File.Exists(target)) File.Delete(target)` - destroying a backup that could be the only surviving
  copy of a workspace somebody saved. Neither defect failed a test. Neither looked like a defect.
  Both were found by reading the code under a sentence rather than trusting the sentence. Every seat on
  this mission was then asked to grep its own diff for the safety claims it had written - what is
  preserved, what is never lost, what cannot happen, what is recoverable - and check each against the
  code underneath. **Report what that sweep found across the mission, including the seats that found
  nothing**, because a sweep nobody ran and a sweep that came back clean look identical in a report.

  **What the sweep actually found, to be reported as a count and not an anecdote:** the Phase 2 seat
  found four false claims in its own diff, one of which is a shipped fail-open now filed as issue
  2730 - the locator reports an unreadable registration as NOT RUNNING, so `onlyIfEmpty` permits a
  restart over a live Director. The Phase 0 seat found four more, three real, including a class
  comment asserting as plain fact the one invariant it documents everywhere else as never exercised on
  any machine. None of these failed a test. **Report the total and the shape**, not just the two the
  Architect happened to find by hand.

- **MERGEABLE and evidence-still-valid are two different facts, and only one has a badge.** A branch
  can be conflict-free against main while its green gate describes a tree that no longer exists,
  because the base moved underneath it. **A gate result is a claim about a specific tree and it
  expires the moment the base moves** - so a rebase is not a formality, it invalidates the evidence
  you were about to merge on. This was caught mid-mission and the missing step was added to every
  mandate: confirm the branch contains current origin/main, rebase if not, re-run the gate, then merge.
- **A seat caught itself about to commit the mission's own headline finding, and said so.** The sweep
  seat's first fail-before-the-fix mutation was `if (false)`. It did not compile - CS0162 - and the run
  exited NON-ZERO, which reads exactly like "tests failed, mutation killed". Banking that number would
  have been witness three reported and committed in the same act. It reported the near-miss instead,
  redid the mutation in a form that builds, and published both numbers - 5 of 12 killed, 7 correctly
  surviving because the untouched paths were untouched. **Report the near-miss, not just the fix:** a
  mission that catches itself is the only evidence that the discipline is real rather than recited.
- **THE GATE WAS TWO THINGS AND EVERYONE REPORTED THE FIRST.** Reviewed and not-reviewed collapsed
  into "gate green" - the lead finding applied to the mission's own process. It surfaced only when
  positive evidence was demanded instead of an assurance: who reviewed it, when, and where the output
  is, something openable. Report what that demand found:
  - **The merged bootstrap went in on half a gate.** Phase 0 (`4f742a4d3`) had exactly ONE Codex
    review, at 2026-09-06T23:47Z on commit `99b500252`, correctly framed as a fix round. Three
    commits went in after it - the rig's self-update, the four safety-sentence corrections, and the
    mutation COMPILE GATE - and the merge followed 19 minutes after the last. None was reviewed.
  - **The compile gate is the sharpest instance.** That unreviewed commit is the instrument that
    voided every mutation claim on the mission; every seat re-ran its evidence because of it, and
    then its replacement numbers were trusted. **An unreviewed instrument was used to invalidate
    everyone else's evidence.** A Codex seat was put on it afterwards, asked specifically whether the
    gate distinguishes three states or merely MOVES the two-way fold - because a gate that turns a
    non-compiling mutant into a different wrong answer is not a fix - and whether the safety sentences
    were fixed by correcting the CODE or by softening the SENTENCE, which are different fixes and only
    one is honest.
  - **Where the demand was answered properly it worked.** Phase 4 produced agent, version, session id,
    timestamp, a 184KB saved output, and the exact two commits reviewed - and volunteered that its own
    fix round was still unreviewed. That is the shape of an answer; "it was reviewed" is not.

- **OUR OWN LEAD FINDING TURNED UP INSIDE THE DRAIN WE BUILT TO FIX IT.** Phase 4's review returned 29
  findings, and the most serious was that an unknown or missing drain-report state fell into the
  permissive `drained` branch - **it would have closed a session on an answer nobody understood.**
  Also found: a re-read failure at close time that closed the seat anyway, destroying the only session
  that could rewrite the document; an amendment saying `blocked` ignored because only restore fields
  were re-applied; a refused deletion recorded as flagged; a session spawned after the capture present
  in no record and silently destroyed; a cycle in the reporting chain letting a whole ring be reaped;
  and a finding's excerpt able to publish a SECOND secret sitting on the same line. Report these: the
  team that spent a day cataloguing this defect class wrote a fresh instance of it into the fix.

- **THE MECHANICAL FORM OF THE DEFECT, and it is the most actionable thing in the report.** Phase 3's
  bounded check found one in its own diff: `if (stored.Origin != Captured) return` decided whether a
  captured workspace's seats were immutable. It is a NOT test against ONE known value, so **any origin
  it has never heard of falls through into the permissive branch** - including an origin written by a
  NEWER BUILD of this same software. Rewritten as `if (stored.Origin == Authored) return`, so anything
  that is not the one value known to be safe gets the strict treatment.

  Generalise it, because unlike "think carefully about states" this one can be searched for:
  **`!=` against a known-BAD value is fail-open by construction; `==` against the known-SAFE value is
  fail-closed.** The first says "everything I have not heard of is fine", the second says "everything I
  have not heard of is suspect". They read almost identically and differ only when the world grows a
  value you did not write - which is exactly what a future version of your own code does.

  Report the same seat's account of why the REST of its diff was clean, because it is the right shape
  for a negative result: the capture COPIES states rather than interpreting them, so an unrecognised
  role is carried through and never branched on; every enum read in validation is an allow list that
  REFUSES the unnamed value; the legacy import refuses a file WHOLE on any shape it does not
  understand. "Clean, and here is the mechanism that makes it clean" is evidence; "clean" is not.

- **A grep across a large tree is an instrument like any other, and a count from a name collision is a
  count from a broken one.** The sweep's first coverage number was wrong because a bare `StopAsync`
  matched 156 test files across unrelated types. It disambiguated rather than banking the figure, and
  the corrected answer was far worse than the wrong one. **This is the day's theme arriving a fourth
  time - inside the measurement of the day's theme.** Report it: every count in this document was
  produced by an instrument, and an instrument that has not been shown to distinguish what it claims
  to distinguish is not evidence.
- **A FIX ROUND THAT MOSTLY DID NOT FIX, and only a second review found out.** Phase 4's round-two
  review opened with: *of the nine named fixes, two are fully fixed, seven still have a residual
  path* - and returned 34 findings. Report that ratio, because it is the strongest number in the
  report for the rule that a fix round is new writing: **seven of nine fixes were believed complete by
  the author and were not.** Among them:
  - **a defect the fix round CREATED**: a senior's `covered:` claim was rejected only if the target had
    already been processed, not if the target's own document existed - so on the wrong iteration order
    a senior covers a worker whose own handover says `blocked`, that worker's document is never parsed,
    its questions are dropped, and it is closed as covered;
  - **a fail-open default written INTO the fix for a fail-open** - a missing read stamp returned
    permission to close;
  - a document read before its final block **latched as drained for ever**, so the seat could never
    converge even after finishing properly;
  - the closing message sent BEFORE the deletion flag, making the branch that claims "it has not been
    asked to close" false;
  - a refused close re-renaming and re-messaging a LIVE session every ten seconds for ninety minutes.

  The author's response is the right one and worth reporting as the lesson: it **restructured rather
  than patched** - the drain state can now only ever be set from a VALID declaration, which closes the
  latch, the missing state and the covers-without-a-declaration together, instead of fixing three
  symptoms.
- **The fix round is where the defects were - and the author caught itself.** Phase 2 counted THREE
  defects that its own change produced and had caught - a surviving acknowledgement mutant, four false safety sentences, and a
  regression it introduced while narrowing a catch during the day's fixes, which left field reads
  uncovered and would have turned a shrugged-at route into a 500. **All three were found after the
  code looked finished.** Report that: it is the strongest available argument for gating a fix round
  as hard as a first draft - and note the framing: this was the AUTHOR catching itself, not a reviewer
  catching an author, which is a stronger argument than any rule could be.
- **The six witnesses of the lead finding**, with what each one cost - see the top of this mandate.
  Note that they bit at three different ALTITUDES: in the code (the locator), in the process (the
  sweep), and in the instrument itself (the mutation harness). Report the re-run counts from the
  voided mutation audits, including any mutants that turn out never to have compiled.

- **A TEST THAT ENCODES AN UNANSWERED QUESTION CAN BE SILENCED BY ANYONE ABLE TO MAKE IT GREEN.**
  Report this beside the unread-rule finding - **they are a pair: one is a rule with no trigger, the
  other is a trigger with no owner.** `ContextLessRouteCensusTests` goes red so a new context-less
  Gateway route cannot appear without a WRITTEN tenant-confinement verdict. Phase 3's new workspace
  route made it red; Phase 4, a different seat that had added no route at all, could have made it
  green in ten minutes by adding the census rows - and the verdict would never have been written. The
  test would have been satisfied while its entire reason was skipped. **The structure can enforce that
  somebody did something; it cannot enforce WHO answers.** That is this mission's defect shape with
  the fold in the human layer: satisfying the mechanism read as making the decision.

- **A MUTATION ARTIFACT WAS COMMITTED INTO THE PRODUCT, and the check meant to prevent it failed in
  the mission's own defect shape.** Phase 2's third review round found `if (false && onlyIfEmptyFromBody
  && verb != restart)` committed in the route guard - so `onlyIfEmpty` could be sent with `stop` or
  `start` and the route would RELAY it instead of refusing. A second artifact, uncommitted, passed a
  hard-coded `false` to the relay in place of the flag, **disabling the entire feature at the one seam
  a caller cannot see.**

  The cause is the report's own headline: the harness edits files in place, and the pre-commit check
  grepped for **a list of mutation strings the author happened to think of** - a check whose pass
  condition is an ABSENCE. It passed over the second artifact twice. Replaced with the positive form -
  every guard line asserted PRESENT, verbatim - it found that artifact immediately. **That is the lead
  finding appearing in a seat's own tooling, for the second time in one day, after that seat had spent
  the day hunting it.** Report it beside the drain instance: two seats, both forewarned, both produced
  a fresh instance in the tools they built to avoid it.

- **AN UNCHANGED VERDICT AFTER FIXING AN INSTRUMENT IS NOT EVIDENCE THE INSTRUMENT DID NOT MATTER.**
  Lead the instrument section with this sentence. The Manager was asked, when the mutation harness was
  fixed, to report whether the numbers changed - and told that an unchanged number would be worth as
  much as a changed one. That was too simple, and the run answered better than the question: the
  verdicts were identical, and **every test count DOUBLED**, because the old code read one of two
  target frameworks. The earlier run had judged on half the evidence; the verdicts agreeing was luck.
  Had the failing test been in the unread half, the verdict would have been wrong and nothing would
  have looked different.

- **THE INSTRUMENT WE USED TO INVALIDATE EVERYONE'S EVIDENCE WAS ITSELF DEFECTIVE - AND SO WAS ITS
  REPLACEMENT.** This is the sharpest single item in the report and it must not be softened. An
  independent Codex review of the merged bootstrap returned FAIL with three P1 findings against the
  mutation compile gate:
  - it hardcoded a path to a disposed worktree, so it could not run from the repository that launched
    it - and had that worktree merely been STALE rather than absent, it would have silently mutated
    and tested a different checkout and emitted plausible evidence about the wrong tree, recording no
    commit or tree identity anywhere;
  - **it captured the test exit code and never read it**, inferring the verdict from the presence of a
    summary token and the ABSENCE of a `[FAIL]` line - so a failing test invocation could be recorded
    as `SURVIVED`. The compile failure was classified `BROKEN`, but a broken test INVOCATION still
    folded into a verdict. **The gate moved the fold rather than removing it;**
  - **no bad verdict failed the executable gate.** The script exited 0 on `SURVIVED`, `BROKEN` and
    anchor-missing alike. A gate whose failure states do not fail is a report, not a gate.

  And the replacement harness, written the same day by a different seat and used by every seat to
  re-run its voided evidence, **had two of the same three**: it read the build exit code but never the
  test exit code, took the FIRST count line so a multi-target run could read a passing target while
  another failed, and had no exit code at all. **Report that the correction was itself uncorrected.**

  **THEN THE FIXED RUNNER PRODUCED THE SAME VERDICTS - AND THAT IS THE MOST INSTRUCTIVE RESULT IN THE
  REPORT.** Re-running the same ten mutants under the corrected harness gave an identical 7 killed, 1
  survived, 2 broken. But **every test count DOUBLED** - 24 where the old reading saw 11 - because the
  project has two target frameworks and the old code read only the first summary. So the previous run
  had been judging on **half the evidence**, and the verdicts coming out the same was **luck, not
  confirmation**. Report it in those words: an unchanged number after fixing an instrument is not
  reassurance that the instrument did not matter. Had the failing test been in the unread half, the
  verdict would have been wrong and nothing would have looked different.

- **A WARNING IN A DOCUMENT IS A DESIGN DEFECT WEARING A NOTE.** Report this as its own rule, because
  it is the most useful thing this mission produced and it generalises far past restarts. The Manager
  proposed *deriving* the welded outcome field rather than removing it - which fixes the risk of two
  fields disagreeing but leaves the ambiguity sitting inside one - and then wrote warnings into two
  mandates telling future readers never to trust that field alone. That was an hour after agreeing
  that welding two independent facts into one token was the defect. **When you catch yourself writing
  "never read this alone", "be careful not to", "remember that this does not mean" - that sentence is
  a report FROM THE DESIGN that it is wrong, and the warning is the cheapest available way to not fix
  it.** The test: *if the fix requires people who were not in the conversation to remember something
  forever, it is not a fix.* Put it beside the false safety sentences, because they are the same
  failure from opposite ends - one is prose that contradicts the code, the other is prose that props
  the code up. Both are prose doing a job that structure should be doing.

## The rule that makes it a QA report and not a demo

**Run the negative cases in the same pass, or the positives mean nothing.** Each must be shown FAILING
CORRECTLY in the same report:

- ask for a restart on a machine that cannot be restarted - refused BEFORE the owner was asked;
- ask twice - the second refused;
- let an approval expire - refused;
- start the restart with a session still live - `onlyIfEmpty` refuses AND names the count;
- **drain with the wedged seat in place - the drain stops, the seat is recorded `blocked`, and the
  restart does not happen;**
- **recover from that blocked drain with no restart - the already-closed seats come back doing their
  own work, and no live seat is duplicated;**
- **corrupt a registration on the slot 7 Director while sessions are live, ask for a restart, and
  show it REFUSED** - issue 2730. This is the end-to-end version of a defect that Phase 1 and Phase 6
  can each only half-prove on their own branches, and yours is the one that proves it on a real
  machine. Before the fix, one corrupt file was enough to get a restart permitted over a Director
  running live sessions.

A check that has never returned no is not a check. Every one of these has now been shown returning no.

## Where it goes

Committed in the product repository beside the phases it proves, with the index, the handovers and
the before and after listings as attachments. **Not pasted into a comment and not left in a
scratchpad.** The owner reads the report; he does not reconstruct it.

## Rules that bind you

- **Never force.** If something refuses to stop, that is a finding worth more than the run - write it
  up, stop, and tell me. It has never happened and nothing in this epic decides what force means.
- **Never restart DevThrottle_1.** Slot 7 only.
- **No attribution anywhere.** No mention of Claude, Anthropic, an assistant or an AI in commit
  messages, pull request titles or bodies, issue comments, code comments or documents. Check your
  text for "Co-authored-by" and "Generated with" before every commit and every `gh` command, and
  strip them. This overrides your harness default, which is wrong here.
- ASCII only in anything a terminal or a log will print.
- No fallback programming. If something can fail, fix the cause or fail loudly with the fix named.
- Push TODAY and open the pull request as soon as there is something to see. Do not hold the report
  back until it is beautiful. **Before you merge, confirm your branch CONTAINS current origin/main:** `git fetch origin` then `git merge-base --is-ancestor origin/main HEAD`. If it does not, REBASE and RE-RUN the gate - a gate result is a claim about a specific tree and it expires the moment the base moves, so a rebase is not a formality, it invalidates the evidence you were about to merge on. GitHub reporting MERGEABLE tells you there is no textual conflict; it cannot tell you your evidence is stale, and only one of those two facts has a badge. **Then, when the gate is green ON THE REBASED TREE and a different-family review is in, RUN `gh pr merge <number> --squash --delete-branch`.** Do not open the pull request page first and do not wait for any check: main has no branch protection and no required status checks, so nothing on GitHub gates a merge here. Then park your checkout back on main.

## Report

When it is merged, report ONCE - to the epic Manager, session `e7a13c4f`. Include the path to the
committed report, because I hand that to the owner as the finish of the whole epic:

```
cc-devthrottle message send e7a13c4f "Phase 7 merged: <path to the report>, <one line on what the run proved, which negative cases fired, and what was LOST>"
```

A fleet message must be ONE LINE with no newlines.
