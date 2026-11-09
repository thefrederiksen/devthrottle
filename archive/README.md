# Archive

Code that nothing builds, references or ships any more. It is kept here, not deleted, so it is easy to put
back: move the folder back to where it came from (the same path without `archive/`) and add any project
back to `cc-director.sln`.

**Nothing in this folder is ever built, tested or released.** It is outside `cc-director.sln`, and no
workflow, script, Dockerfile or test points into it. If you find yourself wiring something here into a
build, move it out of the archive first.

## What is here, and why

Archived on 9 November 2026, by the owner's decision, after checking that nothing on main referenced
each item:

| Folder | Was at | What it was |
|---|---|---|
| `src/CcDirector.Terminal` | `src/CcDirector.Terminal` | The old Windows-only (WPF) terminal control. The Director uses `CcDirector.Terminal.Avalonia`. |
| `src/CcDirector.CliExplorer` | `src/CcDirector.CliExplorer` | A Windows-only diagnostic for probing agent command-line flags. Last changed February 2026. |
| `src/CcClick` | `src/CcClick` | An old copy of cc-click. The live one is `tools/cc-click`. |
| `src/CcDirector.Terminal.RenderHarness` | `src/CcDirector.Terminal.RenderHarness` | A one-off harness for rendering the terminal control. |
| `tools/ConsoleDiagnostic` | `tools/ConsoleDiagnostic` | A .NET 8 Windows console diagnostic. Last changed February 2026. |
| `tools/cc-browser-archived` | `tools/cc-browser-archived` | Version 1 of cc-browser, replaced by `tools/cc-browser`. |
