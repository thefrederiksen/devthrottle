# Proof - the way up offers a record whose sessions all ended without a handover

Written by the Developer (session 91475f79) on 20 September 2026, on branch
`smart-restart/p3-offer-ruling`, cut from `origin/main` at `d8fdaafed`. Issue #3167.

The task was the one ruling phase 3's Tech Lead asked for and the Delivery Lead made:
`ruling-way-up-presence-check.md`, committed with this change.

---

## 1. What was wrong

`DirectorWayUp.IsOfferable` offered a record only when it held a seat decided `restore` that had not
come back. A seat that ended at the limit is written `undecided`, so it is never such a seat.

The record the operating system shutdown writes holds nothing else:
`DirectorDrain.RecordOperatingSystemShutdownAsync` writes EVERY seat as `EndedAtLimit` with decision
`Undecided`. So that record could never be offered by any means, and ruling 10.5 - "on the way up
offers the saved conversations as in 10.3" - could not be satisfied by any record at all. Those
conversations were reachable only through File, Restart history.

## 2. What I changed

**`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs`**

- `IsOfferable` now offers a record that came from a smart shutdown, was not cancelled, and holds at
  least one seat that can still be acted on: a seat decided `restore` that has not come back
  (`OwedSeats`, as before), OR a seat that ended without a handover whose saved conversation can be
  reopened and has not been (`ReopenableSeats`, new).
- `ReopenableSeats` is `EndedWithoutHandover` plus one question: can the offer be TAKEN? It asks
  `WayUpWords.ReopenOffer(...).CanReopen` rather than restating the rule, so it cannot drift from what
  the button itself would do. `EndedWithoutHandover` already requires the seat not to have come back,
  which is the "and has not been" half.
- `BuildRecord` now also counts the seats that ended without a handover. It counts them off the ROWS'
  own filter, not off `ReopenableSeats`, because the rows list every such seat - including one whose
  conversation was never recorded, which is listed with a sentence and no button. The number a person
  reads is the number of rows under it.
- Comments: why the check is wider than 5.3 item 10, and that either row group may be empty.

**`src/CcDirector.ControlApi/SmartRestart/IDirectorWayUp.cs`**

- `WayUpRecord` gains `SeatsEndedWithoutHandover`.
- `WayUpRecord.SeatsOwedLabel` becomes `SeatsLabel`, because it now names both counts and a name that
  said "owed" would be a lie on the record this change exists for.

**`src/CcDirector.ControlApi/SmartRestart/WayUpWords.cs`**

- New `SeatsLabel(owed, endedWithoutHandover)`. With nothing ended it returns exactly what
  `SeatsOwedLabel(owed)` returned, so every existing record reads word for word as it did.
- `SeatsOwedLabel(int)` is unchanged and still used by `OutcomeLabel` in the history.

**Client (`src/CcDirector.Avalonia/SmartRestart/`)**

- `WayUpOfferViewModel.SeatsOwedLabel` -> `SeatsLabel`, still shown exactly as given; the log line
  also records the new count.
- `WayUpOfferWindow.axaml`: `TxtSeatsOwed` -> `TxtSeats`, bound to `SeatsLabel`.
- `WayUpOfferWindow.axaml.cs`: the designer record.
- `RestartHistoryViewModel`: the comment on `HasOffer` and the refusal message in `BuildOffer` said
  "owes no seats", which is no longer the rule. No behaviour changed there - the history carries the
  offer exactly when `IsOfferable` says so, which is the whole point of it reading one rule.

No fallback was added anywhere: a record with nothing that can be acted on is refused, in one place,
with the history unchanged behind it.

## 3. The check - counts before and after

Read the COUNT, not the colour.

| Command | Before (on this branch, before my change) | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 606 passed, 0 failed | **614 passed, 0 failed** |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 145 passed, 0 failed | **147 passed, 0 failed** |

Both before-counts match what phase 3 recorded on main (606 and 145).

`dotnet build cc-director.sln` succeeds with 0 warnings.

**One flake seen, and it is not mine.** The very first before-run reported 1 failed of 606:
`FleetManagerPlacementServiceTests.ResumePendingAsync_MarkChangedByHandBeforeTheFirstLookAfterARestart_AbandonsAndClosesNothing`,
an Entity Framework read inside `TenantSettingsStore.Get`. Run alone it passed; the same filter run
again passed 606 of 606. It is in the fleet-manager placement tests, touches nothing this change
touches, and it has not reappeared in any of the eleven runs since. I am recording it rather than
leaving it out: if it comes back, it was there before this branch.

## 4. What each new test proves, in plain words

**Engine** - `src/CcDirector.Gateway.UnitTests/Restart/DirectorWayUpOfferTests.cs`, eight new:

1. *A record whose seats all ended without a handover is offered with those seats unticked* - the
   whole point. The operating system shutdown record is now offered: nothing waiting to come back,
   both seats listed, both UNTICKED, each carrying a button that works, no bring back row at all,
   nothing started, and the start-up check still never asks what is running.
2. *The label names both what is waiting and what ended without a handover* - a record holding one of
   each reads as one sentence naming both, so the unticked rows under it are explained and what
   pressing bring back would do is not hidden.
3. *A record whose ended seats have no saved conversation is not offered and stays in the history* -
   nothing left to act on. Every button such a window could draw would be one that could never work,
   so it is not offered; its seats are still readable in the history, each saying there is nothing to
   reopen.
4. *An ignore-all record whose seats all ended without a handover is still not offered* - 5.3 item 8
   holds. The record has exactly the shape the widening now offers, so it is the door that had to
   stay shut.
5. *A cancelled record whose seats all ended without a handover is still not offered* - 5.3 item 7
   holds, same shape, same reason.
6. *An ended seat that has already come back does not make a record offerable* - "and has not been".
7. *A record offered for ended seats alone brings nothing back* - pressing bring back with nothing
   ticked is refused in the engine's own words, ticking the ended row is refused with what to do
   instead, and the restore is never called either way.
8. *A record of Codex seats alone is offered and still promises no conversation* - Codex stays noted
   only (5.4): the record IS offered, because the mission says such a seat is offered as a fresh blank
   session in its repository, and the offer says plainly that the conversation does not come with it.

**Engine, history** - `DirectorWayUpHistoryTests.cs`: the test that asserted such a record was NOT
offered at start-up now asserts that it is, that the history carries the SAME offer (same line, same
rows), and - unchanged - that each seat carries a working reopen offer. Its name says what it now
proves.

**Window** - `src/CcDirector.Avalonia.Tests/SmartRestart/WayUpOfferWindowTests.cs`, two new:

9. *Show, every seat ended without a handover, rows are unticked and each carries its button* - the
   real window is opened on the record the operating system shutdown produces: the engine's line is on
   screen, two tick boxes, neither ticked, and two reopen buttons.
10. *Show, every seat ended without a handover, nothing is asked of the engine* - showing that window
    asks the engine for nothing at all. Nothing comes back by itself.

## 5. Revert proofs - each new test can fail

Every one of these mutated the PRODUCTION source, rebuilt, ran the real filter, then restored in a
`finally` and rebuilt again, so no mutated binary could report a later run green. From proof 7 onward
the helper also asserts the restored file matches the commit.

| # | The mutation | Filter | Result |
|---|---|---|---|
| 1 | `IsOfferable` narrowed back to `OwedSeats(doc).Count > 0` | Gateway | 3 failed, 611 passed: the start-up offer test, the Codex-alone test, and the history test |
| 2 | `ReopenableSeats` drops the `CanReopen` question | Gateway | 1 failed, 613 passed: the no-saved-conversation test |
| 3 | `doc.CancelledAtUtc is null` removed | Gateway | 3 failed, 611 passed: both cancelled tests and the cancelled history test |
| 4 | the smart-shutdown kind check removed | Gateway | 3 failed, 611 passed: both ignore-all tests and the twenty-five-records cap test |
| 5 | the ended rows built `Ticked: true` | Gateway | 5 failed, 609 passed: the start-up offer test, the Codex test, the history test, and both older unticked-row tests |
| 6 | `SeatsLabel(owed, ended)` put back to `SeatsOwedLabel(owed)` | Gateway | 2 failed, 612 passed: the start-up offer test and the both-counts test |
| 7 | the window words the count itself (`SeatsLabel => Record.Headline`) | Avalonia | 4 failed, 143 passed: the new window test, and three older ones that hold the same rule |
| 8 | the window ticks every row (`_ticked = true`) | Avalonia | 2 failed, 145 passed: the new window test and the older unticked-row test |

**A restore that did not take, said plainly.** The first attempt at proof 7 printed "RESTORED" and the
mutation was still in the file - the helper did not check. Proof 8 therefore ran with proof 7's
mutation still in place and its result was contaminated (5 failed, not 2). I added the restore check
to the helper and ran BOTH proofs again from a clean tree; the table above holds only the clean runs.
Why the first `git checkout --` did not take was not established. The lesson is the one already
written down: a revert proof must ASSERT its restore, not report it.

After every proof the tree matched the commit, and the two final runs on the restored source are the
614 and 147 in section 3.

## 6. What I could not reach

- **No Director was run.** The mandate forbids running the feature against a real Director, so
  nothing here proves what the window looks like on screen at a real start-up, nor that a reopened
  conversation really comes back with its context. Phase 3's rig measured that separately; this change
  does not touch the reopen itself.
- **"Has not been reopened" is read off the RECORD, not off this run.** A seat counts as not yet
  reopened when it has no restored session id. The once-only reopen claim
  `DirectorWayUp.Reopened` is process state and is deliberately not consulted here, so a record whose
  seats were all reopened a minute ago is still offered by the history until something is written onto
  the record. That is unchanged by this task and is the gap `DirectorWayUp` already documents: marking
  a seat needs the restore lease, a workspace write and a new mark kind in
  `CcDirector.Gateway.Contracts`, which would need a Gateway deploy. At start-up, where the offer is
  actually made, nothing has been reopened yet, so the case does not arise there.
- **The history now draws a Bring back button on such a record.** The history entry carries an offer
  exactly when start-up would, by one rule; opening it on an all-ended record shows the same window,
  whose rows are all unticked. Pressing bring back there is refused in the engine's own words ("no row
  was ticked"). I judged one rule with a plain refusal better than a second rule in the history that
  could drift from the first, which is what phase 3 deliberately built. If the owner would rather that
  button were not drawn at all when a record has no bring back row, that is a small change in the
  history window and a ruling I did not have.
- **Only the two filters in the mandate were run**, plus a full solution build. No web tests, no
  Python tests, and the parked suites were not run.
