# Factory Seats, phase 1 - a seat you can open

Proof for phase 1 of the mission "Factory Seats" (issue 3950, mission document
`docs/missions/factory-seats-2026-10-13/MISSION.md`). Built on devlinux on 13 October 2026, on the branch
`factory-seats/phase-1-seat-page`, rebased onto origin/main at `0ae87f3e5` before the check was run.

## What changed

1. **Every seat has a page**, at `/factories/<factory>/seats/<seat>`, with or without a schedule.
   - The Gateway composes every word (`FactorySeatPageFold`, `GET /gateway/factories/{factory}/seats/{seat}`); the
     Cockpit draws it (`FactorySeatView.tsx`).
   - Its panels, in order: the head (name, role, factory, computer, a chip for how it starts, Talk); **What this seat
     does**; **How it starts**; **Goal** with the goal number; **What it has left behind**.
   - In this phase "What this seat does" shows where the seat's brief file is - its path and its computer, from the
     registration - and says: "The brief is a file on <computer>. It is not held on the Gateway yet, so it cannot be
     read or changed here." No brief is stored; that is phase 2.
2. **Every row of the Seats tab opens its seat** with "Open seat", also a seat with no schedule.
3. **Talk only.**
   - A seat with nothing scheduled reads "Talk only" where it read "Not scheduled".
   - A factory nothing is scheduled for has the status word **TALK ONLY** and the line "Nothing scheduled. You run it
     by talking to it." It ranks last, after RUNNING, and has its own group and count on the cards.
   - A failure or a waiting question still wins over it, exactly as over RUNNING.
   - A factory whose schedules are all switched off stays PAUSED. So does one that names a schedule the Gateway no
     longer has: something was set to run it.
4. **A schedule can be taken off a seat.**
   - One route: `POST /cron/jobs/{id}/unlink`. One command: `cc-devthrottle schedule unlink <id>`. One button: "Take
     off this seat", beside the schedule on the seat page, behind a confirm in the Gateway's sentences.
   - The schedule stays, switched on or off as it was, with the next run it had, in no factory. One that is switched
     on keeps running as a plain schedule; the confirm, the answer and the command all say so, and the command offers
     `schedule disable` first.
   - It obeys `FactoryNaming.TryAct` exactly as pausing does: a person, a session of that factory, or the account's
     own machine token. The session key guard lets a session reach the route, so the answer a session outside the
     factory reads is the factory rule's own sentence.
   - Refused, with nothing changed: a schedule on no seat (409), and a schedule its factory's archive switched off
     (409) - Restore switches those back on by id, so it would switch on a schedule that is no longer the factory's.
5. **The old read-only page is gone.** `/factories/<factory>/agents/<agent>` and the older
   `/factory-agents/<factory>/<agent>` land on the seat page. The Cockpit view, its client call, the Gateway route
   `GET /gateway/factory-agents/factories/{factory}/agents/{agent}`, its fold and its contract are removed. Links the
   Gateway writes for a factory agent (a session's chip, the map) now point at the seat page.

What the old page showed and where it is now: the triggers that wake the seat, each with its status and last check,
and their Pause or Resume, are under How it starts; the last 7 days' counts and the recent activity are under What it
has left behind. **Two things were not carried over**, because the mission document's panel list does not have them:
the "Ask the Fleet Manager to change it" link, and the IDLE / WORKING / FAULT word beside the name (a trigger's fault
is still shown on the trigger).

## Tests

| Where | What it shows |
| --- | --- |
| `Gateway.UnitTests/Factory/FactorySeatPageFoldTests.cs` (15) | a seat with a schedule; **a seat with no schedule** has every panel and reads Talk only; **a seat whose schedule id is missing from the Gateway** says so and offers nothing to press; one missing and one running; paused is not talk only; triggers; the confirm's sentences for a switched-on and a switched-off schedule, and when it says the seat becomes talk only; no Take off for a schedule the archive switched off; the answer's sentence; only the seat's own rows and its own last talk; the newest 15 lines; every Seats row opens its seat |
| `Gateway.UnitTests/Factory/FactorySeatRoutesTests.cs` (18, real HTTP, real schedule store, registry and activity record on one database) | the seat page for a seat with a schedule and one with none; the last talk is the seat's own; an unknown seat and an unknown factory are 404 with a sentence; a session key is refused the page; unlink by the owner takes it off, keeps it switched on with the same next run, the registry's seat no longer lists it, the seat reads Talk only, **the factory with nothing left scheduled reads TALK ONLY**, and the schedule is listed outside any factory; a switched-off one stays off; **a session outside the factory is refused** with the pause rule's own sentence and nothing changes; a session in no factory likewise; an unknown session gets "try again"; a session of the factory and the machine token are allowed; a schedule on no seat, a missing schedule and an archived factory's schedule are refused; the guard admits `POST .../unlink` and nothing else on that path |
| `Gateway.UnitTests/Factory/FactoriesScreenFoldTests.cs` (4 new, 3 changed) | **a factory with seats and nothing scheduled** is TALK ONLY, rank 4, on the list and on its page; it never hides a failure or a question and sorts last; every schedule switched off, or one paused trigger, stays PAUSED; seats naming schedules the Gateway does not have stay PAUSED |
| `Gateway.Tests/FactoryRegistryRouteTests.cs`, `FactoriesScreenRound2Tests.cs` (changed) | the same rule through the registry's own routes |
| `tools/cc-devthrottle/tests/test_schedule_factory_seat.py` (8 new) | `schedule unlink` calls the one route and never a read-then-update; prints the Gateway's sentence; offers `schedule disable` first when it keeps running and not when it is off; `--json` is the Gateway's answer; **the refusal for a session outside the factory** is shown and exits 1; a schedule on no seat exits 1; an answer that does not say what happened is never reported as taken off; a missing id and an unknown flag exit 2 |
| `apps/cockpit/src/factory/FactorySeatView.test.tsx` (13) | a seat with nothing scheduled opens, shows every panel and offers only Talk; a seat with a schedule shows Edit, Pause and Take off; Pause and Resume send what the button said; a trigger and its Pause; the missing schedule's sentence and no buttons; the confirm asks first and sends nothing until confirmed; confirmed, it shows the Gateway's sentence and reloads; **refused, the Gateway's sentence stays in the confirm and nothing reloads**; an unknown seat shows the Gateway's sentence |
| `apps/cockpit/src/factory/FactoriesScreen.test.tsx` (4 new) | both old agent addresses land on the seat page, query kept; every Seats row has Open seat, also one with no Edit and one with no schedule at all |
| `apps/cockpit/src/factory/FactoriesCards.test.tsx` (1 new, 2 changed) | a TALK ONLY factory is in its own group, last, and counted as talk only, never as paused |
| `packages/client-core/src/factory/factoriesScreenClient.test.ts` (6 new) | the seat page's address, escaped; the Gateway's sentence on a 404; the unlink is one POST with no body; a refusal keeps the Gateway's sentence; a web page instead of data is refused, never read as taken off |

## The check (mission document, section 7)

Run on devlinux on the rebased branch.

| Command | Result |
| --- | --- |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Schedule"` | 1,251 passed of 1,251 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | 48 passed of 48 |
| `npm run typecheck` | clean, all four workspaces |
| `npm test --workspace @devthrottle/client-core` | 2,022 passed of 2,022 |
| `npm test --workspace @devthrottle/cockpit` | 1,122 passed of 1,123. The one red is `src/licenses/fixtures.test.ts`: see below |
| `python3 -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"` | 859 passed, 1 skipped |

Beyond the check:

| Command | Result |
| --- | --- |
| `dotnet test src/CcDirector.Gateway.UnitTests` (the whole suite) | 11,489 passed, 3 failed, 2 skipped of 11,494. The three reds are below |
| `python3 -m pytest tools/cc-devthrottle/tests -q -k "help or schedule or factory or axi"` | 2,731 passed, 2 skipped |

**How the commands were run here, where it differs from the mission document:**

- **The .NET tests ran with `DOTNET_USE_POLLING_FILE_WATCHER=1`.** Without it, 109 tests failed at start-up with "The
  configured user limit (128) on the number of inotify instances has been reached": every test that builds a web
  host takes a file-watch slot, and this machine's other sessions had used them. The failures were in tests this
  branch does not touch as well as in its own, and none of them was an assertion. With the variable set, all pass.
- **The .NET kit is version 10 from `~/.dotnet`**; the machine's own `dotnet` is version 8.
- **pytest ran inside a throwaway environment** (`uv run --no-project --with pytest --with typer --with click --with
  rich --with requests --with ./tools/cc_storage --with ./tools/cc_shared python -m pytest ...`), because this
  machine's Python has no pytest.

**The three reds in the whole Gateway unit suite are in files this branch does not touch**, and none is reached by
the mission's check: `VoiceUnheardCountTests.NotePlayed_AResetWhoseSaveFailed_IsWrittenAgainByTheNextPlay`,
`VoiceAutoOffLineTests.ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt` and
`HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`.
Each makes a save fail and expects the failure; on this Linux machine the save does not fail. They fail the same way
run alone. They were not run on origin/main here, so "red on main too" is likely and not proven.

**The one red in the Cockpit suite is not this branch's.** `src/licenses/fixtures.test.ts` compares
`apps/cockpit/src/licenses/fixtures.ts` with `missions/license-switching/cockpit-api.md` and finds 28 examples where
it expects 16. This branch changes neither file (`git diff origin/main -- apps/cockpit/src/licenses missions` is
empty), so the test is red on origin/main too. It belongs to the License Switching mission.

## Revert proof

With the work committed, the factory rule was taken out of the unlink route (the `FactoryNaming.TryAct` call in
`CronJobEndpoints.cs`), the Gateway rebuilt and `FactorySeatRoutesTests` run: 3 failed, 15 passed. The three are
exactly the refusals - a session outside the factory, a session in no factory, and a session the Gateway does not
know yet. Put back and rebuilt: all 54 `FactorySeat` tests pass.

## Screenshots, from a local Gateway

`capture.py` took them and can be run again; its first lines say how. It starts a throwaway Gateway on port 7991
with its own data folder, registers three demonstration factories, drives headless Chrome, and stops the Gateway. It
never touches the owner's Gateway, factories or schedules. Each picture exists at 1280 pixels wide (`-desktop`) and at
375 (`-phone`, with the Cockpit's menu collapsed).

The fixture: **Seat Demo** has a Boss with no schedule, a Scout with one, and a Writer with two, one of them paused.
**Talk Only Demo** has two seats and no schedule. **Paused Demo** has one seat whose only schedule is switched off.

| File | What it shows |
| --- | --- |
| `seat-with-a-schedule-*.png` | the Scout's page, opened from its row: the head with "Scheduled", the brief's path and computer and the sentence that the Gateway does not hold it yet, the schedule with Edit schedule, Pause and Take off this seat, the goal, and what it has left behind |
| `seat-with-no-schedule-*.png` | the Boss's page, opened from its row: "Talk only" in the head, "Talk only. Nothing is scheduled for this seat. You run it by talking to it.", and every other panel present - the brief, the goal, its last talk |
| `seat-with-two-schedules-one-paused-*.png` | the Writer: two schedules, the second marked Paused with Resume |
| `seat-schedule-missing-NOT-GATEWAY-DATA-*.png` | a seat whose schedule the Gateway does not have: "Schedule missing", the sentence, and no button. **Not the Gateway's own data** - see below |
| `seats-tab-open-seat-*.png` | the Seats tab: Open seat under every seat, and "Talk only" where the Boss's schedules would be |
| `factories-talk-only-beside-paused-*.png` | the cards: Paused Demo under Paused, Seat Demo under Running on its own, Talk Only Demo under Talk only, and the count "1 talk only" |
| `factory-page-talk-only-*.png` | Talk Only Demo's page: TALK ONLY and "Nothing scheduled. You run it by talking to it." |
| `take-off-1-confirm-*.png` | before: the confirm, in the Gateway's four sentences |
| `take-off-2-after-*.png` | after: the Gateway's sentence at the top, and the Scout now reads Talk only |
| `take-off-3-listed-outside-any-factory-*.png` | after: the schedule that kept running is listed under "Schedules outside any factory" |
| `take-off-refused-already-off-*.png` | a failure case: the schedule was taken off elsewhere while the confirm was open; the Gateway's refusal stays in the confirm |
| `seat-the-factory-does-not-have-*.png` | a failure case: a seat the factory does not have shows the Gateway's sentence, not a blank page |

**One picture is not from the Gateway's own data.** A seat that names a schedule the Gateway does not have cannot be
produced on a real Gateway since issue 3650: a seat's schedule list is derived from the schedules themselves. The page
must still draw it, so for `seat-schedule-missing-NOT-GATEWAY-DATA` the browser was handed the Scout's real page with
its schedule replaced by exactly what the fold returns for a missing schedule. The words are proven by
`FactorySeatPageFoldTests.Page_ASeatWhoseScheduleIsMissing_SaysSo_AndOffersNothingToPress`; the picture proves only
that the Cockpit draws them.

## The Gateway's answers, as sent

`answers.json` holds every answer below exactly as the throwaway Gateway sent it during the run that took the
screenshots.

**The status words** (`GET /gateway/factories`):

```
paused-demo     PAUSED     rank 2  "1 schedule switched off"
seat-demo       RUNNING    rank 3
talk-only-demo  TALK ONLY  rank 4  "Nothing scheduled. You run it by talking to it."
```

**A schedule taken off its seat** (`POST /cron/jobs/{id}/unlink`, HTTP 200):

```
"text": "Taken off Scout of Seat Demo. 'Seat Demo Factory - Scout - morning scan' is still switched on and keeps
         running at its times, as a plain schedule in no factory."
"factory": "seat-demo", "seat": "scout"
```

and the schedule read back afterwards: `"enabled": true, "factory": null, "seat": null, "cronExpression": "15 6 * * *"`.

**Taken off a second time** (HTTP 409):

```
"error": "'Seat Demo Factory - Scout - morning scan' is on no seat of any factory, so there is nothing to take off."
```

**A seat the factory does not have** (`GET /gateway/factories/seat-demo/seats/owner-session`, HTTP 404):

```
"error": "Seat Demo has no seat 'owner-session'. Its seats are: boss, scout, writer. Only a registered seat has a
          page; what any other session wrote is on the factory's Activity tab."
```

**The command line, against the same Gateway:**

```
$ cc-devthrottle schedule unlink cj_...            exit 0
Taken off Scout of Seat Demo. 'Seat Demo Factory - Scout - morning scan' is still switched on and keeps running at
its times, as a plain schedule in no factory. (cj_...)
help[3]:
  cc-devthrottle schedule disable cj_...
  cc-devthrottle schedule get cj_...
  cc-devthrottle schedule link cj_... --factory seat-demo --seat scout

$ cc-devthrottle schedule unlink cj_...            exit 1   (the same schedule again)
Error: 'Seat Demo Factory - Scout - morning scan' is on no seat of any factory, so there is nothing to take off.

$ cc-devthrottle schedule unlink cj_nowhere        exit 1
Error: no such cron job
```

**A session outside the factory, refused.** This answer is from `FactorySeatRoutesTests.
Unlink_ByASessionOutsideTheFactory_IsRefusedInThePauseRulesOwnWords_AndNothingChanges`, which sends the request over
real HTTP to the real route as a session of `invoice-factory`, and asserts the whole body and that the schedule and
the seat's list are unchanged afterwards. HTTP 403:

```
"error":  "this schedule belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change
           or run it"
"detail": "Everything on a schedule decides what a session of 'warmforward' will be told to do - its prompt, where
           it runs, and when - and that session may read and write that factory's memory. Changing or running it
           from outside the factory would therefore direct one of its members. A schedule with no factory on it is
           open to any session, as before."
```

It is the sentence `FactoryNaming.ActRefusal` writes for pausing or editing the same schedule; the test asserts they
are equal.

## Not proven here

- **The session refusal against a running Gateway with a real session key.** A session key is registered by a
  Director over its tunnel, and the throwaway Gateway has no Director. The refusal is proven by the route test above,
  which puts the session's identity where the Gateway's sign-in check puts it. That the sign-in check itself lets a
  session key reach the route is proven only at `SessionKeyGuard.Check`, not end to end.
- **The hosted Gateway.** Nothing was run against PostgreSQL or with tenants other than the local one. No table or
  column was added; the unlink writes two existing columns.
- **A schedule that is missing from the Gateway, end to end.** See "One picture is not from the Gateway's own data".
- **Talk and Edit schedule from the seat page, in a browser.** The throwaway Gateway has no Director, so a talk cannot
  start there. Both buttons are the Seats tab's own components and are covered by the Cockpit tests.
- **A seat started by a trigger, in a screenshot.** The fixture has no trigger. The words are proven by the fold test
  and the Cockpit test.
- **The memory count above zero, in a screenshot.** The fixture's factories have no memory notes. The count is proven
  by the fold and route tests.
- **Mobile.** The phone app has no Factories screens; the phone pictures are the Cockpit at 375 pixels.
- **A factory that is not registered.** The old agent page worked for any factory agent that had written an activity
  row. The seat page is for registered seats: the old address of an agent that is no registered seat now shows the
  Gateway's 404 sentence. Its rows are still on the Activity tab.
