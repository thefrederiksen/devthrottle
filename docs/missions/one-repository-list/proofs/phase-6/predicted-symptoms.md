# Phase 6 - what I predict will break, written before I broke anything

Mission: One repository list, held on the Gateway. Phase 6 of section 6 - *the Director's dialog reads
the Gateway list, falling back to the local scan with a line on screen when the Gateway cannot be
reached.*

This document is committed **on its own, before** the reverts are run and before `watched-it-fail.md`
exists. It is the prediction; that one is the observation. A proof nobody has watched fail is decoration,
and a symptom written down after the fact is not a prediction.

---

## The guards, and what each is for

| Suite | Class | Tests |
|---|---|---|
| `CcDirector.Avalonia.Tests` | `NewSessionRepositoryListTests` | 23 - the rules, as pure functions |
| `CcDirector.Avalonia.Tests` | `NewSessionDialogGatewayListTests` | 7 - the real window, headless, with pictures |
| `CcDirector.Gateway.UnitTests` | `GatewayClientKnownRepositoriesTests` | 9 - the route and the four outcomes |

Filters used below:

```
dotnet test src/CcDirector.Avalonia.Tests    --filter "FullyQualifiedName~NewSession"                        # 30
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~GatewayClientKnownRepositoriesTests"  # 9
```

**Three of the seven attacks below are rules I never removed.** The lesson this mission already paid for
is that reverting the exact line you deleted proves the guard catches THAT line, not the class its name
claims - three seats validated one guard that way and all three missed the same defect in the other
direction. So C is the plausible half-correction a later developer would write in good faith, D is the
failure in the opposite direction (falling back from a perfectly good answer), and F is the recurring
path defect put back in a shape I did not write.

---

## A. The dialog never asks the Gateway

**The change:** delete `await LoadGatewayRepositoriesAsync();` from the dialog's `Loaded` handler. The
screen keeps working - it shows the machine's own list, exactly as it did before this phase.

**Predicted:** `CcDirector.Avalonia.Tests`, filter `NewSession` - **7 failed, 23 passed**. Every test in
`NewSessionDialogGatewayListTests` and none in `NewSessionRepositoryListTests`, because the rules are
untouched and only the wiring is gone.

**Predicted first symptom:** `TheDialogShowsTheGatewaysList_InTheGatewaysOrder` fails on
`Assert.Equal(1, asked)` with **actual 0** - nobody asked for the list.

**Predicted second symptom:** `WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback`
fails on the notice text, which is still `Checking the Gateway for the one repository list...` - the
screen is stuck waiting for an answer it never asked for.

`CcDirector.Gateway.UnitTests` stays **9 passed**: the client is fine; nothing calls it.

---

## B. The desktop sorts the Gateway's list for itself

**The change:** in `NewSessionRepositoryList.Order`, delete the `if (sourceIsTheGatewaysOrder)` branch,
so the last-used comparison runs over the served list too. This is the mission's own defect wearing a
feature's clothes.

**Predicted:** filter `NewSession` - **5 failed, 25 passed**:
`Order_OnTheGatewaysList_LastUsedDescending_IsTheServedOrderUntouched`,
`Order_OnTheGatewaysList_LastUsedAscending_IsTheServedOrderReversed`,
`Order_ThroughTheNameHeadingAndBack_ReturnsTheGatewaysOrderIntact`,
`TheDialogShowsTheGatewaysList_InTheGatewaysOrder`, `SortingByNameAndBack_ReturnsTheGatewaysOrder`.

**Predicted symptom:** the served order `zephyr-tools, beacon, atlas-reporting, cinder` becomes
`zephyr-tools, atlas-reporting, beacon, cinder` - the never-opened repository that the Gateway placed
SECOND is dragged to the bottom, because a client sort has its own idea of where a row with no date
belongs.

**Predicted to stay green, and this is the point:**
`Order_OnTheMachinesOwnList_ComputesTheLocalLastUsedOrder`. The fallback's order is genuinely computed
here and this revert does not change it, so a suite that only tested the local list would pass this
defect straight through.

---

## C. The plausible half-correction - a rule I never removed

**The change:** in `Order`, for the Gateway's list, apply the fix a later developer would write in good
faith: used repositories by last-used descending, then the never-opened ones beneath them by name,
ascending being that order reversed. It looks exactly like the mission's stated order. It is still the
desktop ruling.

**Predicted:** filter `NewSession` - **5 failed, 25 passed**, the SAME five as B.

**Predicted symptom:** identical to B - `zephyr-tools, atlas-reporting, beacon, cinder`. The fixtures are
deliberately an order no client sort produces (a never-opened row between two used ones), which is the
only reason a rule this plausible is caught at all. A fixture that happened to be sorted would let it
through.

---

## D. Falling back because the answer looked wrong - the other direction

**The change:** in `LoadGatewayRepositoriesAsync`, take the fallback when the Gateway serves an EMPTY
list, on the reasoning that an empty list must be a mistake. This is the failure the mission warns about
in terms: falling back for anything other than a Gateway that could not answer hides a real defect.

**Predicted:** filter `NewSession` - **1 failed, 29 passed**. Exactly
`WhenTheGatewayServesAnEmptyList_TheScreenShowsIt_AndDoesNotFallBack`, and nothing else - every other
test hands the screen a non-empty answer or no answer at all.

**Predicted symptom:** `Assert.Empty(RowsOnScreen(dialog))` fails with **2 rows on screen**, the
machine's own `mindzie-studio` and `devthrottle`, on a machine the Gateway says has no repositories.

---

## E. The screen stops saying it is on the fallback

**The change:** in `LoadGatewayRepositoriesAsync`, hide the notice on the fallback branches instead of
showing it. The list is still right; the screen simply does not say where it came from. This is the
failure the owner named by name when he chose this design.

**Predicted:** filter `NewSession` - **3 failed, 27 passed**:
`WhenTheGatewayCannotBeReached_ItStillListsRepositories_AndSaysItIsOnTheFallback`,
`WithNoGatewayConnected_ItListsTheMachinesOwnRepositories_AndSaysSo`,
`WhenTheGatewayRefuses_TheScreenShowsTheGatewaysOwnWords`.

**Predicted symptom:** each fails on `Assert.True(notice.Visible, "the screen said nothing about being on
its fallback")` - the assertion message IS the defect.

---

## F. The folder name read from the host instead of from the path - a rule I never removed

**The change:** in `NewSessionRepositoryList.NameForScannedRepository`, put back
`Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))`, which is what the dialog had before this
phase and is the sixth sighting of this mission's recurring defect.

**Predicted:** filter `NewSession` - **2 failed, 28 passed**. Two of the four cases of
`NameForScannedRepository_WithNoScannedName_ReadsTheFolderOutOfThePath`.

**Predicted symptom, on this macOS machine:** `D:\ReposFred\devthrottle` answers
`D:\ReposFred\devthrottle` instead of `devthrottle` - the whole path in the Name column. The two
slash-separated cases stay green, which is exactly why this defect survives on a machine that only ever
sees its own separator.

---

## G. The client reads the wrong route

**The change:** in `GatewayClient.GetKnownRepositoriesAsync`, read `directors/{id}/repos` - the
registry-only route the Cockpit used to read, and half a list.

**Predicted:** `CcDirector.Gateway.UnitTests`, filter `GatewayClientKnownRepositoriesTests` - **1 failed,
8 passed**: `ItReadsTheOneRepositoryListRoute_ForThisDirector`, on the request address. The other eight
pass, because the stub answers whatever it is asked - which is the honest limit of a stub and the reason
this one test asserts the address at all.

---

## H. A Gateway that refused, reported as a Gateway that could not be reached

**The change:** in `GatewayClient.GetKnownRepositoriesAsync`, return `Unreachable` for an error answer
instead of `Refused`. The two collapse into one, which is how "the Gateway is broken" comes to read as
"the network is down" on screen.

**Predicted:** filter `GatewayClientKnownRepositoriesTests` - **3 failed, 6 passed**:
`WhenTheGatewayRefuses_ItCarriesTheGatewaysOwnWords`,
`WhenTheGatewayRefusesWithNoBody_ItReportsTheStatusLine`,
`WhenTheAnswerIsNotAList_ItIsARefusal_NotUnreachable`.

**Predicted symptom:** the outcome is `Unreachable` where `Refused` was expected, and the Gateway's own
sentence - *The Director has not reported a machine name.* - never reaches the screen, replaced by a
sentence about the network that is not true.

---

## What the predictions do NOT cover, said here rather than discovered later

- **`AskTheDirectorsGatewayAsync`**, the production resolution of the ask from the running Director's
  `ControlApiHost`, has no test: it needs the real desktop application object. Everything below it is
  covered - the client's route and outcomes, and the dialog's behaviour for each of those outcomes - and
  the seam between them is three lines with no branch but a null check.
- **The Gateway's own ordering.** Phase 3 owns and proved it. Phase 6 proves only that the desktop does
  not re-derive it.
- **A real Director against a real Gateway.** The route was called by hand against the hosted Gateway
  with this machine's own Director token during the ground check, and answered; that is recorded in the
  phase README, not automated here.
