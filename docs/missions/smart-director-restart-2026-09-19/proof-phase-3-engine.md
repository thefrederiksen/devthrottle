# Proof - phase 3, Developer: the way up ENGINE

Written by the Developer seat opened by the phase 3 Tech Lead (session 38f41a97), 20 September 2026.

- Branch: `smart-restart/p3-way-up-engine`, cut from `origin/main` = `ab2770c4a`.
- Worktree: `D:/ReposFred/devthrottle-smart-restart-p3-engine`.
- Commit: `d1168ea18`.
- Pull request: **#3202**, against `main`, not merged and not waiting on the hosted checks.

---

## 1. What was built

Four new files in `src/CcDirector.ControlApi/SmartRestart/`, namespace
`CcDirector.ControlApi.SmartRestart`, and one new method on `ControlApiHost`. No window, no XAML, no
change to `src/CcDirector.Avalonia`, no change to the Gateway, to `WorkspaceStore`, to
`WorkspaceValidation`, to any contract in `CcDirector.Gateway.Contracts`, or to `DirectorRestore`,
`DirectorDrain` or the phase 1 `SmartRestart` files.

| File | What it is |
|---|---|
| `IDirectorWayUp.cs` | The interface a window calls, and every record and enum it answers with. This is the published surface the next Developer builds against. |
| `WayUpWords.cs` | Every word a person reads, in one place, plus the one rule that decides what reopening a seat would really do, and the seed file's text. |
| `IWayUpGateway.cs` | The two seams - the Gateway and the restore - and the real implementation of each. |
| `DirectorWayUp.cs` | The engine. |
| `ControlApiHost.CreateDirectorWayUp()` | How a window gets the engine. Never null, like `CreateSmartShutdown`. |

### The published surface

```csharp
public interface IDirectorWayUp
{
    Task<WayUpOffer> FindOfferAsync(CancellationToken ct);
    Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct);
    Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct);
    Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct);
}

IDirectorWayUp ControlApiHost.CreateDirectorWayUp();   // never null
```

`WayUpOffer` is `(WayUpOfferState State, string Message, WayUpRecord? Record)`, where the state is
`Offered`, `NothingWaiting` or `Refused`. `WayUpRecord` carries the workspace id, when the shutdown was
in both universal and local time, the headline, the when and reason labels, how many seats are owed with
its label, and the rows. A `WayUpRow` is `BringBack` (a mission head and the seats under it, ticked) or
`EndedWithoutHandover` (one seat, unticked, with a `WayUpReopenOffer`). The history is
`(bool Refused, string Message, IReadOnlyList<WayUpHistoryEntry> Entries)`, and an entry carries the same
`WayUpRecord` as its offer when the record still owes seats.

Every word a person reads is computed in the engine and carried on these answers. A window lays them out;
it never decides what a state means (critical rule 7 in `CLAUDE.md`).

---

## 2. The commands that were run, and the counts

### The baseline, before anything was written

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
```

    Passed!  - Failed:     0, Passed:   523, Skipped:     0, Total:   523

**Total 523, passed 523, failed 0.** The Tech Lead measured 523 total, 522 passed, 1 failed on the same
commit. The total agrees exactly. The one red it saw -
`SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state`, which fails with a
SQLite error in `DeviceRegistry` and passes when run alone - passed in this run. It is intermittent, it is
not this phase's, and nothing here touched it.

### After

Same command:

    Passed!  - Failed:     0, Passed:   567, Skipped:     0, Total:   567

**Total 567, passed 567, failed 0.** Risen by 44, which is exactly the number of tests added (19 + 13 +
12, listed in section 4). No new failure.

---

## 3. The revert proof - two tests shown able to fail

The work was COMMITTED FIRST (`d1168ea18`), so `git checkout --` could only ever restore the committed
file and never eat the change. Each mutation is one line, applied to the source and then reverted; each
run is a full build, never `--no-build`, so what was measured is the source and not a stale assembly.

### Mutation 1 - drop the rule that a record belongs to THIS Director

In `DirectorWayUp.CandidatesAsync`, one line:

```
-  && string.Equals(w.DirectorName, directorName, StringComparison.OrdinalIgnoreCase))
+  && string.Equals(w.DirectorName, w.DirectorName, StringComparison.OrdinalIgnoreCase))
```

    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpOfferTests.A_record_for_another_director_on_this_machine_is_not_offered
    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpHistoryTests.The_history_leaves_out_another_directors_records
    Failed!  - Failed:     2, Passed:   565, Skipped:     0, Total:   567

**2 failed, 565 passed, 567 total.** Both are the tests that own the rule, and no other test noticed - so
those two, and only those two, are what hold it.

### Mutation 2 - put a rule of conduct into the seed file

In `WayUpWords.SeedFileText`, one line:

```
-  "Verify the state of your work before you act on anything." + Environment.NewLine;
+  "Verify the state of your work before you act on anything. Do not commit anything unless the owner asks." + Environment.NewLine;
```

    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpBringBackTests.The_seed_file_holds_exactly_the_four_parts_and_nothing_else
    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpBringBackTests.The_seed_file_with_no_reason_holds_the_same_four_parts
    Failed!  - Failed:     2, Passed:   565, Skipped:     0, Total:   567

**2 failed, 565 passed, 567 total.** The added sentence is the exact kind of line that overrode a
handover's own plan on 19 September, and the two seed tests caught it because they assert the WHOLE file
against the four parts it is allowed to hold. A test that had only checked some banned phrase was absent
would have passed this mutation.

### Restored, and green again with a full build

`git status --short` and `git diff HEAD --stat` both printed nothing after the restore, so the source is
byte for byte the committed source. The source files were then touched to force a recompile and the check
run again:

    CcDirector.ControlApi -> ...\CcDirector.ControlApi.dll
    CcDirector.Gateway.UnitTests -> ...\CcDirector.Gateway.UnitTests.dll
    Passed!  - Failed:     0, Passed:   567, Skipped:     0, Total:   567

Both assemblies were rebuilt in that run, so the green is the restored SOURCE and not a leftover binary.

---

## 4. What each new test proves, in plain words

### `DirectorWayUpOfferTests` - 19 tests

1. **A record for another Director on this machine is not offered.** The key is the Director's display
   NAME, because a restarted Director gets a new identifier. It is not even read as a document: the
   Gateway's own summary said whose it was.
2. **A record already brought back is not offered.** Every owed seat has a restored session id, so nothing
   is owed.
3. **A cancelled record is not offered.** Those sessions never stopped; offering them would start a second
   copy of each.
4. **An ignore-all record is not offered.** The owner said the sessions did not matter.
5. **A record with one owed seat IS offered, and nothing is asked about running sessions.** The check is
   the record's presence. The Gateway seam carries no question about sessions at all, so a session count
   cannot creep into the rule even by accident.
6. **The newest record that may be offered is the one offered**, and reading stops there rather than
   walking the rest.
7. **Only the newest twenty-five records are read** - forty are listed, twenty-five are read, and the
   first and last read are the newest and the twenty-fifth newest.
8. **A record listed and then deleted is passed over**, and the next one is offered.
9. **Rows are one per mission head, leads first, each carrying its own seats, all ticked.** Two missions,
   a three-deep chain in one of them, and the seats come out lead, tech lead, worker.
10. **A seat whose reporting line names a seat this record does not hold is its own mission head.** The
    chain stops where the record stops.
11. **A seat that ended at the limit is its own row, unticked**, with no seats under it and a detail
    saying it was still running when time ran out. It is not counted among the seats waiting to come back.
12. **A seat that never answered is also its own unticked row**, with its own reason.
13. **A Claude Code seat is offered its saved conversation** ("Reopen its saved conversation").
14. **A Pi seat is offered its saved conversation** the same way.
15. **A Codex seat is offered a fresh session and told so** - "Codex cannot be started on a saved
    conversation ... a NEW, blank session".
16. **An agent nothing knows is offered a fresh session like Codex.** This is the safe way round, and
    every agent added after this was written falls into it by itself.
17. **A seat with no conversation id says so and offers nothing** - no button that could never work.
18. **The Gateway unreachable is a refusal carrying the reason**, never an empty list, and the test asserts
    the state is not `NothingWaiting`.
19. **A Director with no display name refuses** rather than matching on a blank, which would claim another
    Director's records.

### `DirectorWayUpBringBackTests` - 13 tests

1. **The order names every seat under every ticked row**, in lead-first order, with the workspace id and
   no asking session, and it is handed to the restore.
2. **A row left unticked is not brought back** - its seats are not named.
3. **The seed file holds exactly the four parts and nothing else.** The whole file is asserted against a
   literal written in the test.
4. **The same four parts when no reason was given** - the third part still says what changed and no more.
5. **The seed file is written beside the handover** it points at, in the drain folder the record names.
6. **A seat with no handover gets no seed file and is still named in the order**, so the restore refuses
   that one seat with its own reason while the others come back.
7. **A row that is not in the record is refused by name**, and nothing is started. A row quietly dropped
   is a session left dead with nobody noticing.
8. **A row that ended without a handover is refused with what to do instead** - reopen its conversation.
9. **Nothing ticked is refused** and never sent as an empty order, which the restore would read as "bring
   back everything owed".
10. **A restore that refuses is reported with its own reason**, and nothing claims to have started.
11. **A record that is no longer there is refused with its name.**
12. **The Gateway unreachable refuses the bring back with the reason**, and the restore is never called.
13. **A seat that does not come back says why beside it**, and the summary counts both.

### `DirectorWayUpHistoryTests` - 12 tests

1. **The history holds every record of this Director, newest first** - a smart shutdown, a cancelled one
   and an ignore-all - each with its kind in plain words.
2. **The history leaves out another Director's records.**
3. **A record that still owes seats carries the offer in the history**, with the same rows the start-up
   check would have shown. One rule, not two that can drift apart.
4. **A cancelled record stays in the history and offers nothing.**
5. **Each seat says what became of it**: came back as a short id, handed over and waiting, or ended when
   time was up.
6. **An empty history says so and is not a refusal.**
7. **The Gateway unreachable is a refused history and never an empty one**, and the test asserts the empty
   history's words are absent.
8. **Reopening a seat starts it on its saved conversation with one line** telling it that it was stopped
   when the Director shut down and must check the state of its work before acting - in its own repository,
   under its own agent.
9. **Reopening a Codex seat makes the same call** with the same conversation id, and says it will be
   blank. There is no second way round it.
10. **Reopening a seat with no conversation starts nothing** and says why.
11. **Reopening a seat that is not in the record starts nothing** and names it.
12. **Reopening with the Gateway unreachable says so** and claims nothing.

---

## 5. Every decision made along the way

1. **One record is offered at start-up, not a list.** The mandate says "offer one only if", and reading
   stops at the first record that qualifies. Anything older is reachable through the history. This is also
   what keeps start-up cheap.
2. **Newest first is `UpdatedUtc`, then `CreatedUtc`.** Both are on the Gateway's summary, so the ordering
   is made without reading a single document.
3. **When the shutdown was** is `CompletedAtUtc`, or `StartedAtUtc` when it never finished, or `CreatedUtc`
   failing both - a record always has that last one. A documented precedence over three optional fields,
   not a fallback that hides anything.
4. **A seat that never answered (`unreachable`) gets an "ended without a handover" row too.** The mandate
   names "a seat whose `DrainState` is `EndedAtLimit`, and any seat that never answered" in one breath.
   Both are worded from their own drain state, so the row says which it was.
5. **Headship is read over the WHOLE record, not over the owed seats alone.** A mission whose lead is not
   coming back still shows as one mission rather than a handful of loose sessions; the row then says the
   lead is not coming back. The order is still built from the row's SEATS, so a head that is not owed is
   never sent to the restore.
6. **The reopen does NOT write to the record.** Marking a seat would need the restore lease and a workspace
   write, which is the restore's own job and outside this mandate. The consequence, said plainly: a seat
   reopened this way still appears in the history as ended without a handover, and could be reopened twice.
   Worth a later ruling; it is not a silent behaviour, because the row says exactly what it does.
7. **The reopen hands the conversation id over for EVERY agent**, including Codex, which ignores it. One
   path, honest words. No second way round it, as the mandate requires.
8. **An agent this build does not know is worded like Codex.** `WayUpWords.Resumes` answers true only for
   Claude Code and Pi; everything else falls to the fresh-session wording. Spellings are normalised, so
   "ClaudeCode", "claude" and "claude-code" are one answer.
9. **A seat with no handover gets no seed file.** A seed naming a document that does not exist is worse
   than none. The seat is still named in the order, and `DirectorRestore.BuildRequest` refuses that ONE
   seat with its own message inside its per-seat catch, leaving the others to come back.
10. **The seed file name is `<short id> - <name> - what changed.md`**, beside the handover, built from
    `DrainPaths.ShortId` and `DrainPaths.Sanitize` so it cannot collide with the handover's own name.
11. **The restore seam is ONE method.** `DirectorRestoreWayUp` does what the tunnel's own restore does -
    claim the one-at-a-time gate, `PrepareAsync`, release on a refusal, then `RunAsync` - so the engine
    builds the order and nothing else.
12. **The Director's display name is read through a function, not held.** It is read from
    `NamedInstanceRegistry` on every call, which is the same source the Director tells the Gateway on every
    reseed, so a Director renamed since it started still finds its own records. The Gateway client is read
    the same way, for the reason phase 1 gave: a settings change replaces it.
13. **`BringBackAsync` awaits the restore** and returns its per-seat outcomes, rather than starting it and
    returning at once. A window calls it off the user interface thread; the interface documentation says so.
14. **Three states on the offer, not a bool plus a nullable reason.** "There is nothing waiting" and "the
    Gateway did not answer" are different facts and a window must not be able to show one as the other.

---

## 6. What I could NOT reach, and what this proof does not cover

- **No Gateway, no Director and no session was ever run.** Every test is against fakes of the two seams.
  What is proved is the RULES - which record may be offered, how rows are built, what the seed file says,
  what order the restore is handed. What is NOT proved is that a real Gateway answers these calls as the
  fakes do.
- **Whether reopening a saved conversation actually restores its context** is untested here, for every
  agent. It is being measured on the isolated rig by another Developer at the same time. This work reports
  only what the code does: `ClaudeDriver` passes `--resume`, `PiAgent` passes `--session-id`, `CodexDriver`
  logs "ignoring resume". If the rig shows that Claude Code or Pi does not really come back with its
  context, the WORDS in `WayUpWords.ReopenOffer` are the one place to change, and nothing else moves.
- **That the Director's display name always equals the `DirectorName` the Gateway stamps on the record.**
  The record's name is written by the Gateway from the display name this Director last told it, and the
  engine reads the same registry the Director tells it from - but the two were not observed agreeing on a
  live Gateway. If they ever disagree, no record is offered at all (never the wrong one), and phase 5's
  real run is where it would show.
- **`ControlApiHost.CreateDirectorWayUp()` has no test.** It is a factory of four arguments with no logic,
  and the unit test project cannot build a `ControlApiHost` without a Gateway. Its arguments are the risk,
  not its behaviour, and they are named in decision 12 above.
- **The `-Parked` suites and the web and Python tests were not run.** Nothing here touches them.
- **The hosted checks on the pull request were not waited for**, as the mandate says.
- **`WorkspaceDrainStates.EndedAtLimit` is only written by the smart shutdown of phase 1.** These rows have
  therefore never been seen against a record a real drain wrote; the rig run of phase 5 is the first time
  they will be.
