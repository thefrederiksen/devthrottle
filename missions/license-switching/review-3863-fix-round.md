# Pull request 3863 fix-round review

## Scope

Read `missions/license-switching/review-3863-escape-only.md` and `missions/license-switching/review-3863-answers.md`. Reviewed the fix diff `cd90d5fa0..HEAD` and the complete `origin/main...HEAD` change. Traced session backend wrapping and writes, remote prompt surface stamping and browser terminal input, the Avalonia desktop key path, the terminal state detector, and the per-session Escape latch.

Ran only:

```
dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --filter UsageLimit
```

Result: Passed; Failed 0, Passed 45, Skipped 0, Total 45. I did not run Gateway integration tests or exercise a live phone, Cockpit, browser terminal, or desktop UI.

## Finding

### Medium: unrelated usage-limit text makes an ordinary menu drop automatic input

`UsageLimitDialog.MayBeShowing` concatenates every row from roughly the lower half of the screen plus six rows above it, then independently looks for a menu hint and any one of the limit words (`src/CcDirector.Core/Sessions/UsageLimitDialog.cs:79-81`). It does not require the hint and limit text to belong to the same menu.

A normal permission prompt like the one in `IsShowing_PermissionPromptWithTheSameCancelHint_IsFalse` at `src/CcDirector.Core.UnitTests/Sessions/UsageLimitDialogTests.cs:351-363` contains “Esc to cancel.” Add an ordinary output line in the visible region such as “The usage limit is described in the README”: squashing makes that `usagelimit`, so `MayBeShowing` returns true even though `IsShowing` is false and no usage-limit menu is open. For this eight-row screen, the selected region includes all rows.

`Session.ReadUsageLimitDialog` stores that result as `MayShow` (`src/CcDirector.Core/Sessions/Session.cs:3060-3061`), and the backend guard uses it to reject all non-Escape automatic writes (`src/CcDirector.Core/Sessions/UsageLimitInputGuard.cs:76-98`). A phone or Cockpit response to the ordinary permission prompt is therefore dropped; text and Enter calls complete without reaching the terminal, leaving that prompt unanswered. The current negative test only covers a permission prompt with no unrelated limit wording.

## Other reviewed questions

- **Remote input bypass:** I found no remaining bypass in the traced paths. The Gateway overwrites prompt surface from the verified device credential; remote device types map to Phone, Cockpit, or Unknown, never Desktop. Raw browser-terminal frames carry no desktop typed origin. Only typed desktop input receives the local-person exception in `Session.WriteRawInput`.
- **Wrapped, reordered, reworded, and partial dialog shapes:** The certain detector joins adjacent rows; the wider detector blocks the reviewed reordered, changed-label, and partial-dialog fixtures without sending Escape. The wrapped-row tests also pass.
- **Local typing and Escape:** The Avalonia terminal sends local key events with `DesktopTyped`, which takes the local-person exception. Escape state is held per Session and set before the write; the production detector calls the method on its own session. The focused tests cover repeat suppression and one Escape across later quiet periods. I found no cross-session target in the call path.

