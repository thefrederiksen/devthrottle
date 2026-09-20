# Review - Smart Director Restart, phase 3, Reviewer 2: the ANSWERS to review 1

Written by the second Reviewer seat opened by the phase 3 Tech Lead (session 38f41a97). I did not write
this code and I run a different agent from the Developer that did. I changed no product code; the one
mutation I made is described below and was put back with `git checkout --`.

Reviewed: the answers to the four findings of review 1. That is, everything between `0cda45788` (the
commit review 1 read) and `238d815f6` (the answers file), which is one code commit (`f9443652e`) plus the
answers document. Worktree `D:/ReposFred/devthrottle-smart-restart-p3-review2`, detached at `238d815f6`.
Review 1's own document lives on the branch `smart-restart/p3-review-1` at commit `df5ccff0d`; it is not in
this tree at this commit, so I read it with `git show df5ccff0d:...`.

---

## 1. Scope

What I read, in the order the mandate gave:

- Review 1 in full (`review-phase-3-1.md` at `df5ccff0d`), including its section 5, the things it attacked
  and failed to break. I checked each of those against the new code.
- The Tech Lead's ruling on each finding (`mandate-phase-3-developer-engine-findings.md`, read from the
  Tech Lead's tree, which is the only place it exists).
- The Developer's answers (`review-phase-3-1-answers.md`), treated as claims to disprove.
- The whole diff `0cda45788..238d815f6`: twenty files, all of them read in the diff and the changed ones
  read in full in the tree.
- The code the answers build on, to check the claims rather than trust them: `DirectorRestore.StillRunning`
  and `TimedOut` in `DirectorRestore.cs`; `GatewayClientRestoreGateway.GetRosterAsync` (to check the claimed
  parity with the new `GatewayClientWayUp.GetRosterAsync`); the roster use in `DirectorRestore.RestoreAsync`;
  every one of the eight agent launch paths (`ClaudeAgent`, `PiAgent`, `CopilotAgent`, `CursorAgent`,
  `CodexAgent` and `CodexDriver`, `GeminiAgent`, `GrokAgent`, `OpenCodeAgent`); `AgentPluginRegistry`
  (what `All` holds, that external plugins are included, that Raw CLI is not a plugin); the whole of
  `DirectorWayUp.cs`, `WayUpWords.cs`, `IDirectorWayUp.cs` and `IWayUpGateway.cs` at this commit; and the
  four test files plus the rig.
- `ControlApiHost.CreateDirectorWayUp`, because finding 1 part two is state inside the engine, and the
  lifetime of the engine decides whether that state means anything.

What I did NOT look at: the phase 1 interface document, the phase 2 window code, anything in
`src/CcDirector.Avalonia`, the rig scripts, the rest of the mission document beyond what review 1 and the
ruling quote, and anything outside the diff between the two commits. I did not re-review the way up engine
as a whole - review 1 did that, and the mandate forbids repeating it.

What I ran, all in the foreground, no command over nine minutes:

1. `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
2. `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin|FullyQualifiedName~Agent"`
3. `dotnet build cc-director.sln`
4. The Gateway check again with my own mutation (section 3), and again after putting it back, on a full
   build.

## 2. The counts, from my own runs

On the untouched commit `238d815f6`, full builds:

- Gateway check: `Passed! - Failed: 0, Passed: 580, Skipped: 0, Total: 580`. **580 total, 580 passed,
  0 failed.** Matches the Tech Lead.
- Agent plugin check: `Passed! - Failed: 0, Passed: 247, Skipped: 0, Total: 247`. **247 total, 247 passed,
  0 failed.** Matches the Tech Lead.
- Whole solution: `Build succeeded. 0 Warning(s) 0 Error(s)`.

Both runs reached their end (each printed its final count line, and the run was not aborted). The known
intermittent test `SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state`
PASSED in my run; I never saw it fail, and I do not report it.

I also counted the rise myself: 580 minus 567 is 13, and I counted 13 new test cases in the diff (5 in
`DirectorWayUpOfferTests`, 2 in `DirectorWayUpBringBackTests`, 6 in `DirectorWayUpHistoryTests`). 247 minus
238 is 9, and I counted 9 in the diff (1 walk test plus an 8-case theory). The numbers agree with the
answers file.

## 3. My own revert proof

I chose the one fix the Developer did NOT mutation-test: the finding 4 guard, `DirectorWayUp.NotThisDirectors`
(`DirectorWayUp.cs` line 657). The Developer's three mutations covered findings 1 and 2 only, so the
finding 4 tests had never been shown able to fail.

I replaced the two comparisons with comparisons of each value against itself, so the guard always answers
null while the code still compiles:

    if (!string.Equals(doc.Machine, doc.Machine, StringComparison.OrdinalIgnoreCase))
    ...
    if (!string.Equals(doc.DirectorName, doc.DirectorName, StringComparison.OrdinalIgnoreCase))

One honest note first: my first attempt used `if (false)`, and the build REFUSED it - CS0162, unreachable
code, treated as an error in this project. That run produced a build failure and no test counts, so it
proves nothing and I do not count it. The compilable mutation above is the proof.

The red run, full build:

    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpBringBackTests.Bringing_back_another_directors_record_is_refused_by_name_and_the_restore_is_never_called
    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpBringBackTests.Bringing_back_a_record_from_another_machine_is_refused_by_name
    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpHistoryTests.Reopening_from_another_directors_record_is_refused_by_name
    Failed!  - Failed:     3, Passed:   577, Skipped:     0, Total:   580

**3 failed, 577 passed, 580 total.** All three finding 4 tests noticed, one per refusal the guard makes,
and nothing else moved - so those three, and only those three, hold the guard.

I put the source back with `git checkout --` (the commit was already in place, so the restore could not eat
it), `git status --short` and `git diff HEAD --stat` both printed nothing, and I touched the engine file to
force a recompile. The green run printed `CcDirector.ControlApi -> ...dll` and
`CcDirector.Gateway.UnitTests -> ...dll`, so the green is the restored SOURCE and not a leftover binary:

    Passed!  - Failed:     0, Passed:   580, Skipped:     0, Total:   580

**0 failed, 580 passed, 580 total**, full build.

## 4. The four questions

### Question 1 - is each finding actually fixed, or only made harder to see?

**Finding 1, part one - fixed, and by the product's own rule rather than a copy of it.**
`ReopenAsync` (`DirectorWayUp.cs` lines 231 to 234) calls `_gateway.GetRosterAsync` and hands the answer
straight to `DirectorRestore.StillRunning(seat, roster)` (`DirectorRestore.cs` line 506), refusing with that
reason when it answers one. I read `StillRunning`: it covers both halves - a seat the roster shows running on
a reachable Director, and a seat still listed under a Director nobody can reach that the drain never
recorded closed. The seam method `GatewayClientWayUp.GetRosterAsync` is word for word the same four lines as
`GatewayClientRestoreGateway.GetRosterAsync` (I read both), returns the envelope with per-Director
reachability, and the restore has been reading that same call in production. The order is right: the roster
check comes before the claim, so a refused-for-running seat does not burn its one reopen. Not made harder
to see - the reason in the refusal is the rule's own sentence.

**Finding 1, part two - fixed in this process, with one contingent weakness that is my Finding 1 below.**
The claim is taken under a lock before the start leaves (`ClaimReopen`, `DirectorWayUp.cs` line 675), keyed on
the record id and the seat's captured session id, and a second attempt is refused with a sentence of the
engine. The two racing clicks cannot both pass it. The weakness is the instance lifetime, which nothing
states.

**Finding 2 - fixed at the cause.** `WayUpWords.Resumes` (`WayUpWords.cs` line 181) holds no agent names any
more; it reads `CanResumeSavedConversation` off the plugin the registry finds, and `AgentName` reads the
display name from the same lookup, so both hand-kept lists are gone. An agent the registry does not know
still answers false and falls to the fresh-session wording, and the invented-agent test from review 1 is
still there and still passes. Not made harder to see.

**Finding 3 - fixed.** `BuildHistoryEntry` (`DirectorWayUp.cs` line 372) gives every seat that ended without
a handover the same `WayUpReopenOffer` the rows carry, computed by the same one method
(`WayUpWords.ReopenOffer(seat.Agent, seat.ClaudeSessionId)` - the identical call the rows make), whether or
not the record owes a seat. The start-up presence check is untouched: `IsOfferable` still requires a seat
decided restore, and the new test asserts BOTH halves in one place and then actually reopens from such a
record. Seats that handed over or came back carry null, with a test. A window no longer has to invent the
sentence.

**Finding 4 - fixed, one rule in one place, and my mutation proved its tests bite.** `NotThisDirectors`
checks the machine and the Director display name, the same two things the read paths require, matched the
same way (`OrdinalIgnoreCase`). It refuses before the bring back chooses seats and before the reopen looks
for one. A Director with no display name throws inside `DirectorName()` and is caught by the entry point, so
it refuses rather than matching on a blank - the read-path behaviour review 1 proved sound is preserved.

### Question 2 - did a fix break something review 1 proved sound?

The one thing review 1 held structurally was the start-up presence check never asking what is running. The
seam has gained `GetRosterAsync`, so the structural proof is gone - by the Tech Lead's own ruling, which is
what makes this question worth asking. What holds the rule now:

- **In the code:** only `ReopenAsync` calls `GetRosterAsync`. `FindOfferAsync`, `ReadHistoryAsync` and
  `BringBackAsync` never touch it - I read all four.
- **In the tests:** one assertion, `Assert.Equal(0, rig.Gateway.RosterAsked)` at
  `DirectorWayUpOfferTests.cs` line 121, inside `A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_
  about_running_sessions`. I grepped the whole suite: it is the only assertion on `RosterAsked` anywhere.

That is thinner than the structural proof it replaces, and it is my Finding 2 below, mostly because the
test's own summary sentence now states the opposite of the truth. The seed file, the bring-back guards, the
refusal-versus-emptiness wording and the write-nothing-on-refusal behaviour that review 1 proved sound are
all untouched by this diff - I checked each changed file against that list and none of them moved.

### Question 3 - the eight values, the walk, and a ninth agent

I read every one of the eight drivers myself, not the table in the answers, and every flag is right:

| Agent | What its launch path really does with the id | Flag |
|---|---|---|
| Claude Code | `ClaudeAgent.cs` line 39: `--resume {id}` | true - right |
| Pi | `PiAgent.cs` line 43: `--session-id {id}`, comment records pi 0.80.10 recalling the first conversation | true - right |
| Copilot | `CopilotAgent.cs` line 59: `--resume {id}` | true - right |
| Cursor | `CursorAgent.cs` line 53: `--resume="{id}"` | true - right |
| Codex | `CodexAgent` delegates to `CodexDriver.BuildLaunchSpec`, which LOGS "ignoring resume" and builds nothing from it (`CodexDriver.cs` lines 78 and 79) | false - right |
| Gemini | `GeminiAgent.cs` line 34: LOGS "ignoring resume" | false - right |
| Grok | `GrokAgent.cs` line 34: LOGS "ignoring resume" | false - right |
| opencode | `OpenCodeAgent.cs` line 35: LOGS "ignoring resume" | false - right |

The flag is a REQUIRED positional part of `AgentPluginLaunchMetadata` - a plugin that leaves it out does not
compile, and the Developer's own test double in `AgentPluginLoaderTests` had to be updated for exactly that
reason, which is the guarantee observed working.

**Does the walk really walk every agent?** Yes. `AgentPluginRegistry.All` is the built-ins plus every
validated external plugin, and the walk iterates all of it, building each one's real launch spec through
the plugin's own `CreateAgent`. It is not a list of the eight.

**Can it pass vacuously?** Only two ways, and both are shut. A registry that answered with nothing is caught
by the `>= 8` assertion with a message saying the instrument is broken. A world where every agent declared
false and no driver built the id is caught by `Each_built_in_agent_declares_the_resume_support_its_driver_has`,
which names all eight expected values - I checked it names eight, four true and four false.

**Would a ninth agent added tomorrow be safe?** Yes, with one direction of lie to watch. A ninth plugin must
state the flag or not compile, and the walk then checks its flag against what its own driver really builds,
so it cannot drift silently. The walk's proxy is "the conversation id appears in the launch arguments",
which the ruling itself chose. For every agent today that proxy is exact. For a hypothetical future agent
that carries the id in its arguments for some purpose OTHER than resuming - a log file name, say - the walk
would force the flag true and the wording would promise a conversation that does not come back. That is the
one direction the proxy can lie in, and it is a note for whoever adds such an agent, not a defect today.

### Question 4 - what is left open

The answers admit two: the same seat can be reopened twice across a Director restart, because nothing is
written onto the record; and a reopen whose start fails keeps its claim, so it cannot be retried until this
Director restarts. **I looked for a third and found the instance-lifetime dependency instead, which is my
Finding 1 below - it is a condition under which the second admitted gap's in-process guard does not even
hold inside one Director run.**

Are the two stated where a reader will find them? The cross-restart gap yes, three times: the comment on
`_reopened` (`DirectorWayUp.cs` lines 37 to 47), the `ReopenAsync` documentation on `IDirectorWayUp.cs`
lines 67 to 70, and the answers file. The failed-start-keeps-claim trade is in the code comment
(`DirectorWayUp.cs` lines 249 to 251) and the answers file, but NOT on the interface documentation - a
window Developer reading `IDirectorWayUp` alone learns the once-only rule and the cross-restart gap and
never learns that a failed start burns the seat's one chance. That is part of my Finding 4 below.

Is the trade the right way round? Yes, both of them. Keeping the claim on a failed start copies
`DirectorRestore.TimedOut`'s reasoning exactly: a start whose answer never came back may have happened
anyway, and giving the claim back would sell the owner a retry that is sometimes the second agent the guard
exists to prevent. The refusal tells him to look in the session list, which is the honest next step. And the
cross-restart gap genuinely needs a mark on the record, which needs the restore lease, a contract change and
a Gateway deploy - the Delivery Lead's decision, correctly not taken by the Developer.

## 5. Findings, ranked

### Finding 1 - the once-only guard is state inside the engine, and the only factory makes a new engine on every call

`DirectorWayUp._reopened` (`DirectorWayUp.cs` line 49) is an instance field. `ControlApiHost.CreateDirectorWayUp`
(`ControlApiHost.cs` line 258) constructs a NEW `DirectorWayUp` on every call, and its own documentation
sells exactly that freshness as a virtue: "The engine holds NO Gateway client and NO display name of its
own. Both are read at the moment of each call." Nothing in the interface documentation, the factory, the
field comment or the answers file says that the once-only reopen guard only works if the CALLER holds one
instance for the Director's lifetime. Even the test rig invites the wrong pattern: `WayUpTestRig.WayUp()`
(`WayUpTestRig.cs` line 35) builds a fresh engine per call, and the twice-test only passes because it
deliberately captures one instance in a local.

Worse, the roster guard will NOT catch what the claim guard misses. `StillRunning` asks about the seat's
CAPTURED session id. A reopened session comes back under a NEW session id - the Gateway assigns it, and
`BuildReopen` hands over only the conversation id - so moments after a successful reopen, the roster still
says nothing about the captured id and the second reopen sails through the still-running check. A caller
doing the natural thing, one fresh engine per action or per screen, therefore gets two live agents in one
saved conversation: exactly the harm finding 1 exists to prevent, on the exact path it was fixed on.

What it would do to a person: a double click in the window phase, with a per-call engine behind it, starts
two agents that both believe they are the same session and write into one transcript.

How sure I am: the mechanics are certain - I read the factory, the field, the rig and `StillRunning`. The
trigger is prospective: no caller exists yet, and the window Developer is building against this surface
right now. That is precisely why it is worth saying now rather than after the windows exist.

This is not disobedience: the ruling ordered "keep a once-only guard inside the engine", and the Developer
did exactly that. But a guard whose guarantee silently depends on how the next caller constructs the object
is a fix with a tripwire in front of it, and nothing warns the caller. The cheapest closes: say it on
`IDirectorWayUp.ReopenAsync` and on the factory ("hold ONE engine for the Director's lifetime - the reopen
claim is engine state"), or hold the claim somewhere that survives a new engine. Which one is the Tech
Lead's call.

### Finding 2 - the "never asks what is running" rule is now held by one count in one test, whose own summary states the opposite

The structural proof is gone by design - the ruling added the roster question to the seam. What holds the
rule now is `Assert.Equal(0, rig.Gateway.RosterAsked)` (`DirectorWayUpOfferTests.cs` line 121), in exactly
one test, on exactly one path (one owed seat offered). No other test asserts it: not the nothing-waiting
path, not the history, not the bring back. The code is right today - only `ReopenAsync` calls the roster -
but a roster call added to `FindOfferAsync`'s not-offered path, or to the history, would not redden a single
test.

And the test that holds the rule opens with a sentence that is now FALSE: "The engine's Gateway seam
carries no question about sessions at all - listing records, reading one, and starting one to reopen it
are all it can do" (`DirectorWayUpOfferTests.cs` lines 92 to 95). The trailing comment added at line 118
corrects it, so one method tells the reader two opposite things. The next reader of that test - the one who
will notice the rule is being eroded - is told the seam cannot ask at all, which is exactly the belief that
lets the rule die quietly.

What it would do to a person: nothing directly. It is a guard-rail finding. The rule it guards is the one
that keeps the start-up offer from flickering with whatever happens to be running, which is the difference
between a record-based promise and a race.

How sure I am: certain on the text and the single assertion; the code itself is right today, so nothing is
broken - the finding is that the rule's one holder is one line and its documentation contradicts itself.
Cheapest closes: fix the stale sentence, and assert `RosterAsked == 0` on the nothing-waiting and history
paths too. Review 1 already flagged this shape once ("exactly ONE test holds this rule") about the
empty-order guard; it is worth the same honesty here.

### Finding 3 - the interface documentation does not say a failed start keeps its claim

`IDirectorWayUp.ReopenAsync` (`IDirectorWayUp.cs` lines 66 to 71) tells the caller the once-only rule and
the cross-restart gap, but not the third fact: a reopen whose start FAILS - Gateway unreachable, or an
answer with no session id - consumes the seat's one claim until this Director restarts. The code comment
says it (`DirectorWayUp.cs` lines 249 to 251) and the answers say it, but the interface is the document the
window Developer reads, and the window is what will show the owner a button that goes dead after one
failure with no sentence in the interface explaining why. The runtime refusal does carry the honest sentence
("Check the session list before asking again"), so the OWNER is told - it is the window DEVELOPER building
against the interface who is not.

What it would do to a person: a transient Gateway failure on a reopen leaves the button permanently
refused until a restart, and the window author, not knowing that is deliberate, may file it as a bug or
work around it.

How sure I am: certain that the interface text omits it; the trade itself I judge right (question 4 above).
This is a documentation finding, ranked below the two above because nothing misbehaves.

### Finding 4 - a ninth agent that carries the conversation id in its arguments for a non-resume purpose will be worded as resuming

This is the one-direction lie of the walk's proxy, described under question 3. Today all eight flags are
exact and the walk is sound; the note is for the future. If an agent ever embeds the conversation id in its
launch arguments for a purpose OTHER than resuming the conversation, the walk will force its flag true and
the offer will promise a conversation that does not come back - the exact wrong direction the safe-side rule
exists to prevent. Whoever adds such an agent will meet a failing walk and must not "fix" it by flipping
the flag to match; the walk's proxy, not the flag, would need widening (for example to name the flag that
carries the id). Ranked last because it needs an agent that does not exist.

### Outside my scope, seen on the way

Nothing else. I looked at the history offers on cancelled and ignore-all records - the new `Reopen` field
now appears on ended seats of those records too - and I am satisfied it is not a defect: review 1 records
that the history ALREADY worded those seats "Its saved conversation can be reopened", so attaching the
offer completes a promise the words were already making, and the reopen itself remains guarded by the
roster, the claim and the record check.

## 6. What I attacked and failed to break

- **The eight flags against the eight drivers.** Read one by one (the table in question 3). Every value
  matches what the driver really does. I also checked the loose spelling match: a record that spells the
  agent "ClaudeCode", "claude" or "Claude Code" finds the one plugin through any of its three published
  names, and Copilot is shown as "GitHub Copilot", which is its plugin's own display name, not a second list.
- **The still-running guard.** Both halves - running on a reachable Director, and listed under an
  unreachable one - are the restore's own rule, asked through the envelope with reachability. I checked the
  claimed parity with `GatewayClientRestoreGateway.GetRosterAsync` line by line: the same call, the same
  envelope, the same reason in the comment.
- **The once-only guard inside one engine instance.** The claim is taken under a lock before the start
  leaves; the roster check does not consume a claim; an early refusal (no record, another Director's record,
  no such seat, no conversation recorded) does not consume one either. The weakness is only the instance
  lifetime (Finding 1).
- **The record guard.** My own mutation proved all three of its tests fail red (3 failed, 577 passed, 580
  total) and nothing else moved.
- **The history offer.** Both halves asserted in one test - not offered at start-up, offered per seat in the
  history - and the test then actually reopens, so the button is not decorative. Seats that handed over or
  came back carry null, with their own test. The offer on the history seat is computed by the identical call
  the rows make, so the two surfaces cannot drift apart in wording.
- **The vacuous-pass guards of the walk.** Empty registry caught by the count; an all-false world caught by
  the eight named cases; external plugins included in the walk because `AgentPluginRegistry.All` is what it
  iterates.
- **What review 1 proved sound.** The seed file is untouched by the diff. The bring-back order rules are
  untouched (`ChooseSeats` is not in the diff). The refusal-versus-emptiness wording and the
  write-nothing-on-refusal behaviour are untouched. `IsOfferable` is byte-identical. No fix undid any of
  review 1's section 5.

## 7. What I could not check

- **No real Gateway, Director or session was ever run.** Everything is against fakes of the two seams, by
  design. That the real Gateway answers `ListFleetSessionsWithReachabilityAsync` as the fake does is not
  proved here, and was not this review's job.
- **That a reopened conversation really comes back with its context**, for Claude Code, Pi, Copilot or
  Cursor. I verified the flags against what the drivers DO with the id - that the id reaches the command
  line - which is all the ruling asked the flag to state. Whether the agent honours it is the rig
  Developer's measurement, running separately.
- **The window phase's caller.** Finding 1 is prospective precisely because no caller exists in this tree;
  I could not check how the window Developer will hold the engine.
- **The hosted checks on pull request #3202** were not read, and the parked suites, the web tests and the
  Python tests were not run. Nothing in the diff touches the browser shells or the Python toolbelt; the
  whole solution does build with 0 warnings and 0 errors, which I ran myself.
- **Review 1's document was not in this tree at this commit** - I read it from the `smart-restart/p3-review-1`
  branch. If that branch's copy ever differs from what the Tech Lead ruled on, I would not know.

## 8. Verdict

All four findings are answered, and answered at the cause rather than painted over: the reopen asks the
product's own still-running rule before it starts anything, the resume fact now lives where the agent is
defined and is walked against the real drivers, the history carries the same offer the rows carry, and both
write paths refuse another Director's record by name. My own counts match the Tech Lead's on every check,
and my own mutation - on the one fix the Developer never mutation-tested - reddened exactly its three tests
and nothing else. The Developer's claims held up one by one; I disproved none of them.

What I owe the Tech Lead is the tripwire: the once-only guard is engine state, the only factory makes a new
engine per call, and nothing tells the window Developer - who is building against this surface right now -
that the guard's guarantee is his to hold. One sentence on the interface, or one held instance, closes it.
Findings 2 to 4 are guard-rail and documentation work, cheap and worth doing before the window phase
freezes on this surface.
