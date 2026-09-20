# Answers - review of phase 2, task 1: the Smart shutdown dialog

These answer `review-phase-2-1.md`, the review of commit `277498f16` on branch
`smart-restart-p2-dialog`. The Developer who built the dialog is gone, so a fresh Developer answers,
opened by the phase 2 Tech Lead (session c6c50eeb). The accept and decline decisions below are the
Tech Lead's, given in the mandate `mandate-phase-2-developer-dialog-findings.md`; this Developer
carried them out and wrote down what was done. The review file itself is landed by the Tech Lead, not
by this branch.

## Finding 1 - the dialog's `SmartShutdownResult` has the same name as the engine's type

**ACCEPTED, by the Tech Lead.** The reason: the harm is proven. The Avalonia project references the
ControlApi project, the wiring task must use `CcDirector.Avalonia.SmartRestart` and
`CcDirector.ControlApi.SmartRestart` in one file, and the unqualified name would then be ambiguous and
the file would not compile. The two types also mean different things, so one name for both breaks the
rule of one name for one thing.

What was done:

- The dialog's record `SmartShutdownResult` is now `SmartShutdownChoice`: it is what the owner chose,
  where the engine's `SmartShutdownResult` is how a run ended. Its file is now
  `src/CcDirector.Avalonia/SmartRestart/SmartShutdownChoice.cs`.
- Every use was renamed, in the window's code-behind, in the view model, and in the tests.
- The engine's type and everything under `src/CcDirector.ControlApi` are untouched.
- Nothing else about the dialog changed. No word the window shows changed.

One thing had to be decided inside the mandate, because nobody can be asked this turn:

- **The name `SmartShutdownChoice` was already taken.** The enumeration beside the record (cancelled,
  smart shutdown, ignore all sessions) was already called `SmartShutdownChoice`, so the record could
  not take that name while the enumeration kept it. The mandate allows renaming the kind enumeration
  beside the record, so the enumeration is now `SmartShutdownChoiceKind`. Checked against
  `origin/main`: neither `SmartShutdownChoice` nor `SmartShutdownChoiceKind` exists anywhere else in
  `src`, and the engine's types are `SmartShutdownAvailability`, `SmartShutdownOutcome`,
  `SmartShutdownPhase`, `SmartShutdownPurpose`, `SmartShutdownRequest`, `SmartShutdownResult`,
  `SmartShutdownSessionProgress`, `SmartShutdownSessionState`, `SmartShutdownSnapshot` and
  `SmartShutdownTimes`, so no new clash is made.
- **The smaller way was taken for everything around it.** The record's property `Choice`, the window's
  `Result` property and `ShowForResultAsync` keep their names: none of them clashes with anything, and
  the mandate says to change nothing else. The one method renamed is the view model's
  `BuildSmartShutdownResult()`, now `BuildSmartShutdownChoice()`, because it carried the old type name
  in its own name and would have read as building the engine's type. The wiring task reads the choice
  as `dialog.Result.Choice`, a `SmartShutdownChoiceKind`, and the time as `dialog.Result.TimeAllowed`.

## The note about the null-forgiving operator in the tests

**DECLINED, by the Tech Lead.** The reason: the reviewer did not raise it as a finding and showed no
harm, and the identical pattern is already on `origin/main` in the same test project
(`SessionRailRowRenderTests.cs`). Nothing was touched.

## The warning for the next mandate - the decision and the count must come from one list

The reviewer raised this as a warning for the wiring task, not as a defect in this change, and says so.
Nothing is wired here, so there is nothing in this change to alter. It is written down here so the next
mandate carries it: the close hook on `origin/main` decides with a narrower rule (working or waiting
for input only) than `SmartShutdownSessionReader` counts with, so the wiring task must take both the
no-dialog decision and the count from the reader's list.

## The check, run after the rename

The branch was rebased onto `origin/main` at `912340ed85d2cc98abd99d1dac6be06e2ef32878`, cleanly. Each
run was in the foreground with a full build, on 20 September 2026:

- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`:
  25 passed, 0 failed, 0 skipped.
- `dotnet test src/CcDirector.Avalonia.Tests`: 624 passed, 0 failed, 0 skipped. The reviewer saw 579 on
  the old base; the rise is tests that arrived on `origin/main`, and the 25 of this change are the same 25.
- `dotnet build src/CcDirector.Avalonia --no-incremental`: 0 warnings, 0 errors.

What this does not cover: the opening test mutation was not repeated after the rename, because the
rename does not touch `InitializeComponent` or any named control; the first Developer and the reviewer
each ran it. The four pictures were not drawn again, because no drawn word or colour changed.
