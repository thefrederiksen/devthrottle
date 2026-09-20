# Phase 6 - watching it fail

Mission: One repository list, held on the Gateway. Phase 6.

The predictions are in `predicted-symptoms.md`, committed **on its own and before any of this was run**
(`dfacf48cd`, the commit before the code). This document is the observation. Every count and every test
name below was produced by running the attack against the FINAL code, on macOS, Apple silicon,
dotnet at `~/.dotnet`, sequentially, restoring between each.

**Every one of the eight predictions was right, name for name and count for count.** Nothing was
adjusted after the fact; where reality diverged from what I believed, it was about the proof HARNESS and
it is written up in full in section 3 rather than tidied away.

---

## 1. The eight attacks

| # | The attack | Predicted | Observed | Match |
|---|---|---|---|---|
| A | the dialog never asks the Gateway | 7 failed, 23 passed | **7 failed, 23 passed** | yes |
| B | the desktop sorts the served list for itself | 5 failed, 25 passed | **5 failed, 25 passed** | yes |
| C | the plausible half-correction (never removed) | 5 failed, 25 passed - the same five | **5 failed, 25 passed - the same five** | yes |
| D | falling back from a perfectly good empty list | 1 failed, 29 passed | **1 failed, 29 passed** | yes |
| E | the screen stops saying it is on the fallback | 3 failed, 27 passed | **3 failed, 27 passed** | yes |
| F | the folder name read from the host (never removed) | 2 failed, 28 passed | **2 failed, 28 passed** | yes |
| G | the client reads the wrong route | 1 failed, 8 passed | **1 failed, 8 passed** | yes |
| H | a refusal reported as unreachable | 3 failed, 6 passed | **3 failed, 6 passed** | yes |

A to F are `CcDirector.Avalonia.Tests`, filter `FullyQualifiedName~NewSession` (30 tests). G and H are
`CcDirector.Gateway.UnitTests`, filter `FullyQualifiedName~GatewayClientKnownRepositoriesTests` (9).

### A - the dialog never asks

Failing: all seven of `NewSessionDialogGatewayListTests`, none of `NewSessionRepositoryListTests`,
exactly as predicted - the rules are untouched and only the wiring is gone.

```
TheDialogShowsTheGatewaysList_InTheGatewaysOrder
  Assert.Equal() Failure: Values differ
  Expected: 1
  Actual:   0

WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback
  Assert.Equal() Failure: Strings differ
  Expected: "The Gateway could not be reached. This is"...
  Actual:   "Checking the Gateway for the one reposito"...
```

Both predicted symptoms, word for word: nobody asked, and the screen is stuck waiting for an answer it
never asked for.

### B - the desktop sorts for itself

```
Order_OnTheGatewaysList_LastUsedDescending_IsTheServedOrderUntouched
  Assert.Equal() Failure: Collections differ
                    ↓ (pos 1)
  Expected: ["zephyr", "beacon", "atlas", "cinder"]
  Actual:   ["zephyr", "atlas", "beacon", "cinder"]
```

The never-opened repository the Gateway placed SECOND is dragged to the bottom. Predicted exactly.

**`Order_OnTheMachinesOwnList_ComputesTheLocalLastUsedOrder` stayed green, as predicted, and that is
the point of recording it:** the fallback's order genuinely is computed on the desktop, this revert does
not change it, and a suite that only tested the local list would have passed this defect straight
through.

### C - the plausible half-correction, which I never removed

The rule a later developer writes in good faith: used repositories by last-used descending, then the
never-opened ones beneath them by name. It looks exactly like the mission's stated order.

Same five tests, same counts. Ascending:

```
Order_OnTheGatewaysList_LastUsedAscending_IsTheServedOrderReversed
  Expected: ["cinder", "atlas", "beacon", "zephyr"]
  Actual:   ["cinder", "beacon", "atlas", "zephyr"]
```

**This is the attack that says the guard is real.** It was never reverted into existence - it is a rule
the author never wrote - and the only reason it is caught is that the fixtures are an order no client
sort produces, with a never-opened row sitting between two used ones. A fixture that happened to be
sorted would let this through, and the screen would silently disagree with the Cockpit and the phone.

### D - falling back from a perfectly good answer, the other direction

```
WhenTheGatewayServesAnEmptyList_TheScreenShowsIt_AndDoesNotFallBack
  Assert.Empty() Failure: Collection was not empty
  Collection: [RepositoryConfig { ... IsFromTheGatewayList = False ... },
               RepositoryConfig { ... IsFromTheGatewayList = False ... }]
```

Two rows on a machine the Gateway says has none, and `IsFromTheGatewayList = False` in the failure
message names the defect: these are the machine's own rows, shown instead of the Gateway's answer. One
test failed and twenty-nine passed, as predicted - every other test hands the screen a non-empty answer
or no answer at all, so this is the ONLY thing holding that direction down.

### E - the silent fallback

```
WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback
  the screen said nothing about being on its fallback
```

The assertion message is the defect, and the defect is the one the owner named when he chose this
design. Three tests, one per fallback.

### F - the folder name read from the host, which I never removed

```
NameForScannedRepository_WithNoScannedName_ReadsTheFolderOutOfThePath(path: "D:\\ReposFred\\devthrottle\\")
  Expected: "devthrottle"
  Actual:   "D:\\ReposFred\\devthrottle\\"
```

Two of the four cases, both the Windows-shaped ones; the two slash-separated cases stayed green. That
split is the whole reason this defect has now been sighted six times in this product: on a machine that
only ever meets its own separator, it looks fine.

### G - the wrong route

```
ItReadsTheOneRepositoryListRoute_ForThisDirector
  Expected: ..."irector%20under%20test/known-repositories"
  Actual:   ..."8/directors/director%20under%20test/repos"
```

One failed, eight passed - and the eight that passed are the honest limit of a stub, which answers
whatever it is asked. That is exactly why one test asserts the address.

### H - a refusal reported as a failure to reach

```
WhenTheGatewayRefuses_ItCarriesTheGatewaysOwnWords
  Assert.Equal() Failure: Values differ
  Expected: Refused
  Actual:   Unreachable
```

Three failed, six passed. With the two collapsed, the Gateway's own sentence - *The Director has not
reported a machine name.* - never reaches the screen, replaced by a sentence about the network that is
not true.

### Restored

After all eight, the working tree is the committed code (`git status` clean but for the proof folder),
and the suites are green again:

```
CcDirector.Avalonia.Tests    --filter NewSession                             30 passed, 0 failed, 0 skipped
CcDirector.Gateway.UnitTests --filter GatewayClientKnownRepositoriesTests     9 passed, 0 failed, 0 skipped
```

---

## 2. The mission check, section 7

Run by me on this worktree, after the reverts were restored.

| Command | Result |
|---|---|
| `npm run typecheck` | **green**, all four workspaces |
| `npm test --workspaces --if-present` | **2,126 passed, 0 failed** - client-core 1,456, cc-assistant 106, cockpit 457, mobile 107 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **6,600 passed, 0 failed, 8 skipped** |
| `dotnet test src/CcDirector.Core.Tests` | **4,491 passed, 0 failed, 18 skipped** - see section 3b |
| `dotnet test src/CcDirector.Avalonia.Tests` | **677 passed, 0 failed, 0 skipped** |

The Gateway and Avalonia figures are from runs at the branch head with the EXIT CODE captured (both
exit 0, neither log containing the word aborted or crashed), because a summary line alone is not a
pass in this repository - `an-aborted-run-reports-passed.md` is why.

**Zero failures, and the skipped counts are stated rather than buried:** 8 in the Gateway unit tests and
18 in Core, both pre-existing and neither in anything this phase touched. No baseline of known-red tests
is quoted, because there is none to quote - the check is genuinely green.

---

## 3. The proof caught a defect in itself, and it is written up rather than quietly fixed

**The first four pictures this phase committed were pictures of the wrong moment.** The one that matters
most - the Gateway unreachable, which section 7 singles out as the proof that must not be skipped -
showed `Checking the Gateway for the one repository list...` instead of `The Gateway could not be
reached.`, and **the test it came from passed**, because the test read the control and the camera read
the past. A single headless capture after the dispatcher's jobs have run hands back a frame composed
before the Gateway's answer reached the screen.

A picture of the wrong moment is the proof covering the wrong thing, and no assertion about the screen
can see it. It was caught only because the pictures were looked at.

**What I first believed, and what disproved it.** My first explanation was that an earlier test's window
was still open and the camera photographed that one; I closed every window and added an assertion that
exactly one dialog is open. The pictures came out right, so the explanation looked confirmed. **It was
wrong.** Leaving the windows open again - with the composition fix in place - produced correct pictures
anyway, so the windows were never the cause. The fix that works is composing the window repeatedly, and
the check that catches the failure is the liveness one.

**The guard, and it was watched failing.** `Photograph` now takes the picture, makes one known visible
change, takes another, and requires the two to differ - a camera returning a frozen frame gives two
identical pictures. Swapping the capture for one that never re-renders
(`GetLastRenderedFrame` with no composition):

```
WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback  [FAIL]
WhenTheGatewayRefuses_TheScreenShowsTheGatewaysOwnWords                           [FAIL]
Failed: 2, Passed: 5
```

Two of the four photographing tests catch it. **Not four - and that is stated rather than rounded up:**
the other two happened to have a pending composition that made their two pictures differ anyway, so they
would not have caught a frozen camera on their own.

**Said plainly, the one-dialog-open assertion is belt and braces and I could NOT watch it fail.** It is
kept because leaving a window open after a test has finished with it is wrong anyway, but it is not
carrying this defect and the code comment says so.

**These two attacks were added AFTER `predicted-symptoms.md` was committed**, because the defect they
guard was found by the proof itself, halfway through. They are not counted among the eight predictions
and they are declared here rather than back-dated.

---

## 3a. One more guard, also added after the predictions

Reviewing the finished code rather than the tests, one case had no guard: a Gateway that is not
answering takes as long as its timeout, and the user may close the dialog well before that. The answer
then arrives to a window that is gone.

The ask now carries a token cancelled on close, and a late answer is dropped. Watched failing - with the
check lifted, the closed dialog takes the late list:

```
WhenTheDialogIsClosedBeforeTheAnswerArrives_TheAnswerIsDropped  [FAIL]
  Expected: 2
  Actual:   4
```

Two rows were the machine's own, on a window the user had already closed; four is the Gateway's list
applied to it afterwards. Declared here rather than back-dated into the predictions, for the same reason
as section 3.

---

## 3b. Core.Tests failed twice in one run - chased, reproduced and named

**One run of `CcDirector.Core.Tests` reported `Failed: 2, Passed: 4489, Skipped: 18, Total: 4509`.** It
is recorded because the alternative - noticing it, re-running until green and publishing the green one -
is precisely what this mission wrote `an-aborted-run-reports-passed.md` about.

It was a COMPLETE run, not an aborted one: the total is the suite's full count, so the failure mode in
that record does not explain this one. **I lost the two test names**, because the command was piped
through a summary filter. That was my mistake and it is why a hunt was needed rather than an answer.

**It was then reproduced, and it is named.** Core.Tests ran green six more times before a repeat-run
hunt caught it:

| Hunt run | Duration | Result |
|---|---|---|
| 1 | 5 m 11 s | 4,491 passed, **0 failed** |
| 2 | 10 m 2 s | 4,484 passed, **7 failed** |

Same commit, same binary, minutes apart, and **the failing run took twice as long** - other sessions on
this Mac were running their own suites throughout. All seven failures spawn a real child process and
assert against a wall-clock timeout: five in `Setup.FleetToolReachabilityTests`, failing at exactly the
30 seconds the test itself chooses, with the product's own `timed out after 30s` in the message; two in
`Settings.ToolDetectionServiceTests`, failing at exactly the 8 seconds of
`ToolDetectionService.DefaultTimeout`.

**Which it is: the TESTS.** They report how busy the machine is. It is the third sighting of that family
in this mission, after `throughput-test-measures-the-machine.md` and `the-parked-suite-nobody-runs.md`.
The full diagnosis, the seven names, the evidence and what should change are in
`../seven-tool-tests-measure-the-machine.md`.

**It is not phase 6's.** The one `CcDirector.Core` file this phase changes is `RepositoryConfig`, whose
27 tests pass in four seconds in isolation and in every full run including the red ones. No failure is
in a file this mission has touched. Not fixed here: it is two other missions' files, and half of it is a
product change rather than a test change.

**What I cannot prove, said plainly:** that the original two failures were among these seven. Their
names were lost. Same suite, same machine, same conditions, and nothing else in 4,509 tests failed
across eight runs - so reading them as members of this family is the overwhelming reading, but it is a
reading and not a fact.

---

## 4. What this does NOT cover

- **`AskTheDirectorsGatewayAsync`**, the production resolution of the ask from the running Director's
  `ControlApiHost`, has no automated test - it needs the real desktop application object. Everything
  either side of it is covered: the client's route and its four outcomes, and the dialog's behaviour for
  each outcome. The route itself was called by hand against the hosted Gateway with this machine's own
  Director token and answered 200 (see the phase README).
- **The Gateway's ordering.** Phase 3 owns it and proved it. This phase proves only that the desktop
  does not re-derive it.
- **The pictures are of a headless window at a fixed size**, not of the dialog on the owner's screen at
  his resolution. The content, the order and the sentences are what they prove.
