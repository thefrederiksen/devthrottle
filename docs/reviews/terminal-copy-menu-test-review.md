# Review of pull request 4691: the copy menu tests size a session terminal by layout

Commit reviewed: 3478e2400, on top of origin/main at 48e9c99c8. Reviewed on 6 November 2026.

## Verdict: APPROVE

## Scope

One file changed: `src/CcDirector.Terminal.Avalonia.Tests/TerminalCopyMenuTests.cs`, eight lines added and two
removed, all inside the `Open` helper that every test in the file shares. No product code changes. I read the
diff, the whole test file, and the product code it exercises in `src/CcDirector.Terminal.Avalonia/TerminalControl.cs`
(`Attach`, `BeginRebuildFromBuffer`, `RunReplay`, `CompleteRebuild`, `EnsureCellsMatchGrid`, `HandleSizeChanged`,
`HarnessSetGrid`), the app's callers of `Attach` in `MainWindow.axaml.cs` and in
`CcDirector.DeliveryQualification/DesktopHost.cs`, and the sibling test files that call `HarnessSetGrid`. I ran the
test project. I did not change anything.

## Is the cause right?

Yes. The chain in the product code is this:

1. `Attach` calls `BeginRebuildFromBuffer`, which clears the grid, sets the replay-in-flight flag, and calls
   `RunReplay`.
2. `RunReplay` is an `async void` method that awaits a `Task.Run`. When the pool thread has already finished by
   the time the `await` is evaluated, the awaiter reports completed and the method continues synchronously on
   the calling thread. In that case `CompleteRebuild` runs inside `Attach`, before `Attach` returns: the replayed
   cells are swapped in, the replay-in-flight flag is cleared, and the poll timer starts. The replayed data in this
   test is one short line, so the pool thread finishing first is realistic under load.
3. `HarnessSetGrid` calls `EnsureCellsMatchGrid`, and `EnsureCellsMatchGrid` calls `InitializeCells` unconditionally,
   even when the size has not changed. Every cell becomes a default cell. With a different size it also allocates a
   new array, so the grid the control renders is no longer the parser's grid.
4. The old `Open` called `HarnessSetGrid(40, 6)` straight after `Attach`. In the inline case that wiped the replayed
   line. The wait loop on `HarnessReplayPending` ended at once because the flag was already false. The selection
   covered empty cells, the menu had no Copy, and the first item was Paste to Terminal, whose click reads the
   clipboard rather than writing it. The clipboard was therefore null, which is exactly the failure reported.

In the common ordering (the await yields, the handover runs on a later `RunJobs`), `HarnessSetGrid` cleared an
empty grid, and `CompleteRebuild` then brought the replayed grid to 40 by 6 through `SegmentedReplay.Continue`,
which preserves content. That is why the test passed almost always and failed only under load. No other cause fits
"once, under load": a replay failure or a deferred attach would fail every run.

## Could the same race clear a real user's terminal in the app?

No. The only callers of `EnsureCellsMatchGrid` are the two harness hooks (`HarnessSetGrid`, `HarnessRebuild`) and the
two places that clear the grid on purpose before a replay starts (the deferred attach and
`BeginRebuildFromBuffer`). Nothing in the app clears cells after `Attach`:

- `MainWindow.axaml.cs` calls `TerminalHost.Attach` and then `UpdateScrollBar`, which does not touch the grid.
- `DesktopHost.cs` in the delivery qualification harness calls `HarnessSetGrid` only before `Attach`, which is safe.
- A layout change after `Attach` goes through `HandleSizeChanged`, which calls `ResizeGridPreservingCells` and
  keeps the overlapping cells.
- In the inline case, `Attach` still calls `ResizeSession` after the handover. `Session.Resize` drops an unchanged
  size, and a changed size writes a resize mark after the poll's start position, so the poll parses it. Either way
  the replayed content stays.

So the race is real but it only harms a caller that forces the grid after `Attach`, which only the harness can do.
This is a test setup defect, as the author says, not a product defect.

## Does the fix depend on time or on the machine?

No. The change removes the forced grid size for a terminal with a session; it adds no wait, no sleep and no clock.
After the change nothing clears the grid after `Attach`, so the test passes whether the handover is inline or
posted. The only thing the test now depends on is the layout grid being at least as wide as the 33-character line,
which the 800-pixel headless window and the headless platform's fixed font metrics give deterministically. The
exact-line clipboard assertion proves it: if the line wrapped, the copied text would differ from the line on every
run, not sometimes.

The pre-existing wait loop (`while (terminal.HarnessReplayPending) { Thread.Yield(); RunJobs(); }`) is not part of
this diff. It reads no clock and sets no timeout; it ends on the handover itself, so a slow machine only makes it
run longer, never changes the outcome. I note it for completeness and raise no finding on it.

## Does the fix weaken what the other tests in the file check?

No. The terminal-without-session path keeps `HarnessSetGrid(Cols, Rows)` and `HarnessRebuild`, so the two tests that
use it (`RightClickMenu_WithSelectionButNoSession_IsJustCopy_WithNoTrailingSeparator` and
`EditMenuCommands_SelectAllThenCopy_CopiesTheScreen`) run exactly as before. The five tests with a session now run
at the layout grid instead of 40 by 6. None of them asserts on the grid size; each selects row 0 or opens the link
menu on row 0, and the line fits on row 0 at either width. The assertions are unchanged. The `Classify` theory does
not use `Open` at all. The comment added at lines 59 to 62 describes the cause accurately.

## Test run

Command, on devlinux with `DOTNET_USE_POLLING_FILE_WATCHER=1`:

```
~/.dotnet/dotnet test src/CcDirector.Terminal.Avalonia.Tests -p:EnableWindowsTargeting=true
```

Result: 111 passed, 0 failed, 0 skipped. The `TerminalCopyMenuTests` class run five more times with `--no-build`:
24 passed each time. A green run cannot by itself prove the absence of a race; the proof above is that after the
change no code path clears the grid after `Attach`.

## Findings

None.
