# Pull request 3863 review: escape only

## Scope

Read `missions/license-switching/MISSION.md` section 5, trap 4, and `missions/license-switching/contract.md` section 6, rule 3. Reviewed the complete `origin/main...HEAD` diff and traced session input writes through `CcDirector.Core`, `CcDirector.ControlApi`, `CcDirector.Terminal`, and `CcDirector.Terminal.Avalonia`, including prompt delivery and direct terminal input.

Ran `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj`. Result: 1,478 passed, 1 failed. The failure is `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne`, which reports that `src/CcDirector.ControlApi/LinkRequestsCache.cs` lacks a row in `docs/BackgroundWork.md`; neither file is touched by this change. The new usage-limit tests passed.

## Findings

### High: Remote raw prompts bypass the "person at the terminal" exception

`SessionCommandExecutor.SendPromptCoreAsync` assigns `InputModality.Typed` to human requests carrying a remote phone or browser surface (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:437`). When `AppendEnter` is false, it sends the request text through `session.SendInput(..., origin, ...)` (`:536`). `Session.WriteRawInput` treats every typed origin as a person physically at the terminal and opens `PersonAtTheTerminal` (`src/CcDirector.Core/Sessions/Session.cs:3027-3035`); the guard then passes the write without checking the dialog (`src/CcDirector.Core/Sessions/UsageLimitInputGuard.cs:94-97`). Remote phone/browser input therefore bypasses the guard. A raw request containing a key sequence such as Down plus Enter can reach the open menu and confirm a paid option.

The exception needs to identify local physical terminal keystrokes, rather than trusting `InputModality.Typed`, which also describes remotely delivered operator prompts.

### High: A wrapped or changed option label makes recognition fail open

`UsageLimitDialog.IsShowing` squashes each screen row independently and requires the exact string `1. Stop and wait for limit to reset` to occur in one row (`src/CcDirector.Core/Sessions/UsageLimitDialog.cs:34-45`). If a narrow or resized terminal wraps that option over two rows, neither row matches. A wording or order change has the same result. `UsageLimitInputGuard.MayPass` permits input whenever recognition returns false (`src/CcDirector.Core/Sessions/UsageLimitInputGuard.cs:94-97`), so an automatic key can reach a visible dialog after this miss.

The six fixtures in `UsageLimitDialogTests.RealDialogs` are labelled as captured screens and cover intact rows, joined rows, dropped spaces, an inline hint, monthly spend, and a wide terminal. They do not cover a split option label, changed label, changed option order, or partial drawing of the option. The direct session test does show the current guard rejecting Enter and `2`; it would fail if that guard were removed. It does not establish protection for the untested screen shapes above.

## Other checks

The normal session send paths converge on the wrapped backend for `ConPty` sessions, including driver writes, `SendInput`, and `SendTextAsync`; the remote-origin exemption above is the bypass found. The automatic Escape path keeps its latch and backend on the `Session` instance, sets the latch before writing, and resets it after a recognized non-dialog screen. I found no cross-session target or repeat loop in that path.
