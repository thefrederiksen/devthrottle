# Answers - review of phase 4 (2 of 2): the command line door

Every finding of `review-phase-4-2.md` beside this file, answered: accepted, with what changed and the
test that proves it, or declined with the reason. Written on 20 September 2026 by the Developer seat
standing in for the one that built the work, which is gone and cannot be woken (method law 11: a
finding is never left unanswered, and the seat that built the work decides - here, the seat standing
in for it).

Each finding was checked against the code before it was accepted. A Reviewer advises; it can be
wrong. Five of the six are accepted, one in part; none is declined outright, and the reasoning for
the part not taken is under finding 2.

**Five, six and the merge.** The branch was merged with `origin/main` twice while this work was done
(no rebase, no force push). The first merge brought the way up's two counts, pull request 3232, which
RENAMED `WayUpRecord.SeatsOwedLabel` to `SeatsLabel` because the sentence now says both counts. Git
merged the text cleanly and the build then failed - `SmartRestartWire` was still reading the old name.
The wire shape follows main: `SmartRestartHistoryEntryDto.SeatsOwedLabel` is now `SeatsLabel`, and the
command line reads `seatsLabel`. That is not a finding; it is what the merge cost, and it is recorded
here because it changes the wire contract this phase shipped.

---

## Finding 1 (Medium) - the three Gateway routes have no test anywhere: ACCEPTED

**Checked first.** True as written. On the base this work sat on, the whole `CcDirector.Gateway.UnitTests`
project passes and not one of its tests reaches `GatewayEndpoints.cs` lines 3949, 3975 and 3989, and
`CcDirector.Gateway.Tests` held no smart-restart test either. The proof named the gap itself. The
harm the Reviewer describes is real and it is the harm this door exists to prevent: the window is
broken, the owner reaches for the command line, and the route is not where the guard, the contract and
the documentation all say it is.

**What changed.** A new hosted test class, `src/CcDirector.Gateway.Tests/SmartRestartRoutesHostedTests.cs`,
on a REAL Gateway with real credentials and a Director really on the tunnel - the pattern the sibling
half of this phase used (`DevReportRoutesHostedTests`, `FakeTunnelDirector`). The Director is
registered at an endpoint nothing listens on, so any working answer could only have come over the
tunnel. Seven tests:

| Test | What it proves |
|---|---|
| `ASessionKey_IsRefusedTheStart_WithTheSentenceNamingWhatItMayDoInstead` | A session key POSTing the start is refused THROUGH THE REAL AUTHENTICATION - 403, code `session_key_out_of_scope`, and the body is `SmartRestartRefusal.Start` exactly, the sentence that names `machine restart-request` as what a session may do instead. The Director is never asked. |
| `TheOwnersDeviceKey_ReachesTheEngineOverTheTunnel_AndTheAcceptanceComesBack` | The owner's own key reaches the engine: the route is mapped at the shape the guard refuses, the verb that travels is `smart-restart/start`, the minutes and the reason arrive as asked, and the answer is 202 with the acceptance - taken, not finished. |
| `ASessionKeyMayReadTheProgressAndTheHistory_AndTheRoutesRelayTheDirectorsOwnWords` | The two reads are open to a session key and are answered BY THE ROUTE, not the guard; each relays its own verb and carries the Director's own labels down untouched. |
| `TheOwnersDeviceKey_ReadsTheProgressAndTheHistoryToo` | The reads are open to both identities, not accidentally session-only. |
| `AnotherAccountsDeviceKey_FindsNoneOfTheThree_AndTheDirectorIsNeverAsked` | The tenant boundary: another account gets 404 on all three, so a stranger cannot even learn the Director exists, and nothing is asked of it. |
| `ADirectorThatAnswersWithNothingReadable_IsRefusedWithTheReason_NotAnEmptySuccess` | All three "answered with nothing readable" branches are 502 carrying their own sentence, never an empty success. |
| `AStartWhoseOrderCannotBeRead_IsRefused_AndNothingIsAsked` | A body that is not an order is a 400 and the Director is never asked - a start that could not be read is never begun on a guessed default. |

**They were each shown to fail** - see the revert proofs below. Round R1 (the two routes mapped one
word off) turns 5 of the 7 red; R2 (the progress route relaying the history verb, and the unreadable
answer returning a blank acceptance) turns 2 red; R3 (the guard's named refusal removed, and the
tenant resolution made not to refuse) turns the remaining 2 red.

**One limit of R1, stated because it is the Reviewer's own point turned on my own tests.** Under R1
the session-key refusal test stayed GREEN, because the refusal happens inside the authentication
before any route is reached - so that test says nothing about where the route is. It is the DEVICE-KEY
tests that pin the shape. The stranger's 404 also stayed green, because a route that is not there is a
404 as well. That is why the class needs both halves and why a guard test alone could never have
covered this.

**Not reached, still.** These tests use a stand-in Director on the tunnel; the real `ControlApiHost`
verb handlers are proven separately (`SmartRestartCommandLineTests`) and nothing yet joins the two
ends in one run. Phase 5's run on the rig is still where a real Director, a real launcher and these
commands meet. Whether phase 5 must cover the commands is the Delivery Lead's to decide, and my answer
is unchanged from the proof's: it should.

---

## Finding 2 (Medium) - the documented success exit code is racy and unreachable on the normal path: ACCEPTED IN PART

**Checked first.** The mechanism is as the Reviewer describes. `DirectorLauncherRestartStep` says in
its own words that a successful ask may never return to this process, because accepting the restart is
the launcher stopping this very Director. The watch polls every five seconds, so exit 0 needs a poll
to land between the engine recording `RestartAccepted` and the process ending, and that window may be
zero length. The usual end of a run that WORKED is therefore the Director going silent and exit 3 -
and the documentation was promising the lucky outcome as the normal one.

**Accepted: the documentation was wrong and is corrected.** `docs/cli-reference.md` and the command's
own help now lead with it: a restart that works usually exits 3, not 0; exit 3 covers both `--no-watch`
and a Director that went silent; exit 0 means the acceptance happened to be read before the Director
stopped. Both name the restart history as WHAT TELLS THE TWO APART after the fact - `restart-history`
carries the record and its outcome, and `director list` says whether the Director came back. A person
or a script now reads what will actually happen.

**Declined: having the command ask the Gateway what a silence meant.** That is the client ruling on
what a new Director id under an old name means, which is exactly what critical rule 7 forbids, and it
would put a second copy of that rule in Python where the Gateway already owns the record and the
registry. The command's behaviour is unchanged: it reports what it SAW, names the last phase it saw,
says in plain words that this is also what a restart that worked looks like from here, and claims
neither. If the Delivery Lead wants a single code for a successful restart, the honest place for it is
a Gateway verdict on the record - one fold, one answer, every client the same - not a guess in the
watcher. Phase 5's real run is what would measure it.

**Tests.** The two exits were already proven and still are:
`test_smartRestart_RestartAccepted_PrintsTheEnginesSentenceAndTheRecordAndExitsZero` and
`test_smartRestart_TheDirectorGoesSilent_SaysSoWithTheLastPhaseAndExitsThree`. The change here is to
words a person reads, not to behaviour, so it adds no test of its own - and saying so is the honest
answer rather than inventing one that would only assert the documentation quotes itself.

---

## Finding 3 (Low) - `restart-history --count` does not apply to `--json`: ACCEPTED

**Checked first.** True: the `--json` branch returned before the narrowing. This repository's command
line standard says in as many words that every filter applies to `--json` too, and that a filter
narrows the same shape rather than changing it; `session list` one file away does exactly that. An
agent composing flags by the house rule got every record and nothing said so.

**What changed.** `restart_history` reads the entries once, and the `--json` branch prints the
Gateway's own object with `entries` narrowed to the newest `count` and nothing else touched.

**Test:** `test_restartHistory_Count_NarrowsJsonTooAndLeavesTheShapeAlone` - one record comes back, it
is the newest, the message is the Gateway's own, and the key set is unchanged. Shown to fail in round
P2 (the narrowing removed from the `--json` path).

---

## Finding 4 (Low) - a malformed answer one level down is silently degraded: ACCEPTED

**Checked first.** True, and it is a fallback inside a file whose whole subject is not telling that
particular lie. An `entries` that was not a list became an empty history, so a Gateway that renamed or
reshaped the field would have printed `count: 0` and exited 0 - the Director reported as never
restarted. A `sessions` that was not a list became no rows, so a run would print a phase and a count
with every session missing underneath. Both are absences presented as answers, which is what the
top-level check already refuses; a shape check that stops at the top level only moves the lie one
level down.

**What changed.** `_entries` and `_rows` refuse anything that is not a list of objects, through one
exit (`_not_a_shape_i_can_read`) that names what could not be read and what is therefore unknown. A
non-object entry is refused rather than dropped, for the same reason.

**Also fixed, and named because the Reviewer did not ask for it:** `_print_entry` carried the identical
fallback for `seats` one level deeper. Leaving one of three identical fallbacks in the same file
because a review happened to name two would be arbitrary, so it is refused the same way - three lines.

**Tests:** `test_restartHistory_EntriesInAShapeThisCannotRead_IsARefusalNotAnEmptyHistory` (three
shapes: a renamed field, a list of numbers, a string),
`test_restartHistory_SeatsInAShapeThisCannotRead_IsARefusal`, and
`test_smartRestartStatus_SessionsInAShapeThisCannotRead_IsARefusalNotARunWithNoSessions`. All three
shown to fail in round P1, which put the three fallbacks back.

---

## Finding 5 (Low) - `--machine` without `--director` is silently ignored: ACCEPTED

**Checked first.** True: `machine` was consulted only when `director` was given. So
`director smart-restart --machine OTHER_BOX` reads as "empty the Director on OTHER_BOX" and empties
the one this session belongs to. Today's harm is contained - a session key is refused the start, and a
terminal with no `CC_DIRECTOR_ID` refuses with "Name the Director" - but this is the one command that
closes every session on a machine, and a locator that is read and dropped is the harm the mandate
names first.

**What changed.** `resolve_director` refuses `--machine` given without `--director` as a usage error
(exit 2), saying what the flag does and what to do instead. It sits in this file's own resolver, so it
binds all three of these commands and touches nothing else in the tool: the shared resolver
(`session_ops._resolve_director_id`) is unchanged, and `--machine` still does its own job beside a
name. The Reviewer suggested the start path or a shared usage error; putting it on all three is one
rule rather than three, and refusing a flag that means nothing is right on a read as well.

**Tests:** `test_everyCommand_MachineWithoutDirector_IsAUsageErrorAndNothingIsAsked` (all three
commands: exit 2, the sentence, and nothing asked of the Gateway) and
`test_smartRestart_MachineBesideDirector_StillNarrowsTheNameToOneComputer`, which proves the flag
still narrows an ambiguous name to one computer - it is only its use ALONE that is refused. Shown to
fail in rounds P2 and P3.

---

## Finding 6 (Low) - the history is capped at 25 and reports the cap as the whole history: ACCEPTED

**Checked first.** True. `DirectorWayUp.MostRecentRecordsRead` is 25 and `CandidatesAsync` takes that
many; the history reads the same capped list, and `WayUpWords.HistoryRead` stated the read count as
the Director's total. The mission's own habit is a restart most mornings, so the cap bites inside a
month, on the one surface whose documentation says nothing is deleted.

**What changed, and what did NOT.** The cap stays. It is right for the start-up read, which only ever
wants the newest, and reading past it on this door would make the history and the way up disagree
about what exists - which is the reason they share the list. What changed is that the cap is now
CARRIED rather than hidden: `CandidatesAsync` returns the records it read AND how many matched before
the cap, and the history's sentence says both when they differ - "The newest 25 of this Director's 30
restart records, newest first. The other 5 are older and are not read here; they are kept on the
Gateway and nothing has been deleted." When nothing is cut off the words are exactly what they always
were, so every existing screen and test reads unchanged.

The claim was corrected everywhere it was made: the verb's contract comment, the history DTO,
`docs/cli-reference.md` and the command's help and actions-registry entry no longer say "every restart
record". The sentence a person reads is still the Director's own; the command line renders it and
words nothing.

**Tests:** `The_history_says_when_older_records_exist_that_it_is_not_reading` (30 records, 25 read,
the sentence naming both counts) and `The_history_says_nothing_about_a_cap_that_cut_nothing_off`
(exactly 25, the old wording), in `DirectorWayUpHistoryTests` - so they run under the mission's own
filter. Shown to fail in rounds C1 and C2.

---

## The checks, and their counts

Everything below was run in the foreground on the MERGED result, branch `smart-restart/p4-command-line`
merged with `origin/main` at `297c090a5`. Both baselines were measured on untouched `origin/main` at
that same commit, in a THROWAWAY WORKTREE of its own (`D:/ReposFred/devthrottle-p4-baseline`, cut
detached from `origin/main`, removed afterwards) - never in this one, so no edit of mine could move the
files a baseline run was reading. Every count is read from the run's own summary line.

The mission's check:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Passed | Failed |
|---|---|---|
| Before, untouched `origin/main` at `297c090a5` | 617 | 0 |
| After | 637 | 0 |

The 20 added are the 18 this branch already had (11 in `SmartRestartCommandLineTests`, 7 guard cases)
and the 2 new cap tests. The baseline moved from the proof's 606 to 617 because `origin/main` gained
pull request 3232's way-up tests in between, which this filter matches.

The command line tool's own test project, `tools/cc-devthrottle/tests`, run from
`tools/cc-devthrottle` with `FORCE_COLOR=1`, exactly as the `tool-contracts` continuous integration
job runs it:

| When | Passed | Failed | Skipped |
|---|---|---|---|
| Before, untouched `origin/main` at `297c090a5` | 3401 | 0 | 3 |
| After | 3448 | 0 | 3 |

The 47 added are the 41 this branch already had and the 6 new ones above.

**The same warning about this machine's Python as the proof carries.** Both numbers were taken in a
throwaway virtual environment (Python 3.11.6, click 8.5.0, typer 0.27.2, rich 15.0.0, pytest 9.1.1).
The system Python on PATH has click 8.1.8, BELOW the tool's own declared floor of 8.2.1, and on it
about 1500 of these tests fail on untouched `origin/main` for reasons that have nothing to do with
this work. One thing has changed since the proof was written and is worth the next reader's time:
**typer 0.27.2 no longer pulls click in**, so `pip install typer rich requests pytest` alone leaves the
environment with no click at all and every run dies on import. Install click explicitly.

Also run, because this change touches the engine's file, the Gateway and the shipped tool:

- The new hosted route tests, `dotnet test src/CcDirector.Gateway.Tests --filter
  "FullyQualifiedName~SmartRestartRoutesHosted"`: **7 passed, 0 failed**. The parked suite's
  machine-wide lock was free; the run took 9 seconds and never waited.
- The whole `CcDirector.Gateway.UnitTests` project: **6856 passed, 0 failed, 8 skipped** (2 minutes 55
  seconds), on the first merged base. This is the run that covers what a filtered revert round cannot.
  It was NOT repeated after the second merge; the filtered check was.
- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`: **147 passed,
  0 failed** - phase 2's and phase 3's screens, which read the words file this change edits.
- `python -m pytest tools/test_shipped_tools_contract.py`: **52 passed** - the shipped-tool contract,
  including ASCII-only shipped source.

## The revert proofs

Every new test was shown to fail. The work was COMMITTED first (commit `811b8d1d1`), so no mutation
could eat it; every restore was `git checkout --` on literal paths, and every run after a restore was a
full `dotnet test` or `pytest` that REBUILT - no run used `--no-build`. The red list below is what the
run printed, not what was expected.

| Round | What was broken | What went red |
|---|---|---|
| R1 | the start route mapped at `smart-restarts` and the progress route at `smart-restart-progress` - one word off, exactly the finding's own example | 5 of 7: `TheOwnersDeviceKey_ReachesTheEngine...`, `TheOwnersDeviceKey_ReadsTheProgress...`, `ASessionKeyMayRead...`, `ADirectorThatAnswersWithNothingReadable...`, `AStartWhoseOrderCannotBeRead...` |
| R2 | the progress route relays the HISTORY verb; an unreadable start answer returns a blank acceptance with 202 instead of the 502 | 2: `ASessionKeyMayRead...`, `ADirectorThatAnswersWithNothingReadable...` |
| R3 | the guard's named refusal of the start deleted; the start route made not to refuse another account's Director | 2: `ASessionKey_IsRefusedTheStart...`, `AnotherAccountsDeviceKey_FindsNoneOfTheThree...` |
| C1 | the cap is not carried (`olderNotRead` always 0) and the cap sentence is unreachable | 1: `The_history_says_when_older_records_exist_that_it_is_not_reading` |
| C2 | the cap sentence fires when nothing was cut off (`olderNotRead` is the whole match) | 3: `The_history_says_nothing_about_a_cap...`, `The_history_says_when_older_records_exist...`, and the existing `The_history_holds_every_record_of_this_director_newest_first` |
| P1 | the three shape refusals put back as the silences they replaced (`entries`, `sessions`, `seats`) | 4: the three new shape tests, and the existing `test_restartHistory_PrintsEveryLabelTheDirectorComputed` |
| P2 | `--count` dropped from the `--json` path; the `--machine` usage error removed | 2: `test_restartHistory_Count_NarrowsJsonTooAndLeavesTheShapeAlone`, `test_everyCommand_MachineWithoutDirector_IsAUsageErrorAndNothingIsAsked` |
| P3 | `--machine` no longer passed to the resolver beside a name | 1: `test_smartRestart_MachineBesideDirector_StillNarrowsTheNameToOneComputer` |

R1, R2 and R3 ran the whole `SmartRestartRoutesHostedTests` class; C1 and C2 ran
`FullyQualifiedName~DirectorWayUpHistory`; P1 ran the WHOLE `tools/cc-devthrottle/tests` directory with
no filter, P2 and P3 the smart-restart file. **C1, C2, P2 and P3 were filtered, and that is a real
limit of those four rounds**: a mutation that reddened something outside the filter would not have been
seen. The unfiltered runs afterwards are what cover that, not the rounds themselves.

After the last restore, on the restored source with a rebuild: the mission check 637 passed 0 failed;
the hosted routes 7 passed 0 failed; the Avalonia screens 147 passed 0 failed; the command line project
3448 passed, 0 failed, 3 skipped; `git status` clean but for the untracked `START-HERE.md`.

## What is still not reached

- **Nothing here has spoken to a real Director, a real Gateway process outside a test, or a real
  launcher.** The new tests boot a real Gateway in-process and a stand-in Director on the tunnel. That
  closes the route gap; it does not close the end-to-end one. Phase 5 is still where these commands
  meet a real Director.
- **A start that actually RUNS through the host is still untested.** `ControlApiHost` has no seam for a
  Gateway client, so the host tests cover every answer a Director with no Gateway can give - which is
  every refusal - and none of the accept path.
- **The exit code a successful restart really produces has not been measured**, only reasoned from the
  launcher's own contract. The documentation now says what the reasoning says; phase 5's run is what
  would confirm it.
- **`Gateway.Tests` was run only under this one filter**, not whole. The class is new and touches no
  shared state beyond its own Gateway and its own ids, but "touches nothing else" is a reading.
- **`Core.Tests`, the web tests and the other Python toolbelts were not run.** This change touches none
  of them, which is also a reading.
