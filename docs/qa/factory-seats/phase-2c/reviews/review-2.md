# Review 2 - Factory Seats phase 2c: the fixes for review 1

Reviewed 13 October 2026, on the branch `factory-seats/review-2c-fixes`, cut from
`origin/factory-seats/phase-2c-gateway-opens-runs`. The Reviewer was opened by the Implementation
Lead and read only; nothing was changed but this file. This is the second review of the phase and it
is narrow: only commit `e5afe1dbe` (the answer to review 1) was read, not the rest of the branch.

## Verdict

Both fixes do what the answer to review 1 says, and neither disturbs any path it must not disturb.
No findings are returned.

## Scope

**What I read.** The whole of commit `e5afe1dbe`: the try around the seat and brief read in
`DirectorCronSessionStarter.StartRunAsync` and its new `BriefNotRead` constant, the one new block in
`FactoryTalkSeed.Compose`, and the three tests the commit added. To judge the four questions the task
set, I also read the code each change touches: `CronEngine.FireAsync` and `EvaluateDueAsync` (what
becomes of a start that reports an error, and what became of one that threw before), the production
wiring of the seam in `GatewayHost` (the delegate is `ScheduledRunOpening.SeatFor` with the registry
find and the brief store), `ScheduledRunOpening.SeatFor` and `PromptFor` (untouched by this commit),
and the boss role refusal in `FactoryRegistryStore` (lines 397 to 401). The answer to review 1
(`answer-1.md`) was read as the developer's claim and checked against the code, not trusted.

**What I ran.** From the worktree, on Windows, the three tests the commit added and nothing else:
`TheBriefCannotBeRead_TheRunIsNotStarted_AndIsRecordedAsARunThatDidNotRunWithTheReason`,
`TheBriefCanBeReadAgain_TheNextRunStartsWithItsBrief`, and
`Compose_AFactoryWhoseBossIsStoredWithAnotherRoleWord_IsToldToRegisterTheBossAsBoss`. All three
passed. The whole suites were not run, as the task said.

**What I could not reach.** A live hosted Gateway: the fix is proven by the tests through the real
engine, starter, registry and stores, not by a fired run on a live machine. No agent was given the
talk's new line, so how a session reads it is untested - the same limit review 1 recorded.

## Findings

None. The four places the task told me to look hardest, each answered:

**Does the new try swallow anything it should not?** No. The delegate it wraps is the one seam whose
written contract is to report a failed start rather than throw, and the catch hides nothing: the
failure's own words go into the returned reason, which the engine stamps onto the run row, and the
whole exception goes to the log. Before this commit an exception there was caught by the engine's
timer boundary, which wrote a log line and nothing else - no run row, nothing on any screen, and the
schedule left due to fail the same way on every sweep. The change makes the failure MORE visible, not
less: a real defect now lands on the factory's problems list and the notify email with its reason,
where before only the log file knew. And no run can now start without its brief: the catch returns a
failure, it never falls back to a briefless start; the only briefless starts remain the designed ones
(a schedule in no factory, a seat with no published brief), which this commit did not touch. One
deliberate behaviour change is worth naming so the Lead knows it chose it: a transient failure now
moves the schedule on and that occurrence is recorded as did-not-run, where a throw used to leave the
job due so the next sweep retried it. That matches how every other start failure is treated (the
refusal, the spawner failure), and it is what the answer says it does.

**Is the not-started row written through the same path every other failed start uses?** Yes. The
starter returns `(null session id, null director id, the reason, no brief version)`, and
`CronEngine.FireAsync` has exactly one place a failed start goes: the record is built with
`InfraStatus` `not-started`, the result `problem`, the kind `did-not-run` and the reason, appended to
the run history, the schedule moved on, and `_onResultRecorded` - which production wires to
`CronRunResultService.Announce`, the factory's problems and activity - is invoked, as is the
notify-on-failure path. It is the identical code the refusal and the spawner failure flow through;
there is no second path. The test proves the row lands by reading it back through a fresh
`CronRunHistoryStore`.

**A schedule in no factory, and a seat with no published brief: still byte for byte as before, and
never slowed or failed by the new read?** Yes. `ScheduledRunOpening.SeatFor` returns null at the
factory-link guard before any registry or brief read, so a schedule in no factory reaches nothing new;
the try around the seam adds no work to that path. `PromptFor` and `SeatFor` are untouched by this
commit, so the phase's own byte-for-byte pins still hold. A seat with no published brief is the
designed briefless state and is started exactly as before; the new catch changes only what happens
when the read THROWS, which is a database failure, not the absence of a brief.

**The talk's new words: only to a factory whose stored boss role is not Boss, and every other
factory's words unchanged?** Yes. The condition is `boss is not null and the stored role is not
"Boss"` compared ordinally - exactly the condition of the registration refusal in
`FactoryRegistryStore` (also ordinal), so the line is given to exactly the factories whose
re-registration would be refused, and only those. A factory whose boss's role is `Boss` and a factory
with no boss append nothing, and the new test proves the equality: the corrected factory's talk, with
the one line taken back out, is identical to the same factory with the role `Boss`. A dangling boss
reference (the registry names a boss seat the factory does not have) produces no line and no crash.

## Observations, not findings

- The recorded reason reads "it did not start a session: not started: the seat's brief could not be
  read, and a run is never started without its brief: ...". "Not started" appears twice because the
  engine's wrapper and the starter's constant both say it. This matches the wording the refusal path
  already produced before this phase, so it is consistent, but if the reason is ever reworded, the
  double phrase is the place to start.
- The `BriefNotRead` constant's own doc summary now sits between the two `<param>` comments of the
  constructor, so those two parameter comments and the constant's summary form one doc comment on the
  constant and the constructor is left without its parameter documentation. Cosmetic only; the code
  and behaviour are correct.
