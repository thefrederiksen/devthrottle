# Factory Inbox - the small defects from the end-to-end proof

Fixes for defects 4, 5, 6 and 8 of the end-to-end proof (`docs/qa/factory-inbox/phase-6-end-to-end/README.md` on the
branch `factory-inbox/end-to-end-proof`), and the thread screen's re-read from defect 1. Mission
fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32. Defects 2, 3 and 7 are out of scope and are filed separately.

## What changed

| Defect | Where | The fix |
| --- | --- | --- |
| 4 - a delegated seat's name repeats its prefix | `Factory/Definition/FactorySeatDelegation.cs`, `SessionName` | The seat start adds "factory title - seat name - " only when the name the Boss passed does not already start with it (any letter case, and a prefix written twice is taken off too). |
| 5 - a long subject pushes the bell off the phone's app bar | `apps/mobile/src/pages/Inbox.tsx` (the phone's inbox frame), `apps/mobile/src/styles.css` | The frame's bar is named `fi-phone-bar`; under `.screen` its title starts from no width and takes only the room left between Back and the bell, wrapping there, so nothing else is made to shrink. The bell's own rule is untouched (only `alerts.css` may write it). Layout only. |
| 6 - the list row says only "1 waiting for a decision" and "Sent" | `Factory/Inbox/FactoryInboxFold.cs` (`Row`), `FactoryInboxWords.cs`, `Api/FactoryInboxEndpoints.cs`; client-core `FactoryInboxListView.tsx` | The Gateway decides the row: when the newest message waits for a seat that cannot be started, the row carries `reasonLine`, the same sentence the thread shows, and the badge says "Waiting". The screen prints `reasonLine` as it comes, in the row's tone. |
| 8 - the archived-factory refusal names the id | `Api/FactoryInboxEndpoints.cs`, `Api/YourInboxEndpoints.cs` | The refusal names the factory's title: "Old shop is archived, so it takes no messages. ..." |
| From 1 - the thread aborts its read every 15 seconds | client-core `FactoryInboxThreadView.tsx` | One read at a time. The next read starts 15 seconds after the last one ENDS; a read is never aborted to make room for another. A read asked for while one runs (the composer left a message) starts as soon as that one ends, so the screen shows the latest answer. The read in flight is aborted only when the screen leaves the factory. |

## Tests that fail without the fixes

Each was run against the code before the fix and failed, then passed with it:

- `FactorySeatStartByTheBossTests.Start_ANameThatAlreadyCarriesTheFactoryAndSeat_IsNotGivenThePrefixAgain` (three names).
- `FactoryInboxEndpointTests.TheList_RowSaysWhyTheNewestMessageWaits_InTheThreadsWords`, and the fold tests
  `FactoryInboxScreensFoldTests.Row_NewestSentAndTheSecretaryCannotBeStarted_SaysWhyAndBadgesItWaiting`,
  `Row_NewestWithTheBossAndOnlyTheSecretaryWaits_HasNoReason`, `Row_NothingWaits_HasNoReason`.
- `FactoryInboxVoiceEndpointTests.Register_ArchivedFactory_IsRefusedInTheTypedSendsWords` and
  `YourInboxEndpointTests.Reply_ToAFactorysMessage_WhenTheFactoryIsArchived_IsRefused_AndNothingIsLeft`.
- client-core: `FactoryInboxListView_RowWithAReason_PrintsTheGatewaysReasonVerbatim_AndARowWithoutOneNone`,
  `FactoryInboxThreadView_SlowRead_IsNeverAbortedOrJoinedByAnother_AndItsAnswerIsShown`,
  `FactoryInboxThreadView_MessageLeftWhileAReadRuns_ReadsAgainWhenThatReadEnds_NotBefore` (with
  `..._AfterARead_ReadsAgain15SecondsLater_AndShowsTheLatestAnswer` and `..._Closed_AbortsTheReadInFlight_AndReadsNoMore`
  holding the rest of the behaviour in place).
- mobile: `Inbox_AppBar_GivesALongTitleOnlyTheRoomBesideTheBell`. A test page has no layout, so this only holds the rule
  in place; the layout itself is proved in a real browser below.

## The pictures

`capture.py` beside this file, run in the foreground: a throwaway local Gateway built from this branch on port 7994 with
its own data folder and `CC_GATEWAY_NO_TAILSCALE=1` (its log says "CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve
auto-provisioning disabled", and the rig fails if it does not), and its own headless Chrome at phone size (390 by 844,
scale 2). It registers "Factory Inbox test" on the computer `fi-inbox-test`, where no Director runs, leaves a typed
message through the real route, and waits for the Gateway's own wake to find no Director there. The Your inbox message
with the step 4 subject is written into the scratch database, as in the step C rig, because only a Director mints the
session key that sends one. `answers.json` holds the Gateway answers quoted here, exactly as sent.

| Picture | What it shows |
| --- | --- |
| `phone-01-factory-inbox-row-says-why-it-waits.png` | Defect 6. The row: "1 waiting for a decision", then the Gateway's sentence "Waiting - no Director is running on the Boss's computer, fi-inbox-test; it goes on when one connects there" in amber, and the badge "Waiting". |
| `phone-02-long-subject-bell-in-place.png` | Defect 5. The proof's step 4 subject in the app bar; it wraps between Back and the bell, and the bell stays at the right edge. Measured in the browser: the bell's right edge 374, the bar's right edge 374, the screen 390 wide. The rig fails if the bell is past the bar. Before the fix: `04f-phone-your-inbox-message.png` in the end-to-end proof. |

Defect 8 in the Gateway's words (`answers.json`, `archived-factory-send-refused`): HTTP 409, "Old shop is archived, so it
takes no messages. Restore it from the Factories screen first."

## What this does NOT prove

- **The thread on the live service.** The re-read fix stops new reads piling up behind a slow one; it does not make a
  slow read fast (pull request 4690 did that). It was tested with a stand-in Gateway client and fake timers, not on the
  hosted Gateway.
- **The Cockpit's row.** The reason line is in the shared list view both shells mount, but only the phone was
  photographed.
- **A long title's height.** A very long subject still makes a tall app bar; it is no longer cut off at the right, but
  it is not shortened.
- **Defect 4 from a real Boss.** The test drives the seat start route with the name a Boss passed; no real Boss was run.
