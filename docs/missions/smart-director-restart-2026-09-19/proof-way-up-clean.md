# Proof - the restart screen makes sense, and records stop piling up

Developer seat, 20 September 2026. Branch `smart-restart/way-up-clean`, cut from `origin/main` at
`e188d3048`. Issues: #3167 (the mission) and #3230 (records offered again for ever).

The pictures are in `attachments/way-up/`. The five new ones - 9 to 13 - are the states the owner
will ask about, and every word in them is the real engine's: each builds a stored record and asks
`DirectorWayUp` to word it, rather than the test writing the sentences itself.

---

## Half one - the screen reads correctly

### What he saw, and what is on the screen now

He photographed the offer and could not read it. The headline said "One session is waiting to be
brought back" and the list below held seven rows: one he could act on and six greyed
"ended without a handover". The date and time were on the screen, second line, in secondary grey, and
he read straight past them: "we're totally missing a date and time when that was saved."

Picture 9 is that same record now: one row under a line that counts one row, the date in primary text
directly under the headline, and the six behind one amber line saying "6 sessions ended without a
handover." Picture 10 is that line opened.

### The five changes, and why each one

1. **The main list holds only what can be brought back.** `WayUpOfferViewModel` splits the engine's
   rows on the kind the ENGINE stamped - `BringBackRows` and `EndedRows` - and the window draws the
   first as the main list. Splitting on a stamped kind is layout; no sentence is chosen in the window
   and no row is dropped.
2. **The sessions that ended without a handover sit behind one line, whole.** Ruling 10.3 is kept in
   full: each is still listed, still unticked, still carries its one reopen button. The disclosure is
   the house pattern (`ToggleButton.disclosure`, copied from `Controls/GatewayConnectionPanel.axaml`
   rather than invented, so the two read alike), and its line, its detail and whether it starts open
   all come from the engine (`WayUpRecord.EndedSectionLabel`, `EndedSectionDetail`,
   `EndedSectionStartsOpen`).
3. **The headline and the list agree.** `WayUpWords.SeatsLabel` now takes one count and names one
   thing: what the main list holds. The other count moved to the section it sits above. A person
   reading either line and counting the rows under it gets the same number, and
   `Each_count_names_exactly_the_rows_it_sits_above` asserts both against the rows the engine built.
4. **The date and time are where the eye lands** - primary `#CCCCCC`, 14 point, SemiBold, directly
   under the headline, with nothing drawn between them.
5. **A shutdown with no reason says nothing.** `WayUpWords.ReasonLabel` answers null instead of "No
   reason was given.", and both windows draw no line at all for a null. It is the engine's decision
   and not each window's, because a window that hid a sentence it did not like would be deciding what
   a state means (critical rule 7).

### When the record holds NOTHING that can be brought back

This is the case ruling 10.5 creates: the operating system shuts the machine down, there is no ten
minutes, and every seat is written as ended at the limit. The mandate asked me to decide what the
window says and say why. Picture 11 is the answer:

- the count line says **"No session handed over, so there is nothing to bring back."** - true, and it
  explains the empty space rather than leaving it;
- the ended section is **open from the start**, because it is the whole window. An empty main list
  above a shut line would read as "nothing here", which is the reading the owner must never get;
- **no Bring back button is drawn at all.** The engine says whether there is anything to bring back
  (`WayUpRecord.CanBringBackAnything`), and on such a record the only answer that button could ever
  collect is the engine's refusal "no row was ticked". A button whose every press is a refusal reads
  as broken, so it is not drawn, and the keyboard focus goes to "Not now".

Everything else is unchanged: the rows are unticked, the restore still refuses them, and nothing comes
back by itself.

---

## Half two - records stop piling up (#3230)

### 1. A seat is marked when it is dealt with

Half of this already worked: a seat BROUGHT BACK carries `RestoredSessionId`, written by a restore
mark, and that survives a restart. A seat whose saved conversation was REOPENED carried nothing. The
only guard was a `HashSet` inside the engine's own process, which a restart empties - so every morning
offered the same dead sessions again, which is exactly what the owner met.

The fix is a new restore mark, `WorkspaceRestoreMarkKinds.Reopened`, writing three provenance fields
on the seat: `ReopenedAtUtc`, `ReopenedSessionId`, `ReopenedByDirectorId`.

- **Provenance, not judgment.** They are written only by `WorkspaceStore.RecordRestoreMark`, and
  `RestoreStoredMarks` puts the stored copy back over any ordinary write - the same treatment
  `RestoredSessionId` gets, for the same reason. A writer who could set the field would make a session
  vanish from the offer; one who could clear it would put a dealt-with session back into it.
- **The claim goes in BEFORE the create is sent**, and which session took it is written afterwards.
  The order is the point: a claim written only after a successful start would be missing for exactly
  the start whose answer never came back, which is the case a claim exists for. A claim with no
  session id is said as it is in the history - "Which session took it was never recorded" - rather
  than left blank.
- **The in-memory `HashSet` is gone**, not kept beside the new mark. Two guards for one rule can only
  produce two different refusals for the same state.
- **The reopen mark needs no restore lease**, deliberately. The lease is granted only by asking for a
  restore, so a reopen that needed one could never be recorded at all; and a reopen is not a restore -
  it starts one session and there is no ordering for two Directors to interleave. What it must not do
  is cut across a restore, so it is refused while ANOTHER Director holds a live lease, and it never
  grants, renews or releases one.

### 2. A record stops being offered once every seat in it is dealt with

`EndedWithoutHandover` now excludes a seat carrying a reopen claim, exactly as it already excluded one
carrying a restored session id. Both counts, the rows and the offerability check read that one filter,
so a dealt-with seat leaves the offer everywhere at once. When the last seat goes, the record itself
stops being offered. Picture 12 is such a record in Restart history: no offer, no button, and each
seat saying what became of it.

### 3. A record stops being offered once it is older than seven days

`DirectorWayUp.OfferedForDays = 7`, measured from when the shutdown was - the moment the window shows
him - so the sentence and the cut-off count the same thing. Seven is the Delivery Lead's number and I
have no reason to think it wrong: it is a working week plus a weekend, so a restart put off over a
weekend is still offered on the Monday.

**The age gates the start-up offer and nothing else, and that is a deliberate difference between the
two surfaces.** In Restart history an old record still carries its offer, with the engine's own
sentence saying why it stopped appearing by itself (picture 13). The owner's stated reason for having
a history at all is "it could be that I accidentally don't restart it right away and I want to restart
it later" - a cut-off that also took the button away would defeat that on day eight. To keep the
Delivery Lead's "one rule, never two that can drift apart", the question "is there anything to act on"
stays a single rule, `HasSomethingToActOn`, that both surfaces read; the age is a separate, named rule,
`IsOfferedAtStartUp`, about interrupting the owner at start-up, and only the start-up read asks it.

Nothing is deleted anywhere. The history still reads every record, whatever its kind, age or outcome.

---

## What happens against a Gateway that does not know the new mark

The hosted Gateway checks the mark name against a closed list (`WorkspaceRestoreMarkKinds.All`, read
in `WorkspaceStore.RecordRestoreMark`). One older than this Director answers **HTTP 400** with
`kind must be one of: started, restored, failed, finished.`

Against such a Gateway, pressing "Reopen its saved conversation":

- **starts nothing at all.** The claim is written first, so a claim that is refused stops the reopen
  before any session is created;
- **says so, in the Gateway's own words.** The message is: *"Nothing was reopened: '<the session>'
  could not be marked as dealt with on the record (kind must be one of: started, restored, failed,
  finished.). Until it can be, reopening it would leave this session offered back to you after every
  restart, so it is not started. A Gateway older than this Director does not know the mark; it accepts
  it once it is up to date."*
- **has no fallback.** There is deliberately no path that starts the session anyway and hopes, because
  that is the defect the mark exists to end (CLAUDE.md: no fallback programming).

The seam tells the three answers apart by STATUS and never by reading the sentence: 2xx is recorded,
409 is "already dealt with" (an ordinary outcome, the guarantee working), and everything else is a
refusal carrying the Gateway's words through unchanged. `GatewayClient.RecordReopenMarkAsync` answers
the status rather than throwing, for that reason.

Everything else in this change works against an older Gateway today: the screen, the counts, the seven
day cut-off and "every seat dealt with" all read fields the record already carries. Only the reopen
needs the deploy, and the deploy is the owner's separate decision.

---

## The counts

Both commands run in this worktree, before and after. Read the count, not the colour.

| Command | Before (`origin/main` at `e188d3048`) | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 637 passed, 0 failed | **651 passed, 0 failed** |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 147 passed, 0 failed | **152 passed, 0 failed** |

The before numbers are the Delivery Lead's own, re-measured here rather than taken from its report.

`dotnet build cc-director.sln` is clean: no errors and no new warnings.

### The new tests, in plain words

**Engine and screen wording** (`Restart/DirectorWayUpOfferTests.cs`,
`Restart/DirectorWayUpHistoryTests.cs`):

- *Each count names exactly the rows it sits above* - the line above the main list counts the main
  list, the section line counts the section, and both are asserted against the rows the engine built.
- *A record with no reason carries no reason line* - the engine answers null rather than a sentence.
- *A reopened seat is off the offer and its reopen is readable in the history* - a dealt-with seat is
  not listed again, and the history says when its conversation was reopened and as what.
- *A record every seat of which is dealt with is not offered but is still in the history* - one seat
  restored, one reopened, nothing offered, both still readable.
- *A record older than seven days is not offered at start-up and says so in the history* - both
  halves: it stops interrupting him, and it keeps its offer with the sentence saying why.
- *The seven day cut-off is counted from when the shutdown was* - asserted from BOTH sides of the
  boundary, because a cut-off tested only well inside one side passes with the comparison the wrong
  way round.
- *Reopening a seat dealt with before the restart is refused from the record* - nothing in this test
  reopens anything; the record ARRIVES carrying the claim, as it would after a restart. This is
  #3230 itself.
- *The reopen is claimed on the record before any session is started* - asserts the ORDER of the two
  marks, which is what makes a lost answer safe.
- *A reopen the record cannot be marked with starts nothing and says why* - the older Gateway.
- *Reopening the same seat twice starts only one session* - kept, now reading the record rather than
  process memory.

**The store** (`Restart/SmartRestartReopenMarkStoreTests.cs` - named to match the mission's own filter,
because a test the mission never runs proves nothing):

- a reopen mark is written with no restore lease and takes none;
- the claiming Director may fill in which session took it, and the claim keeps the moment it was
  CLAIMED;
- a second reopen is refused by name, and another Director cannot complete somebody else's claim;
- a reopen is refused while another Director is restoring, and its own lease is no obstacle;
- an ordinary write can neither forge a reopen nor erase one - both directions, because they fail
  oppositely;
- a mark kind this build does not know is refused with the kinds it does, and this build's list now
  names the reopen.

**The window** (`SmartRestart/WayUpOfferWindowTests.cs`):

- *The main list holds only the rows that come back* - and the others are not drawn at all until asked
  for, with the shut caret beside their line.
- *Opening the ended section reveals every row unticked with its own button* - through the real
  disclosure control and its real two-way binding, not by setting the view model; and shutting it again
  changes no words.
- *The date and time are drawn in primary text right under the headline* - colour, size, weight and
  position, which is exactly what he read past.
- *A record with no reason draws no reason line at all.*
- *A record with nothing to bring back draws no bring back answer* - and the record that has something
  still draws it.

**Pictures** (`SmartRestart/WayUpScreenshotTests.cs`): five new ones, each proved to be a real drawing
(the window background, the panel background, more than a hundred distinct colours), and picture 10
additionally asserts that the six rows and five reopen buttons are really in it - "more than a hundred
colours" cannot say that.

---

## The revert proofs

Each one reverts the production change, rebuilds, and shows the named tests go red; the tree is
restored immediately after and the two suites above are the restore run, built from source. No run in
this document used `--no-build`.

| # | What was reverted | What went red |
|---|---|---|
| R1 | The main list binds to every row again | 7 failed / 152 - including *the main list holds only the rows that come back*, and the picture test |
| R2 | `ReasonLabel` says "No reason was given." again | 1 failed / 651 - *a record with no reason carries no reason line* |
| R3 | The date goes back to `#AAAAAA` at 13 point | 1 failed / 28 - *the date and time are drawn in primary text* |
| R4 | A reopened seat is listed and offered again | 2 failed / 651 - the two "dealt with" tests |
| R5 | The reopen starts a session without claiming it | 4 failed / 651 - every reopen claim test |
| R6 | `OfferedForDays` set to 3650 | 2 failed / 651 - the two age tests |
| R7 | The store stops restoring the reopen fields over a write | 1 failed / 651 - *an ordinary write can neither forge a reopen nor erase one* |

The mutation script fails loudly when the text it removes is not present, because a mutation that
silently did nothing would produce a green run that reads exactly like a test which does not cover the
change.

---

## What this does NOT prove, said plainly

- **Nothing here ran against a real Gateway or a real Director.** The engine is proved through its two
  seams and the store through its own database harness. The older-Gateway behaviour is proved by the
  seam answering a refusal and by the store refusing an unknown kind - not by pointing this build at a
  deployed Gateway, which I am not allowed to do.
- **The pictures prove what the windows DRAW, not that they look right.** A person opens the files for
  that. Pictures 9 to 13 do carry the real engine's words; pictures 1 to 8 are the earlier phase's, and
  their words are the tests' own stand-ins.
- **The window test for the missing reason line does not exercise `WayUpWords`**, and the engine test
  for it does not exercise the window. They are deliberately two tests: under revert R2 the window test
  stayed green, which is the split working, not a gap.
- **`Gateway.UnitTests` is parked out of the default local gate**, so `.\scripts\test-local.ps1` says
  nothing about the 651 above. The run in this document is the coverage for it.
- **Nothing measures what a reopened conversation really comes back with.** That is the rig's, and this
  change does not touch it.

## One thing I could not reach, and it is not mine

`tools/cc-devthrottle/tests/test_smart_restart.py` has **10 failing tests out of 30 on this machine,
and they fail identically with and without my change** - I checked by restoring that file from
`origin/main` and running them again: the same ten, 10 failed / 20 passed both ways. Every one fails
with `ValueError: stderr not separately captured` raised inside `click/testing.py`, which is the
installed click version not separating stderr for `CliRunner`, not a product defect. My own change to
that file is one line: `notOfferedAtStartUpLabel` added to the keys the restart history prints, which
that loop already skips when the value is absent. Worth somebody pinning the click version; it is
outside this task.
