# Answer to review 1 - Factory Seats phase 2c

Answered 13 October 2026 by the Developer who built the phase, on the branch
`factory-seats/phase-2c-gateway-opens-runs`. Both findings are accepted and both are changed in code.

## Finding 1 (medium): a failure reading the brief at fire time records nothing - ACCEPTED

The reviewer is right, and the harm is as described: the read was new in the fire path, it could throw straight
through a seam whose contract is to report a start that failed, and a factory would have stopped running with no
run row.

**What changed.** `DirectorCronSessionStarter.StartRunAsync` now reads the seat and its brief inside a try. A
failure is logged in full and RETURNED as the start's error:
`not started: the seat's brief could not be read, and a run is never started without its brief: <the failure's own
words>`. The engine then does what it does for every start that failed: it records a run with `InfraStatus`
`not-started`, the result `problem`, the kind `did-not-run` and that reason, moves the schedule on, and tells the
factory. Nothing is handed to a Director, so no run starts without its brief.

**Why a catch in a method that is not an entry point.** The coding style puts try and catch at entry points only.
This is the one seam whose written contract is the opposite of throwing ("it reports the error so the engine
records a not-started run"), and the catch does not hide the failure: it turns it into the recorded reason and
writes the whole exception to the log.

**The tests**, both through the real engine, starter, registry and stores:

- `TheBriefCannotBeRead_TheRunIsNotStarted_AndIsRecordedAsARunThatDidNotRunWithTheReason` - the brief read throws
  "the database is locked". Nothing was handed to the Director. The run row is read back from the database by
  another store: no session, `not-started`, `problem`, `did-not-run`, and the reason
  `it did not start a session: not started: the seat's brief could not be read, and a run is never started without
  its brief: the database is locked`. The schedule's next time moved on.
- `TheBriefCanBeReadAgain_TheNextRunStartsWithItsBrief` - after the failure clears, the next run is given version
  1 and records it, and both runs are in the history.

**What was not changed.** The reviewer notes the same hole exists for the registry read inside the refusal check,
which is older than this phase. It is left as it is: it is outside this phase's task, and the Implementation Lead
can decide whether it wants its own change.

## Finding 2 (low): a goal change in a talk cannot complete for a factory whose boss is stored with another role word - ACCEPTED

The refusal stays; it is the owner's decision D4. The talk's own instruction now says what the refusal would
otherwise be the first to say.

**What changed.** In `FactoryTalkSeed.Compose`, the goal-change step gains one line when, and only when, the
factory's boss seat is stored with a role that is not `Boss`:

    - in that manifest, set the role of the boss seat <id> to "Boss". It is registered today as "<stored role>",
      and the boss's role is Boss: a registration that gives the boss any other role word is refused, and the goal
      would not be registered.

It is said to every seat of such a factory, because any seat's talk may change the goal. A factory whose boss's
role is `Boss`, and a factory with no boss, are given exactly the words they were given before; the three pins of
the words from before this phase still pass unchanged.

**The test.** `Compose_AFactoryWhoseBossIsStoredWithAnotherRoleWord_IsToldToRegisterTheBossAsBoss_SoAGoalChangeCanComplete`:
the line is in the boss's talk and in another seat's talk, it is the one thing that differs from the same factory
with the role `Boss`, and a factory with no boss is not told it.

**What this does not do.** It does not change a stored role by itself. A factory stored with another role word
keeps it until a person or a talk registers it again.

## The reviewer's observations

- The talk's version is self-reported by the session on its `talked` line. Agreed, and it stays decision 1 in the
  proof for the Implementation Lead to keep or replace.
- The other two observations need no change.
