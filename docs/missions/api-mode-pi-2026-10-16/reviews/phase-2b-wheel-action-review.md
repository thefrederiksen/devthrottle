# Phase 2 follow-on review: the Gateway rules the wheel button (pull request #4133)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2 follow-on, item 4. Reviewer
seat, opened by the Gateway Tech Lead, a different agent family from the Developer, reading only.
Reviewed at the pull request head `e8f670372`, on the branch `api-mode-wheel-action`, one commit
behind `origin/main` (`a6df830f3`, the phase 4 gate records); it merges onto main clean. Date:
17 October 2026.

## Verdict

**Holds.** The code meets the agreed contract field for field and ruling for ruling, the wheel routes
never refuse a button the fold shows as enabled for a reason the fold could have known, the schema
entries match the generator's shape, and the tests prove each ruling. Two findings. Finding 1 is not a
defect in this code: it is the agreed ruling on holder `none` set against what the Director's own
phase 4 branch has already built, and it needs the Tech Lead's decision before any client builds the
button. Finding 2 is a test that proves less than its name says and is a two-line fix.

## Scope: what I read, what I ran, what I could not reach

**Read, in full, on the pull request head:** the whole diff against `origin/main` (five files);
`ApiModeSessionFold.cs` in full, not only the new method, to confirm what `working` means and when
it is true, false or null; `ApiModeDisplayDtos.cs`; `GatewayEndpoints.ApiModeVerbs.cs` in full
(`SessionWheelState.Read`, `RunApiModeVerbAsync`, every refusal and its order); `ApiModeRowStamp.cs`
as the fold's one caller; `ApiModeSessionFoldTests.cs` and `ApiModeVerdictOnTheRosterTests.cs` in
full, with the fixture helpers they build from (`ApiModeEventTestData`); the `WheelHolders` constants
on `SessionDto.cs`; the `schema.ts` entries and their precedent in the same file; the pull request
description; the mission issue's phase 4 row and section 7; the phase 2 wheel verb review and the
display fold review in this folder.

**Read for the Director's side, which this pull request's rulings are made against:** the open pull
request #4094 (branch `origin/apimode/p4-wheel`, commit `b4fd8d012`): its description, and in
`ApiModeSessionBackend.cs` the whole of `TakeWheelAsync` and `HandBackWheelAsync`, every place
`_wheelHolder` is written, and the test names in `ApiModeWheelTests.cs`. That is the only Director
handler for `wheel-take` and `wheel-hand-back` anywhere on the remote; `origin/main` has none.

**Ran:** nothing. The brief forbids test runs on this machine (another seat is testing, and the gate
runs on Windows). The Developer's counts (`ApiModeSessionFoldTests` 17 of 17,
`ApiModeVerdictOnTheRosterTests` 6 of 6) are taken as reported; every test was judged by reading it.

**Could not reach:** a running Gateway, Director or Pi. No client renders the button yet (no file
under `packages/` or `apps/` reads `wheelAction`), so rule 7 is checked on the contract, not on a
screen.

## The contract, checked field for field

| Promised | On the branch | Holds |
|---|---|---|
| `ApiModeSessionDisplay.WheelAction`, type `ApiModeWheelAction`, null means no button | `public ApiModeWheelAction? WheelAction { get; set; }`, assigned null by the fold for `none` and for an unknown holder | Yes |
| `Verb` is `takeTheWheel` or `handBack` | `ApiModeWheelVerbs.TakeTheWheel = "takeTheWheel"`, `HandBack = "handBack"`; the fold assigns only these | Yes |
| `Label` is "Take the wheel" or "Hand back" | The two literals, each beside its verb | Yes |
| `Enabled`, a boolean | `public bool Enabled` | Yes |
| `DisabledReason` null whenever `Enabled` | By construction: only the one object initialiser with `Enabled = false` sets it. Asserted null in three of the four enabled tests (see finding 2) | Yes |
| Wire: `apiMode.wheelAction = { verb, label, enabled, disabledReason }` in camel case | `GetSessions_TheWheelButton_ReachesTheWireByItsCamelCaseNames` reads all four names off the real `GET /sessions` | Yes |

## The rulings, checked one by one

- **Holder `protocol`, a turn known to be running.** `working` is true only when the latest turn in
  the receipts has no outcome AND the holder is `protocol`, so the fold's "known" is exactly the
  receipts' knowledge. The button is Take the wheel, disabled, with the agreed sentence. Holds.
- **Holder `protocol`, idle, agent disconnected, or nothing held after a restart.** Enabled in all
  three; the tests cover each. The pull request's note is right that `working` cannot be null under
  `protocol` with today's fold, and the code still treats only `true` as a reason. Holds.
- **Holder `user`.** Hand back, enabled, even with a turn open in the terminal (`working` is null
  there, which the fold's "Not known" branch gives, and the button ignores it). Holds.
- **Holder `none`.** No button. Meets the agreed ruling. Finding 1 is about whether the agreed
  ruling is the right one now that the Director has defined `none`.
- **An unknown holder.** No button, and the fold already logs the holder it has no words for.
  Holds.
- **A terminal-mode row.** The fold returns null before the button is reached, and the byte-for-byte
  roster test still stands. Holds.

## The routes against the fold

The brief asked whether a route can refuse a button the fold shows as enabled, for a reason the fold
could have known. I traced each enabled button to the route it names:

- **Take the wheel, enabled** means the holder is `protocol` and no turn is known to be running. The
  take route refuses at the Gateway only when the session cannot be located, the state is unreadable,
  the mode is terminal, or the holder is `user`. None of those can be true when the fold produced this
  button from the same pushed row, so the Gateway forwards the take. Holds.
- **Hand back, enabled** means the holder is `user`. The hand back route refuses at the Gateway only
  while the holder is `protocol`. So the Gateway forwards the hand back. Holds.
- **Both buttons read the same pushed row** (`SessionWheelState.Read` and the fold are both fed
  `SessionDto.WheelHolder`), and the Director's reply-waits-for-the-push contract (wheel verb review,
  finding 3; built in #4094 as `PushDeltaAndWaitAsync`) keeps the row the button was folded from
  from being one push behind the Director at the moment the button is pressed.

Where the two disagree, in the other direction, is recorded in note A and finding 1: the fold is
stricter than the route on a running turn, and the fold shows nothing on `none` where the route
forwards both verbs.

## What else was checked and found correct

- **Rule 7.** The client receives a verb, a label, a boolean and a sentence. There is nothing left
  to decide: not which button, not its words, not why it is greyed out.
- **One place.** The button is ruled in one private method, called once, from the one fold. No
  second ruling exists on the Gateway or in a client.
- **The fold stays pure.** No clock, no input or output; the same receipts and the same holder give
  the same button.
- **The schema.** `wheelAction?: null | components["schemas"]["ApiModeWheelAction"]` matches the
  generator's shape for a nullable object reference (compare `turnVerdict` and `apiMode` in the same
  file); the four fields match the shape openapi-typescript writes for non-nullable strings and
  booleans with defaults (`kind?: string`, `enabled?: boolean`), and `ApiModeWheelAction` sits in the
  alphabetical place the generator would put it, between `ApiModeSessionDisplay` and
  `CompactContextRequest`. A regeneration should produce the same text.
- **The tests, to the test standard.** No port (an in-memory test server), no process, no sleep, an
  injected clock on the pushed store, the harness's SQLite file for the receipts. Each test has one
  arrangement and asserts the ruling for exactly that arrangement.
- **Plain English.** Every sentence a user can see is a plain one.

## Findings

### Finding 1 (medium): holder `none` shows no button, and `none` is the one state in which the user needs a button to get the session going again

**Where:** `ApiModeSessionFold.WheelAction`, the `WheelHolders.None` case, which returns null with
the comment that "what none means after the user held the wheel ... is the Director's to define in
phase 4, and this ruling is revisited then".

**What the Director has already defined.** Pull request #4094, the Director's half of phase 4, is
open on `origin/apimode/p4-wheel` and says in its own words: "When the user's terminal exits on its
own: WheelHolder becomes none. Pi is not restarted by itself. ... The next take opens the terminal
again on the same conversation; the next hand back starts Pi with nothing to stop. Both are tested."
The tests are `TakeWheelAsync_ThenTheTerminalExitsOnItsOwn_LeavesNoHolderAndTheNextHandBackStartsPi`
and `TakeWheelAsync_AfterTheTerminalExitedOnItsOwn_OpensTheTerminalAgainWithoutStartingPi`. In
`ApiModeSessionBackend.cs`, `HandBackWheelAsync` on `none` skips the terminal (there is none) and
starts Pi over the protocol; `TakeWheelAsync` on `none` starts the terminal. The Gateway's own routes
already forward both verbs on `none` (wheel verb review, finding 2). So on every layer below the
button, `none` is a state you act on. On the button, it is a state you cannot.

**The concrete failure.** The user takes the wheel, types Pi's exit command in the terminal (or Pi
crashes there). The Director pushes holder `none`; every prompt sent from now on queues with the
reason "you have the wheel" (#4094: "Prompts sent meanwhile stay queued with reason
UserHasTheWheel"). The row reads kind `stopped`, "No agent process is running", and the Terminal tab
shows **no button**. Rule 7 forbids the client from offering one. From the Cockpit or the phone there
is no way to resume the session: not hand back, which the Director would accept and which would start
Pi and deliver the queue, and not take. Only a raw `POST /sessions/{sid}/wheel/hand-back` from
outside a client resumes it. The user sees a session with prompts waiting and nothing to press.

**Why it is a finding and not a note.** The code meets the ruling it was given, and the brief says
the contract is agreed. But the ruling defers to a phase 4 definition that already exists on the
remote and contradicts it, and the comment promises a revisit that no issue or phase record names.
If #4094 merges first, the button is wrong on main from that moment; if the client builds the
button first, it is built against a verdict that leaves the user stranded. This should be settled
now, by the Tech Lead, as a recorded decision, not rediscovered in the phase 6 proof (whose step 4
is exactly take, type, hand back).

**What a fix looks like (the Tech Lead decides).** The wire shape does not change in any option.
(a) On `none`, offer **Hand back, enabled**: it is the verb that resumes the session over the
protocol, which is what a session started in API mode should return to, and its label already says
what will happen. (b) On `none`, offer Take the wheel, enabled, so the user is sent back to the
terminal they were in. (c) Keep no button and record, in the mission's phase 4 record, that a session
whose terminal exited can be resumed only from the command line. Option (a) is the one I would
choose; the fold's `Agent` label for `none` ("No agent process is running") already tells the user
why the button is there.

### Finding 2 (low): the wire value `handBack` is never asserted as a literal, so a misspelt constant would pass every test

**Where:** `Fold_TheUserHasTheWheel_OffersHandBackEnabled` asserts
`Assert.Equal(ApiModeWheelVerbs.HandBack, wheel.Verb)`, the constant against itself. The roster test
`GetSessions_TheWheelButton_ReachesTheWireByItsCamelCaseNames` asserts the literal `"takeTheWheel"`
and never a hand back. No test on the branch, and no file anywhere under `src/` or `packages/`
outside the constant's own definition, contains the string `"handBack"`.

**The concrete failure.** Change the constant to `"handback"` or `"hand-back"`. All 17 fold tests and
all 6 roster tests still pass. A client built to the promised contract (`verb === "handBack"`) shows
no button, or the wrong one, while the user holds the wheel, and the suite is green.

**The fix.** One literal assertion: either `Assert.Equal("handBack", wheel.Verb)` in the fold test,
or a second roster test with `PushRoster(apiWheel: WheelHolders.User)` reading `verb` off the wire,
which would also prove the hand back button's camel-case names, which the existing roster test
proves only for take. While there, `Fold_TheProtocolHoldsTheWheelAfterAGatewayRestart_OffersTakeTheWheelEnabled`
is the one enabled case that does not assert `DisabledReason` is null; the other three do.

## Notes, not findings

- **A. The fold is stricter than the Director on a running turn, and that is a choice worth
  recording.** The button is disabled while a turn runs, with "Stop it, or wait for it to end". But
  the Gateway's take route does not refuse a take for that reason (it cannot see the receipts), and
  on #4094 the Director does not refuse it either: `TakeWheelAsync` with the protocol holding calls
  `SuspendAsync`, which "stops a running turn, closes Pi's input, and waits for the process to exit",
  then opens the terminal. So a take sent over the route mid-turn succeeds and stops the turn as a
  side effect, while the button says it cannot be done. A disabled button with an explicit Stop is a
  defensible product choice (stopping a turn is not something to do by accident), and the phase 6
  proof's "take the wheel twice: the second is refused" is unaffected. It should be written down,
  because a reader comparing the button to the route will otherwise read it as a disagreement.
- **B. The route reads the holder case-insensitively; the fold reads it ordinally.** A Director
  pushing `"Protocol"` would be ruled by the route as `protocol` and by the fold as a holder it has
  no words for: no button, kind `unknown`. Pre-existing in the fold, not introduced here, and
  #4094's `WheelHolderWord` writes the lower-case constants, so no Director produces it today.
- **C. A disabled button has no escape if the turn's end never reached the Gateway.** `working` is
  rebuilt from the receipts, so if a `TurnEnded` was lost while the holder stayed `protocol`, the
  button stays disabled until the next turn ends, and Stop goes to a Director that may answer that no
  turn is running. This is the display fold review's observation B (re-send until acknowledged is a
  requirement on the Director's push), inherited, not new.
- **D. The branch is one commit behind `origin/main`.** The missing commit is #4132, gate records
  only; the merge is clean.
