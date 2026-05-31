# Tools Catalog Page

## Goal

Turn the ~45 `cc-*` tools that ship with CC Director from an invisible, unverified
pile of binaries into a browsable, self-testing catalog with a first-class **Tools**
page in the Avalonia app.

Today the toolset is dark:

- The tools build to `%LOCALAPPDATA%\cc-director\bin` (34 `.exe` present right now)
  but nothing *runs* them to prove they still work after a refactor.
- `docs/cli-reference.md` is auto-generated from `--help`, so we know they parse,
  but parsing is not functioning.
- Skills (`/write`, `/vault`, `/commit`, ...) call these tools, but the wiring is
  tribal knowledge - there is no place that says "skill X drives tool Y".
- `cc-launcher` is talked about but **does not exist yet**.

The catalog fixes all four: it lists every tool, tests it (green/red), shows what
commands it exposes, and links it to the Claude Code skills that drive it. It is
built **Claude Code first**; Gemini / Pi / other agents are a later column in the
same model, not part of this work.

## Concept

A top-level **Tools** destination (its own page, sibling to Sessions / Settings /
Voice), structured as a master-detail view:

- **Left:** searchable list of tools, each with a rolled-up status chip
  (`PASS` / `FAIL` / `NOT BUILT`).
- **Right:** detail pane for the selected tool, with tabs:
  - **Overview** - description, source dir, resolved binary path, version.
  - **Commands** - the subcommands/flags (seeded from `cli-reference.md`).
  - **Tests** - the layered health checks, each runnable with a green/red result.
  - **Skills** - the Claude Code skills that drive this tool (auto-discovered).
  - **Logs** - raw stdout/stderr of the last test run (so a FAIL is debuggable).
- **Top:** an agent selector (`Claude Code` now; Gemini / Pi greyed-out "later"),
  a `Run All Tests` button, and a count summary (`44 PASS  2 FAIL  1 NOT BUILT`).

ASCII sketch of the layout:

```
+=====================================================================================+
| CC Director            [ Sessions ] [ Voice ] [ TOOLS ] [ Settings ]                |
+=====================================================================================+
|  AGENT: [ Claude Code v ]  (Gemini, Pi ... later)        [ Run All Tests ]  47 tools|
+------------------+------------------------------------------------------------------+
|  search [______] |  cc-vault                              status: [ PASS ]  v0.9.2  |
|                  |  ----------------------------------------------------------------|
|  > cc-vault   OK |  [ Overview ] [ Commands ] [ Tests ] [ Skills ] [ Logs ]         |
|    cc-launcher OK|  ----------------------------------------------------------------|
|    cc-html    OK |  OVERVIEW                                                         |
|    cc-word  !FAIL|    The contacts/secrets vault CLI. Source: tools/cc-vault        |
|    cc-outlook OK |    Binary:  %LOCALAPPDATA%\cc-director\bin\cc-vault.exe           |
|    ...           |  TESTS                          [ Run ]                          |
|                  |    [PASS] binary on PATH          12ms                           |
|  -------------   |    [PASS] cc-vault --version      88ms  -> 0.9.2                  |
|  44 PASS  2 FAIL |    [PASS] cc-vault contacts list -n 1   140ms                    |
|  1 NOT BUILT     |  SKILLS  (Claude Code)                                            |
|                  |    /vault -> drives  /contact-merge -> drives  /campaign -> uses |
+------------------+------------------------------------------------------------------+
```

## Decisions (locked)

1. **Placement:** own top-level page, not a Settings section.
2. **Test depth:** layered - every tool declares three checks: (a) binary on PATH,
   (b) `--version` responds, (c) one safe **read-only** smoke command. No side
   effects (no emails, no posts, no writes).
3. **Skill mapping:** auto-discover from skill files, with a hand-curated override
   file for what discovery cannot see. The UI shows discovered vs. declared
   honestly (no faking a link that was not found).

## What maps onto what

| Catalog concept        | Reality                                                       |
|------------------------|---------------------------------------------------------------|
| Tool                   | A `tools/<name>/` project built to `bin\<name>.exe`           |
| Tool description       | First line / summary from `tools/<name>/README.md` + manifest |
| Tool commands          | The block in `docs/cli-reference.md` for that tool            |
| Tool version           | stdout of `<tool> --version`                                  |
| Tool tests             | The 3 layered checks declared in the manifest                 |
| `PASS`/`FAIL`/`NOT BUILT` | All checks pass / any check fails / binary not in `bin`    |
| Skill -> tool link     | `cc-*` token found in a `skill.md` / `SKILL.md`, or override  |
| Agent                  | Claude Code (now); Gemini/Pi are future columns               |

## New code

### Core: tool catalog model + manifest

- `src/CcDirector.Core/Tools/ToolDescriptor.cs` - one tool: `Name`, `Description`,
  `SourceDir`, `BinaryPath`, `Commands` (from cli-reference), `Tests` (3 checks).
- `src/CcDirector.Core/Tools/ToolTest.cs` - one check: `Kind`
  (`OnPath` / `Version` / `Smoke`), `Command`, `ExpectContains?`, plus a result
  shape (`Passed`, `DurationMs`, `Stdout`, `Stderr`, `ExitCode`).
- `src/CcDirector.Core/Tools/ToolManifest.cs` + `tools-manifest.json` - the
  authoritative declaration of each tool's description and its smoke command.
  Seeded once from `cli-reference.md` (presence + version are universal and need
  no per-tool data; only the **smoke** command is hand-declared per tool).
  Lives at `docs/tools-manifest.json` (checked in, reviewable).
- `src/CcDirector.Core/Tools/ToolCatalogService.cs` - loads the manifest, resolves
  binary paths against `%LOCALAPPDATA%\cc-director\bin`, marks `NOT BUILT` when a
  binary is absent. Logs entry/exit/errors per CodingStyle.

### Core: test runner

- `src/CcDirector.Core/Tools/ToolTestRunner.cs` - runs a `ToolTest` by shelling the
  declared command with a short timeout, captures stdout/stderr/exit code, returns
  a result. **Read-only only** - the runner refuses to execute a smoke command not
  whitelisted in the manifest (guard against side effects).
  - `RunAllAsync` fans out across tools with bounded concurrency; reports progress.
  - No retries, no fallback - a failing tool reports FAIL with its real output.

### Core: skill discovery

- `src/CcDirector.Core/Tools/SkillToolLinker.cs` - scans skill files for `cc-*`
  mentions and builds the skill->tool map. Sources, in order:
  - Global: `%USERPROFILE%\.claude\skills\<name>\skill.md`
  - Repo: `.claude/skills/<name>/SKILL.md` (or `skill.md`)
  - Override: `docs/skill-tool-overrides.json` - hand-curated links discovery
    misses (e.g. a skill that calls a tool indirectly).
- Output per tool: list of `{ skill, relation }` where relation is `drives` (skill
  is primarily about this tool) or `uses` (mentions it among others). The UI
  flags whether each link was `discovered` or `declared`.

### Control API

- `src/CcDirector.ControlApi/ToolsEndpoint.cs`:
  - `GET /tools` - the catalog: descriptors + last-known status (no run).
  - `GET /tools/{name}` - one tool's full detail incl. skill links.
  - `POST /tools/{name}/test` - run that tool's checks, return results + logs.
  - `POST /tools/test` - run all (bounded concurrency), stream/return summary.
  Mirrors the existing `SettingsEndpoint` registration style.

### Avalonia UI

- `src/CcDirector.Avalonia/Controls/ToolsView.axaml(.cs)` - the master-detail page,
  same UserControl pattern as `VoiceView` / `ConnectionsView`. Loads the catalog
  async on `Loaded` (immediate "Loading..." per the responsive-UI rule), populates
  the left list, binds the detail tabs. `Run` / `Run All` call the Control API and
  update status chips on the UI thread via the dispatcher.
- `MainWindow.axaml(.cs)` - add the top-level **Tools** nav entry next to Settings;
  show `ToolsView` in its panel.
- All styling per `docs/VisualStyle.md` (dark `#252526` / `#1E1E1E`, `#CCCCCC`
  text, existing button styles).

### cc-launcher (net-new tool)

`cc-launcher` is both a catalog entry and the fix for a real current pain: a
Director cannot spawn a sibling Director from inside its own process tree without
the child claude.exe inheriting a nested ConPTY and dying (the problem the
`cc-director-launch` Task Scheduler workaround in `CLAUDE.md` exists to dodge).

- New `tools/cc-launcher/` project (same shape as other tools: `build.ps1`,
  `README.md`, `--version`, `--help`).
- Job: an always-on, out-of-band launch endpoint/agent that starts a
  `cc-director-avalonia*.exe` with a clean parent (outside any Director ConPTY),
  so a running Director can ask it to launch a sibling slot for development.
- Replaces the Task Scheduler instructions in `CLAUDE.md` once proven.
- Its catalog test is special: presence + version like any tool, but its smoke
  check is "launcher reachable / service responding", not a destructive launch.

This plan **scopes the catalog entry + manifest slot + health check** for
cc-launcher. The launcher's own internal design (transport, how a Director calls
it) is tracked as its own follow-up so the catalog work is not blocked on it - the
catalog renders cc-launcher as `NOT BUILT` until the launcher lands.

## Phasing

1. **Catalog read-only.** ToolDescriptor + manifest + ToolCatalogService +
   `GET /tools`. Tools page lists everything with `NOT BUILT` vs built, Overview +
   Commands tabs. No tests yet. (Proves the page and discovery of binaries.)
2. **Test runner.** ToolTest + ToolTestRunner + `POST /tools/{name}/test` +
   `POST /tools/test`. Tests tab live, status chips, Run / Run All, Logs tab.
3. **Skill linkage.** SkillToolLinker + override file + Skills tab.
4. **cc-launcher.** Build the launcher tool; wire it into the catalog; migrate the
   `CLAUDE.md` Task Scheduler section to point at it.
5. **Agent abstraction (later).** Generalize "agent has skills + tools" so Gemini /
   Pi become additional columns. Out of scope for the first build.

## Testing

- `src/CcDirector.Core.Tests/ToolCatalogServiceTests.cs` - manifest load, binary
  resolution, `NOT BUILT` detection (Arrange-Act-Assert, named
  `Method_Scenario_Result`).
- `ToolTestRunnerTests.cs` - a known-good stub binary passes; a missing binary
  fails honestly; a non-whitelisted smoke command is refused.
- `SkillToolLinkerTests.cs` - a skill file mentioning `cc-vault` links to it;
  override file adds a link discovery missed; discovered-vs-declared flag correct.
- Manifest integrity test: every built binary in `bin` either has a manifest entry
  or is reported as unmanaged (no silent gaps).

## Open questions

- **Manifest authoring:** seed `tools-manifest.json` by parsing `cli-reference.md`
  once, then hand-correct the smoke commands? (Recommended.) Or write all ~45 by
  hand? Parsing-then-correcting is less work and stays honest about coverage.
- **Smoke-command safety:** a few tools (cc-outlook, cc-gmail, cc-reddit, linkedin)
  have *no* safe read-only command that needs zero auth. For those, the smoke check
  is "auth status / whoami" (read-only) or omitted with the tool marked
  "presence + version only" in the manifest - shown truthfully, not faked green.
- **cc-launcher transport:** local HTTP endpoint vs. named pipe vs. keeping a thin
  Task Scheduler shim behind a `cc-launcher` CLI face. Decide in the launcher's own
  design pass (phase 4).
