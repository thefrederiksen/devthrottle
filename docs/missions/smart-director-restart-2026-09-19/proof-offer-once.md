# Proof - the restart offer appears once, and can be cleared

Developer seat, 20 September 2026. Branch `smart-restart/offer-once`, cut from `origin/main` at
`986557607`. Issue: #3167 (the mission).

The owner's ruling, in his own words, is the whole of what this change implements:

> I think as soon as the restart history as soon as we have used a restart It should no longer be
> offered on startup We can keep the history around for 7 days But then the user would have to go to
> a menu in the file system and saying open from old restart So this means that automatically we
> should only see this restart message once if we use it The user should also be able to clear it if
> they don't want it right it could be that they shut down but they don't want to use it and they
> don't want to see it on every upstart

The pictures are in `attachments/way-up/`. Picture 9 is the offer with the new action on it, picture
14 is a record that was used, picture 15 is a record he cleared. Every word in those three is the
real engine's: each builds a stored record and asks `DirectorWayUp` to word it.

---

## What changed

### 1. Used once means never offered again

`DirectorWayUp.IsOfferedAtStartUp` now asks FOUR questions, and every one of them is about
interrupting the owner at start-up rather than about what the record holds:

| Rule | What it answers |
|---|---|
| `HasSomethingToActOn` | is there anything left to act on at all (unchanged, and still the ONE actionability rule both surfaces read) |
| `HasBeenUsed` | **new** - has anything been brought back or reopened from this record |
| `IsClearedFromStartUpOffer` | **new** - has he asked not to be offered it |
| `IsTooOldToOffer` | is it older than seven days (unchanged) |

`HasBeenUsed` reads the SAME two marks everything else reads and adds no third way of deciding: a
seat that came back carries `RestoredSessionId`, written by a restore mark, and a seat whose
conversation was reopened carries the `Reopened` claim from pull request 3245. Both are written only
by the Gateway and both survive a restart. The mandate's "reuse them; do not add a second way" is
kept literally - `EndedWithoutHandover`, the counts, the rows and the offerability check still read
one filter, and the new rule reads the same two fields that filter reads.

**The deliberate consequence, said plainly.** A record with ONE seat brought back and SIX never
touched is no longer offered at start-up, and those six leave the start-up offer with it. That is
not a side effect - it is the ruling. Under yesterday's rule ("a record stops being offered once
EVERY seat in it is dealt with") those six were six reasons to put the window in front of him again
at every start, which is exactly what he asked us to stop.

**Nothing is lost, and the tests assert both halves in the same breath.** The six stay in the record,
they stay in File, Restart history, and every button they had still works there -
`The_six_untouched_seats_are_still_reachable_in_the_restart_history` reads the history entry, counts
six rows that ended without a handover, and asserts each one still carries "Reopen its saved
conversation". If the first half is ever shipped without the second, that test goes red.

### 2. He can clear it without using it

A third answer on the offer: **"Don't ask again"**. It records a clearing on the record and does
nothing else.

- **On the record, not in this process.** `WorkspaceDocument.ClearedFromStartUpOfferAtUtc` and
  `ClearedFromStartUpOfferByDirectorId`, written only by a restore mark of the new kind
  `WorkspaceRestoreMarkKinds.Cleared` and restored over every ordinary write - the same treatment the
  reopen claim gets, for the same reason: a writer who could set it would make a record vanish from
  his start-up, and one who could clear it would put a record he has dismissed back in front of him.
  A clearing this Director remembered in its own memory would be forgotten by the very restart it
  exists to survive; that was product issue 3230 for the reopen.
- **It needs no restore lease and names no seat.** The lease is granted only by asking for a restore,
  and clearing is not a restore - it starts nothing and brings nothing back - so a clearing that
  needed one could never be recorded at all. What it must not do is cut across a restore that IS
  running, so it is refused while ANOTHER Director holds a live lease, exactly as a reopen is, and it
  never grants, renews or releases one.
- **Pressing it twice is not an error.** The first clearing's moment and Director stand and the
  answer is success. This is deliberately unlike a second reopen, which is refused: a second reopen
  would put a second agent into one saved conversation, which is real harm, while after either
  clearing the record has stopped appearing, which is the whole of what was asked for. Refusing it
  would hand him a red sentence for pressing a harmless button.
- **It is drawn only where pressing it would change something.** A record already cleared, and one
  already used, have both stopped interrupting him for good, so the engine answers
  `CanClearFromStartUpOffer: false` and the window draws neither the button nor its sentence. A
  button whose press changes nothing reads as broken.
- **Nothing is deleted.** No seat, no decision, no saved conversation, no record. The history reads
  it in full, with its offer and working buttons (picture 15).

### 3. The history says which of the three reasons stopped it appearing

`NotOfferedAtStartUpLabel` used to answer one sentence - too old. It now answers one of three, and
because a record can carry more than one they are RANKED: **used, then cleared, then too old.**

Used comes first because it is the strongest thing that happened - sessions came back out of this
record - and it is what he is most likely to be asking about. Cleared comes next, because it is his
own act and he may well have forgotten it. Age comes last, being the only one nobody did on purpose.
`A_record_that_was_used_cleared_and_aged_out_says_it_was_used` builds a record carrying all three at
once and asserts the order, which is the only way to prove which wins.

A record with nothing left to act on still gets no sentence here, because it already has its own in
`OutcomeLabel`, and two sentences saying the same thing in different words on one entry is what this
screen was cleaned up to stop.

---

## The exact words now on the window, and why they distinguish the three actions

Bottom right, unchanged, the primary pair:

- **Bring back** - brings the ticked rows back.
- **Not now** - writes NOTHING at all. The record is offered again the next time the Director starts.
  Its meaning is unchanged, and `Not_now_writes_nothing_and_the_record_is_offered_again_next_time`
  asserts that the engine is asked for nothing at all: no clearing, no reopen claim, no restore.

On its own line above them, on the LEFT, under a divider:

- **Don't ask again**, with this sentence beside it, in secondary grey, from `WayUpWords.ClearDetail`:

  > Not now asks you again the next time the Director starts. This stops it asking at all: nothing is
  > brought back and nothing is deleted, and the record stays in File, Restart history.

**Why those words.** The mandate asks for two things of them, and the sentence does both in one line.
It NAMES "Not now", so the difference between "ask me again next time" and "stop asking" is read off
the window rather than out of a manual - no reader has to hold both buttons in their head and guess.
And it says what is NOT done - nothing brought back, nothing deleted - because "clear" is the word
most likely to be read as "delete", and the owner's own rule is that nothing is ever deleted. The
button itself says "Don't ask again", which is his own phrasing of the case ("they don't want to see
it on every upstart") and is not a verb that could be read as destroying anything.

What is said afterwards repeats the same three things, because that is the moment he is most likely
to fear he has thrown something away (`WayUpWords.ClearedMessage`):

> You will not be asked about this again when the Director starts. Nothing was brought back and
> nothing was deleted: the record is still in File, Restart history, with every session it holds and
> every button it had.

**Where it is, and why there.** Picture 9 shows it: the clearing action is on its own line, on the
left, under a divider, with the whole width of the window between it and the primary pair at the
bottom right. It is drawn in secondary grey and never in the primary blue, because it is not the
thing he came to the window to do.
`Show_TheClearingAnswer_IsDrawnOnItsOwnLineAwayFromThePrimaryPair` asserts that geometry on the real
opened window - its far corner is above and to the left of where both other buttons begin - rather
than asserting that three buttons exist.

Once any answer has come back, all three are replaced by "Close". Leaving "Don't ask again" on screen
after a bring back would offer a third thing to do about a record he has just used, and it is one the
engine would refuse.

---

## What happens against a Gateway that does not know the new mark

The hosted Gateway checks the mark name against a closed list (`WorkspaceRestoreMarkKinds.All`, read
in `WorkspaceStore.RecordRestoreMark`). A Gateway older than this Director answers **HTTP 400** with
`kind must be one of: started, restored, failed, finished, reopened.` - and, if it is older still, a
list that does not name `reopened` either.

Against such a Gateway, pressing "Don't ask again":

- **changes nothing at all.** No record is marked, no session is started, nothing is deleted;
- **says so, and says he will be asked again** (`WayUpWords.ClearRefusedMessage`):

  > Nothing was changed: this record could not be marked as cleared (kind must be one of: started,
  > restored, failed, finished.). Nothing has been deleted and nothing has been brought back, and you
  > WILL be asked about this again the next time the Director starts. A Gateway older than this
  > Director does not know the mark; it accepts it once it is up to date.

- **has no fallback.** There is deliberately no path that answers "you will not be asked again" when
  the record carries nothing of the kind. That is the one outcome this must never have: he would meet
  the same window at the next start with no idea why, which is worse than the state he complained of.

A Gateway that will not answer AT ALL is a separate case and is tested separately
(`A_clearing_a_gateway_never_answered_changes_nothing_and_says_why`): the two fail differently - one
answers and refuses, the other never answers - and both could otherwise read as success.

**Everything else in this change works against an older Gateway today.** "Used once" reads
`RestoredSessionId` and the reopen claim, both of which the record already carries; the three history
sentences are computed in the Director. Only the CLEARING needs the deploy, and the deploy is the
owner's separate decision. Note that the reopen from pull request 3245 is also still waiting on that
same deploy.

---

## The counts

Both commands run in this worktree, before and after. Read the count, not the colour.

| Command | Before (`origin/main` at `986557607`) | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 666 passed, 0 failed | **686 passed, 0 failed** |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 152 passed, 0 failed | **159 passed, 0 failed** |

The before numbers were measured here, on this branch's base commit, rather than taken from
yesterday's report - which said 651 and 147 against an older base.

`dotnet build cc-director.sln` is clean: no errors and no new warnings.

### The new tests, in plain words

**The engine** (`Restart/DirectorWayUpOfferOnceTests.cs` - named so the mission's own filter runs it):

- *A record with one seat brought back and six untouched is not offered again* - the owner's own
  shape. It also asserts the record still HOLDS six seats that can be acted on, so the test cannot be
  satisfied by a record that simply became empty.
- *The six untouched seats are still reachable in the restart history* - the other half of the same
  rule: six rows, each still carrying its reopen button, and the entry saying why it stopped
  appearing.
- *A record with one conversation reopened and the rest untouched is not offered again* - a reopen
  counts as using it, exactly as a bring back does; the seat still waiting to come back is still
  offered from the history.
- *Reopening one seat takes the whole record off the start-up offer* - proved by asking the ENGINE to
  reopen a seat and then asking it for the offer again, not by handing it a record already marked. A
  test that only hand-built the marked record would stay green if the reopen stopped writing them.
- *Bringing one row back takes the whole record off the start-up offer* - the bring back really runs,
  really writes its seed file into a real folder, and really hands the restore its order.
- *A cleared record is not offered and brings nothing back* - and nothing was started, no restore was
  ordered, every seat is still there and none of them was marked.
- *A cleared record says so in the history and still offers everything it held.*
- *Not now writes nothing and the record is offered again next time* - no clearing, no reopen claim,
  no restore order.
- *A clearing the record cannot be marked with changes nothing and says why* - the older Gateway, and
  the record is still offered afterwards.
- *A clearing a Gateway never answered changes nothing and says why.*
- *Clearing a record that belongs to another Director changes nothing* - and nothing is marked, the
  refusal coming before the mark.
- *Clearing a record that is no longer there changes nothing.*
- *The clearing answer is offered only where pressing it would change something* - true on an
  untouched record, false on a used one and on a cleared one, with the sentence null in both.
- *The clearing answer says on the window how it differs from Not now* - the sentence names the other
  answer, says "stops it asking at all", and says nothing is deleted.
- *A record that was used, cleared and aged out says it was used* - the ranking, proved from a record
  carrying all three.

**The store** (`Restart/SmartRestartClearMarkStoreTests.cs`):

- a clearing mark is written with no restore lease, names no seat, takes no lease, and takes nothing
  away from the record;
- a second clearing keeps the first moment and is not an error;
- a clearing is refused while another Director is restoring, and its own lease is no obstacle;
- an ordinary write can neither forge a clearing nor erase one - both directions, because they fail
  oppositely;
- this build's list of mark kinds names the clearing, and a kind it does not know is refused with the
  kinds it does.

**The window** (`SmartRestart/WayUpOfferWindowTests.cs`):

- *The clearing answer is drawn on its own line away from the primary pair* - the real geometry of the
  opened window.
- *It draws the engine's own sentence beside it* - the window is handed a sentence no engine would
  produce and draws it unchanged.
- *A record the engine says cannot be cleared draws no clearing action at all* - and the two answers
  are untouched.
- *Pressing it reaches the engine with this record's id and shows its answer* - off the interface
  thread, and nothing was brought back or reopened by it.
- *A clearing the engine refused shows the refusal and not a sentence of its own.*
- *After an answer has come back the clearing answer is gone with the others.*
- *Not now asks the engine for nothing at all.*

**Pictures** (`SmartRestart/WayUpScreenshotTests.cs`): picture 9 now carries the third action and the
test asserts the button, its words and the engine's sentence are really drawn; pictures 14 and 15 are
new, each proved to be a real drawing.

---

## The revert proofs

Each one breaks ONE production line, rebuilds, and shows the named tests go red; the source is
restored in a `finally`, so nothing - a failed build, a git warning, an exception - can end a run with
the mutation still in the tree, and the two suites above are the restore run, built from source. No
run in this document used `--no-build`. The mutation fails loudly when the text it removes is not
present, because a mutation that silently did nothing produces a green run that reads exactly like a
test which does not cover the change. Each run is the mission's own full filter, never a narrower one.

| # | What was reverted | What went red |
|---|---|---|
| R1 | `IsOfferedAtStartUp` stops asking `HasBeenUsed` | 5 failed / 686 - every "used once" test, plus the history test that reads the offer from the history |
| R2 | `IsOfferedAtStartUp` stops asking `IsClearedFromStartUpOffer` | 1 failed / 686 - *a cleared record is not offered* |
| R3 | `NotOfferedAtStartUpLabel` stops naming "used" | 3 failed / 686 - the two history sentences and the ranking |
| R4 | The store's clearing branch writes nothing | 4 failed / 686 - every clearing store test |
| R5 | The store stops restoring the clearing over an ordinary write | 1 failed / 686 - *an ordinary write can neither forge a clearing nor erase one* |
| R6 | A refused mark answers success anyway (the fallback the rule forbids) | 1 failed / 686 - *a clearing the record cannot be marked with changes nothing and says why* |
| R7 | The record always says it can be cleared | 1 failed / 686 - *offered only where pressing it would change something* |
| R8 | The clearing action loses its visibility binding | 2 failed / 159 - *draws no clearing action*, and *gone with the others* |
| R9 | The window invents its own sentence and calls the engine on the interface thread | 2 failed / 159 - both press tests |

---

## What I found about the File menu

**It is there, and it reaches everything this change hides from the start-up offer.**

`MainWindow.axaml.cs` line 4615 adds `File, Restart history...`, which opens
`RestartHistoryWindow.ShowForAsync`. That window reads `IDirectorWayUp.ReadHistoryAsync`, which reads
the newest twenty-five captured records for this Director on this machine **whatever their age,
outcome or kind**, and carries the same offer the start-up window makes for every record that still
holds something to act on. None of the three start-up rules is asked there:

- a record that has been USED still carries its offer, with every untouched seat and its button
  (picture 14, and `The_six_untouched_seats_are_still_reachable_in_the_restart_history`);
- a record he CLEARED still carries its offer (picture 15, and
  `A_cleared_record_says_so_in_the_history_and_still_offers_everything_it_held`);
- a record over seven days old still carries its offer (unchanged from pull request 3245, picture 13).

Each of the three says in the engine's own sentence why it stopped appearing by itself, so nothing
vanishes in silence. **I have not renamed the menu item**, as the mandate instructs: the Delivery Lead
has asked the owner whether he wants it called something closer to his own words.

One thing worth saying about it, since it is the "menu in the file system" he is being sent to: the
history reads the newest twenty-five records, and its own sentence says so and says the rest are still
on the Gateway. That cap now matters slightly more than it did, because more records reach the history
without ever having interrupted him. It is not a defect of this change and I have not touched it.

---

## What this does NOT prove, said plainly

- **Nothing here ran against a real Gateway or a real Director.** The engine is proved through its two
  seams and the store through its own database harness. The older-Gateway behaviour is proved by the
  seam answering a refusal and by the store refusing an unknown kind - not by pointing this build at a
  deployed Gateway, which I am not allowed to do.
- **The clearing has never been written by the real `GatewayClientWayUp` against a real Gateway.**
  `MarkClearedFromStartUpOfferAsync` sends the mark down the same route
  (`POST /gateway/workspaces/{id}/restore/marks`) and reads the same statuses the reopen does, and the
  route needed no change to carry a new kind - but the first real write will happen after the deploy.
- **The pictures prove what the windows DRAW, not that they look right.** A person opens the files for
  that. Pictures 9, 14 and 15 carry the real engine's words.
- **`Bringing_one_row_back_takes_the_whole_record_off_the_start_up_offer` writes the restored session
  id onto the seat itself**, because the rig's restore seam does not write to the record - the real
  restore does, through the Gateway. The test proves the bring back really ran and that a restored id
  ends the offer; it does not prove `DirectorRestore` writes that id, which its own tests cover.
- **`Gateway.UnitTests` is parked out of the default local gate**, so `.\scripts\test-local.ps1` says
  nothing about the 686 above. The run in this document is the coverage for it.
- **No web tests and no Python tests were run.** The command line reads the history through
  `SmartRestartHistoryEntryDto.NotOfferedAtStartUpLabel`, which this change fills with the new
  sentences and did not reshape, so nothing there needed a change - but I did not run
  `tools/cc-devthrottle/tests`. Yesterday's proof records ten failures in that suite on this machine
  that are a `click` version problem and not a product defect.
- **There is no command-line verb for clearing**, and none for bringing back or reopening either - the
  command line reads the history and nothing more. Adding one is not in this mandate.
