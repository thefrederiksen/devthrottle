# Issue #494 - Settings > Agents UI polish: code review / proof

Presentation-only polish of the Settings > Agents UI delivered in #489. No change to the
`agent.entries` config model, persistence, migration, or `ToolDetectionService` behavior.

This run was done on a saturated shared machine, so UI/screenshot verification was deferred to a
human. The proof bar here is: clean build, passing unit tests, and a self code-review against the
acceptance criteria. The manual-test checklist (click-paths for AC1-10) is in the PR description.

## What changed

Files (in worktree off origin/main commit `b27125e`):

- `src/CcDirector.Avalonia/SettingsDialog.axaml`
  - List: shared `ColumnDefinitions="56,*,100,64,86,96"` for the header and each row; outer
    `ScrollViewer` keeps `HorizontalScrollBarVisibility="Disabled"`; each row Border has an 8px
    right margin and the Actions cell is a fixed 96px right-aligned column so Edit + trash never clip.
  - Up/Down are compact 26x26 `iconButton`s drawn with vector `Path` arrow glyphs.
  - Remove is a compact 26x26 `iconButton` drawn with a vector trash-can icon (no emoji/Unicode).
  - Status renders as a small pill (`Border CornerRadius=8`) whose colors come from the row
    view-model; hidden when there is no status.
  - Editor reordered to match the action order: (1) Agent type dropdown at the top, (2) Display
    name directly below, (3) `Detect this tool` as a prominent primary button. The manual
    Executable/Browse/Quick-check/Launch-preview area is a collapsible `EditorManualPanel` with an
    `Enter the path manually` reveal link. Preset / Default model / Args override moved into a
    collapsible `EditorAdvancedPanel` behind an `Advanced` `ToggleButton`. The live "This is what
    launches" preview and Save/Cancel remain at the bottom.
  - Added a `Button.iconButton` style.

- `src/CcDirector.Avalonia/SettingsDialog.axaml.cs`
  - `AgentEntryRow` gained `HasStatus`, `StatusPillBackground`, `StatusPillForeground` (pill
    rendering only - no model change).
  - Editor state fields `_editorLoading` and `_displayNameIsCustom`.
  - `OpenEditor`: new entries auto-name from the default type via `AgentEntryNaming`; existing
    entries keep their stored name and are flagged custom so a type change never renames them.
    Manual area starts revealed only when an existing entry already has a path; Advanced always
    starts collapsed.
  - `EditorTypeCombo_Changed`: auto-fills the Display name from the new type, but only while the
    name is not customized.
  - `EditorDisplayName_Changed`: marks the name customized when the user types (programmatic writes
    are guarded by `_editorLoading`).
  - `BtnEditorDetect_Click`: on success collapses the manual area and shows a green
    `Found <type> at <path> (source: ...)` line; on failure reveals the manual area.
  - `BtnEditorShowManual_Click` / `EditorAdvancedToggle_Changed`: reveal handlers.
  - `BtnRemoveAgent_Click`: now async, shows a `ConfirmDialog` before removing.

- `src/CcDirector.Avalonia/AgentEntryNaming.cs` (new) - pure, UI-free naming logic:
  `AutoNameForType` (base label + lowest-free "(N)" disambiguation, case-insensitive) and
  `ShouldAutoFillName` (blank or auto-generated name -> auto-fill; custom name -> leave alone).

- `src/CcDirector.Avalonia/ConfirmDialog.axaml` + `.cs` (new) - minimal yes/no confirm dialog
  returning `bool?`, modeled on the existing `MessageDialog`.

- `src/CcDirector.Avalonia.Tests/AgentEntryNamingTests.cs` (new) - 12 tests for the naming logic.

## Per-AC: how it is satisfied

1. **No clipping at min width.** Shared fixed column template + disabled horizontal scroll + fixed
   96px right-aligned Actions column + 8px row right margin keep Edit/trash inside the panel.
   *Verified by build; needs human screenshot at min width.*
2. **Trash-icon Remove with confirm.** Remove is a vector trash `iconButton`; `BtnRemoveAgent_Click`
   awaits `ConfirmDialog` and only removes on confirm. *Logic verified by code-review; needs human
   screenshot of the confirm.*
3. **Compact arrows + status pill.** Up/Down are vector `Path` arrow `iconButton`s; Status is a
   colored pill bound to `StatusPill*`/`HasStatus`. *Verified by build; needs human screenshot.*
4. **Type first, name below.** Editor XAML order is Agent type combo, then Display name textbox.
   *Verified by code-review; needs human screenshot.*
5. **Auto-name + "(N)".** `AgentEntryNaming.AutoNameForType` returns the base label or the lowest
   free "(N)"; wired into `OpenEditor` (new) and `EditorTypeCombo_Changed` (type change while name
   not customized). The field stays editable. *Verified by unit tests (AutoNameForType_* +
   ShouldAutoFillName_*); needs human screenshot on empty vs Codex-exists forms.*
6. **Prominent Detect.** `Detect this tool` is a `primaryButton` placed right after Type/Name; on
   success fills the path and shows a green `Found ...` line. *Logic verified by code-review;
   machine-dependent, needs human screenshot of a successful detect.*
7. **Manual collapses on detect success, reveals on fail.** `SetManualAreaVisible(!result.Found)`
   in `BtnEditorDetect_Click`; `Enter the path manually` reveal link otherwise. *Logic verified by
   code-review; needs human screenshots (collapsed vs expanded).*
8. **Advanced collapsed.** Preset / Default model / Args override live in `EditorAdvancedPanel`
   behind the `Advanced` toggle, collapsed on open; they still save (unchanged `BtnEditorSave_Click`
   reads `EditorPresetCombo`/`EditorModelBox`/`EditorArgsOverrideBox`). *Save path verified by
   code-review + existing #489 persistence tests; needs human screenshot + config excerpt.*
9. **Edit preserves customized name.** `OpenEditor` sets `_displayNameIsCustom = true` for existing
   entries and copies the stored name verbatim, so opening never renames; `ShouldAutoFillName`
   returns false for custom names. *Verified by unit tests (ShouldAutoFillName_CustomName_*); needs
   human screenshot editing "Codex (my codex)".*
10. **Same `agent.entries` shape.** `BuildEntriesFromRows`, `AgentEntryStore`, and the save path are
    unchanged from #489. *Verified by the full `CcDirector.Core.Tests` suite (incl. #489 agent
    persistence tests) passing; needs human config diff.*

## Verified by build + tests vs needs human UI verification

- Verified now: clean `dotnet build cc-director.sln` (0 warnings, 0 errors); naming logic
  (AC5/AC9) via 12 new unit tests; no model regression via the full Core suite (1971 passed);
  no Unicode/emoji in any edited file.
- Needs human UI verification (deferred - machine saturated): the visual ACs - min-width no-clip
  (AC1), trash+confirm appearance (AC2), arrows/pill appearance (AC3), editor field order (AC4),
  auto-name on screen (AC5), live Detect success/fail and manual collapse (AC6/AC7), Advanced
  collapse + on-disk config excerpt (AC8/AC10), and edit-preserves-name on screen (AC9).

## Unit-test results

- `CcDirector.Avalonia.Tests`: Passed 56, Failed 0, Skipped 0 (12 new in `AgentEntryNamingTests`).
- `CcDirector.Core.Tests`: Passed 1971, Failed 0, Skipped 3 (no regression in #489 agent.entries).
- `dotnet build cc-director.sln`: Build succeeded, 0 Warning(s), 0 Error(s).

I believe this is finished for the parts provable without launching the UI. The visual acceptance
criteria are left for a human to confirm with screenshots.
