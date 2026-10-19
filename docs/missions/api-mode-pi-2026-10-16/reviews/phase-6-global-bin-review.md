# Phase 6 review: a Director on a non-default root keeps its tool links inside its own root

Mission: API mode for agent wrappers, proven on Pi (issue #4070). The change under review is pull request #4211,
branch `fix/non-default-root-keeps-global-bin`, read at its one commit `f98471512` ("A Director on a non-default root
never writes the user's global bin"), which sits directly on `dac07950f`, origin/main at the time of reading.
Reviewer seat, opened by the Delivery Lead, running a different agent family from the Developer. Written 18 October
2026 on devlinux, in a worktree cut from the pull request branch (`../devthrottle-review-4211`), never the shared
checkout. No real Director was run, no file under the real home folder was written, and nothing was built or fixed
beyond the test projects I compiled to run the pull request's own tests. The only file I wrote is this one.

## Verdict

**The change does what the brief says, it is safe for the machine's installed Director on Linux and macOS, and Windows
is unchanged. Two low findings, both for readers the pull request left on the old rule, and both resting on a premise
in the pull request's own description that is wrong.** The one rule, `ToolShimPath.ShimDirFor`, answers the user's
`~/.local/bin` only when the install root is the machine's default root, and the root's own `bin` otherwise; every
writer and remover of the user bin in the setup engine and the browser harness installer now asks it. The pull request
says the readers it left alone are right because a scratch root's own `bin` "is not on PATH". It is on PATH: the
session launcher puts the machine root's `bin` first on every session's PATH, and the start-up path repair puts it
first on the Director's own PATH once `cc-devthrottle` resolves there. The consequence runs in the product's favour,
because a scratch Director's sessions find the scratch root's own tools, but it leaves the tool health probe and the
Python doctor naming a link in `~/.local/bin` that the repair no longer writes on a non-default root. Neither finding
touches the installed Director. The seat that built the work decides what to do with them.

## Scope: what I read, what I ran, what I could not reach

**Read in full, on the branch at `f98471512`.** Every file in the diff: `ToolShimPath.cs` (the new `ShimDirFor` and
`IsSameDirectory`, and the unchanged `UnixShimDir`, `EnsureOnProcessPath`, `WithDirectory`, `Contains`),
`BrowserHarnessInstaller.ShimDir`, `InstallLayout.cs` (both constructors, `Default()`, `ToolShimDir`,
`ToolShimDirFor`, `HomeDir`), the call sites in `PythonToolsInstaller.cs` (`ShimPathsFor`, the legacy alias purge,
`WriteShims`, `WriteUnixShims`), `ToolReconciler.ManagedShimPath`, the uninstaller's shim sweep,
`MacFileOwnership.InstallTargets`, the phase 6 proof program's one line, and all three test files the pull request adds
or edits: `ToolShimPathTests`, `InstallLayoutToolShimDirTests`, `BrowserHarnessInstallerTests` (with its
`WithRootAndPath` helper), and the `MacFileOwnershipTests` assertion.

**Read on the branch to answer the brief's six checks, unchanged by the pull request.** How a root is decided:
`CcStorage.Base`, `MachineRoot`, `MachineRootOf`, `IsDirectorInstanceHome`, `DefaultRoot` and `ResolveDefaultBase`;
how the Director sets its own root (`Program.cs`, the `InstanceContext` initialisation that sets `CC_DIRECTOR_ROOT`
to `<machine root>/instances/<slug>`); how tools are found: `SessionManager` (the session PATH),
`FleetToolPathRepair` (`RepairAtDirectorStart`, `OwnsTheSavedPath`, `HoldsFleetTool`, `RepairProcessPath`),
`CcToolExecutable.Resolve`, `ToolCatalogService.BuildDescriptor`, `ToolHealthProbe.PathGapReason` and its caller,
`FleetToolReachability`; the start-up order in `Program.cs` and `App.axaml.cs` (PATH fix, path repair, self-heal);
every construction of `InstallLayout` in product code; `InstallFinalizer.EnsureMacUserBinOnPath` and the setup
command line's finalisation; `AgentPresence`; `UnixProcessHost`; and in the Python toolbelt
`tools/cc-devthrottle/src/setup_ops.py` (`_shim_dir`, `_shim_paths`, `_command_status`, `doctor_data`,
`_path_gap_notice`, `repair`). For check 5, `PythonToolsHealAndShimTests` and `ToolReconcilerTests` in full enough to
list every call that writes or deletes a shim, and the one `WindowsOnlyFactAttribute` the repository has.

**Ran.** Only the pull request's own tests and the existing tests beside them, filtered, one project at a time, after
waiting for another Developer's test run on this machine to end. I did not run `PythonToolsHealAndShimTests` or
`ToolReconcilerTests`, as the brief instructs. Results are in the last section.

**Could not reach.** No macOS machine: the macOS answers are proven by the tests that fix the platform explicitly and
by reading, not by a run. No Windows machine: Windows is proven by reading that every Windows branch still composes
from `BinDir` and by the Windows cases in the new tests, which run on any host. I did not run the Python toolbelt's
tests. The slot-5 breakage itself was not reproduced: the brief forbids a real Director and the real user bin.

## The six checks

### 1. How "default root" is decided, and whether it can misjudge a real or a test Director

The rule compares `CcStorage.MachineRoot()` with `CcStorage.DefaultRoot()` as full paths without a trailing
separator, case-sensitively on Linux and case-insensitively elsewhere.

**The installed Director is judged default, on every platform.** With `CC_DIRECTOR_ROOT` unset, `Base()` and
`DefaultRoot()` call the same `ResolveDefaultBase` with the same three inputs, so the strings are identical. The
Director then sets `CC_DIRECTOR_ROOT` to its own instance home, `<machine root>/instances/<slug>`, and
`MachineRootOf` climbs out of any number of `instances/<name>` levels back to the machine root, so a Director started
by the launcher, by a systemd unit, by hand from a session's shell (which inherits an instance home), or restarted
from inside itself all come back to the same string. A named instance (`--instance`) is a sibling folder under the
same `instances`, so it is default too. That is the one case the brief flagged as a regression for every Linux and
macOS user, and it does not happen.

**A test Director is judged non-default when, and only when, its root is somewhere else.** A scratch
`CC_DIRECTOR_ROOT` outside the default root, the devlinux case, is not under a folder named `instances`, so
`MachineRootOf` returns it as given, it differs from the default, and the links go to `<scratch>/bin`. A test Director
whose root is `<default>/instances/slot-5` IS judged default and keeps maintaining `~/.local/bin`. That is correct, not
a gap: the tools are one installed copy per machine, that Director runs the machine's own `pyenv`, and re-linking
`~/.local/bin` at the machine's own environment is what the installed Director does too. The devlinux isolation notes
pair a scratch root with a scratch HOME; with this change a scratch root alone keeps the real user bin untouched, and
a scratch HOME alone makes `DefaultRoot()` the scratch home's own default, so its `~/.local/bin` is the scratch one.

**Two edges, neither a finding.** The same folder reached by two different strings (an explicit `CC_DIRECTOR_ROOT`
through a symlinked home, say) is judged non-default, because `Path.GetFullPath` does not resolve links. That is the
same limit `FleetToolPathRepair.OwnsTheSavedPath` has had since it was written, and `CC_DIRECTOR_ROOT` is documented
as the test-rig override, so I do not call it a regression. And there are now two implementations of "is this the
machine's default root": `OwnsTheSavedPath` (ordinal-ignore-case everywhere, trims quotes, no full-path
normalisation) and `IsSameDirectory` (ordinal on Linux, full path). They can disagree on Linux for two roots that
differ only in case; `OwnsTheSavedPath` only guards the Windows saved path, so nothing diverges today, but the next
Unix caller of either could. Worth one rule, in a later change.

### 2. Does a non-default Director still find its own tools once its links are in `<root>/bin`

**Yes, and better than the pull request's description claims.** The pull request body says the scratch root's own
`bin` "is not on PATH" and that a scratch Director "keeps using the machine's global tools on PATH". Both halves are
wrong on origin/main, in the product's favour:

- **Sessions.** `SessionManager` puts `CcStorage.Bin()` first on every session's PATH when that directory exists,
  on every platform (`PathWithOwnToolsFirst`). For a scratch root that is `<scratch>/bin`, which now holds the links.
  So a session of a scratch Director runs the scratch root's own `cc-devthrottle`, and the fleet preamble's "this
  command is already on your PATH" is true. Before this change `<scratch>/bin` held no cc-* links on Linux, so the
  session depended on `~/.local/bin`, which the self-heal had just re-pointed at the scratch root. That was the defect.
- **The Director itself.** `CcToolExecutable.Resolve` looks in `CcStorage.Bin()` first, before PATH, so the
  Director's own launches of its tools find `<scratch>/bin/<tool>`. `FleetToolPathRepair.RepairAtDirectorStart` puts
  `<machine root>/bin` first on the Director's running PATH on every platform, provided `cc-devthrottle` already
  resolves there. On a scratch root's first start the self-heal writes the links after that repair has run, so for
  that one run the Director's own PATH holds `~/.local/bin` (from `EnsureOnProcessPath`) and not `<scratch>/bin`. From
  the second start on, it holds both.
- **Agents.** The coding agents' own binaries are found through `AgentPresence` and `UnixProcessHost`, which
  still add `~/.local/bin`; those are not cc-* shims and are untouched.

What this leaves is the readers in finding 1.

### 3. Any writer, remover or reader of the user bin left on the old rule

**Writers and removers: none left.** I searched the product for `.local/bin`, `".local", "bin"`, the removed
`MacUserBinDir`, `UnixShimDir`, and every caller of `WriteShims`, `RemoveLegacyAliasShims`, `ShimPathsFor`,
`ManagedShimPath` and the uninstaller sweep. Everything that writes or deletes a cc-* link goes through
`InstallLayout.ToolShimDir` or `BrowserHarnessInstaller.ShimDir`, and both ask `ShimDirFor`. The hand-run scripts and
the two `install.py` files with a `--bin` flag are run by a person, as the pull request says. `InstallFinalizer`
edits shell start-up files, not the bin.

**Readers: three left, and they now give a wrong answer on a non-default root.** See finding 1.

### 4. Windows unchanged

`ShimDirFor` returns `Path.Combine(installRoot, "bin")` whenever `isWindows` is true, before it looks at the default
root or the home folder, and `InstallLayout.BinDir` is the same expression, so `ToolShimDir` on Windows equals
`BinDir`. Every Windows branch in `PythonToolsInstaller`, `ToolReconciler` and the uninstaller still composes from
`BinDir` directly. `BrowserHarnessInstaller.ShimDir` on Windows was `CcStorage.Bin()`, which is
`MachineRoot()/bin`, and is now `ShimDirFor(MachineRoot(), ...)` with `isWindows` true, which is the same string; the
existing Windows-only test for it is unchanged and the new rule-level tests assert the Windows answer for both the
default and a scratch root from any host. `FleetToolPathRepair`, the saved-path rule, is untouched.

### 5. The two test classes marked Windows-only that still run on Linux

**The marking does not skip them.** `[SupportedOSPlatform("windows")]` is an analyser annotation; every test in both
classes is a plain `[Fact]`. The repository's only skipping attribute, `WindowsOnlyFactAttribute`, lives in
`CcDirector.Core.Tests`, not in the engine tests. The engine test project is in the default gate (875 tests, by the
script's header), so on a Linux host these classes run.

**Before this change, a Linux run wrote into the real `~/.local/bin`, but not the link that broke devlinux.** Both
classes build `new InstallLayout(<temp>/local)` and call the real `WriteShims(new[] { "cc-pdf" })` (heal-and-shim
tests line 196; reconciler tests lines 91, 125, 314, 393 and 470). On Linux that was a symlink at the real
`~/.local/bin/cc-pdf` pointing into a temporary folder the test then deletes: a broken `cc-pdf`, machine-wide. The
legacy alias purge tests reach `RemoveLegacyAliasShims` and the purge inside `InstallAsync`, which delete entries in
the same directory for the retired names. No test in either class writes or removes a `cc-devthrottle` link, and the
reconciler's missing-shim path is driven with fakes placed in `pyenv/Scripts`, so a test run could not have been the
`cc-devthrottle` breakage the brief describes. It could have broken `cc-pdf`. The pull request's "would have written
real `~/.local/bin` links" is therefore right in substance and too broad in the name it implies.

**After this change, neither class touches the real home folder's bin.** The one-argument constructor leaves the
default root null, so `ToolShimDir` asks `CcStorage.DefaultRoot()` for the real machine's default, and a temporary
root is never equal to it; the links land in `<temp>/local/bin`. What remains is that both classes consult the real
machine to decide that: `DefaultRoot()` reads the real HOME and data-directory variables and, on a Linux host whose
`~/.local/share` does not exist, creates `~/.local/share/cc-director`. The outcome never depends on it (a temporary
root cannot equal the default), so it is not a rule 5a finding; it is one empty folder on a pristine machine, and the
three-argument constructor the pull request added would remove even that if the Developer wants both classes fully
off the machine. I did not run either class.

### 6. Tests real, on a fake file system

`ToolShimPathTests` are pure: six new tests feed made-up paths and read the answer. `InstallLayoutToolShimDirTests`
fix the default root and the home folder inside a temporary folder through the new constructor, so no test reads or
writes the real home; the two `WriteShims` tests exercise the real symlink writer against that temporary home, which
is the right level for proving the writer, and they assert both the link inside the root and that the temporary
global bin was left exactly as it was. The new `BrowserHarnessInstallerTests` case pins `CC_DIRECTOR_ROOT` to a
temporary root and asserts the answer on every platform. All of it decides by its own inputs, none by the machine.

One note, not a finding: the two `WriteShims` tests `return` on Windows and are counted as passed there having run
nothing, which is the pattern the file beside them already uses for its Windows-only case. An explicit skip would make
the Windows gate's count honest; the rule-level tests above them do run on Windows and carry the Windows answer.

## Findings

### Finding 1 (low): the three readers left on the old rule name a remedy that cannot come true on a non-default root

**Where.** `ToolHealthProbe.PathGapReason` (`src/CcDirector.Core/Tools/ToolHealthProbe.cs`, around line 205) reads
`ToolShimPath.UnixShimDir`; `ToolShimPath.EnsureOnProcessPath` adds `UnixShimDir` to the Director's PATH whatever the
root; and `tools/cc-devthrottle/src/setup_ops.py` (`_shim_dir`, `_shim_paths`) looks for every link in
`~/.local/bin` whatever the install root it climbed to.

**The harm, proven from the code.** Take a Director on a scratch root on a machine with no default install (a clean
machine, or the scratch-HOME isolation the devlinux notes use). At its first start the self-heal writes
`<scratch>/bin/cc-pdf`; the catalog finds the tool built in its install but not on the Director's PATH, because the
path repair ran before the link existed and `EnsureOnProcessPath` added only `~/.local/bin`. The probe then marks the
tool as not working with "installed, but its link `~/.local/bin/cc-pdf` is missing; cc-devthrottle setup repair
writes it". The repair runs, writes `<scratch>/bin/cc-pdf` again, and the message does not change, because nothing
will ever write that link for that root now. The same for every tool on the board, and for the wizard harness, which
the pull request's own comment names as a non-default root. It clears at the next start, when the path repair finds
`cc-devthrottle` in `<scratch>/bin`. Likewise `cc-devthrottle setup doctor`, run from a session of that Director,
reports "missing or incomplete tool shims" for every tool, and `repair` ends with a PATH notice naming
`~/.local/bin`, while the links it just wrote sit in `<scratch>/bin`, which that session's PATH already holds.

**Why it is low.** The installed Director is never affected. It is a wrong diagnosis with a wrong remedy on a test
root, for one run, on a machine without a default install. But the pull request body justifies leaving these readers
alone with "its own links sit in `<root>/bin`, which is not on PATH", and that premise is false: it is on PATH for
every session and, after the first start, for the Director. The readers should ask the same rule the writers ask, or
the pull request's text should stop claiming they are right. The Developer decides which.

#### Developer's answer to finding 1: fixed

The three readers now ask the same rule the writers ask, one definition per language, keyed the same way.

- **.NET.** `ToolShimPath.MachineShimDir` is the rule `ShimDirFor` asked about this process's machine root. Its pure
  form, `ShimDirForRootSetting(rootSetting, defaultRoot, home, isWindows)`, takes the machine root exactly as
  `CcStorage.MachineRoot` does: the default root when the CC_DIRECTOR_ROOT setting is empty, otherwise
  `CcStorage.MachineRootOf` of the setting, so an instance home climbs out to the root above it. `UnixShimDir` is
  removed, so nothing can name `~/.local/bin` without the rule any more.
  - `ToolShimPath.EnsureOnProcessPath` adds `MachineShimDir`. A scratch Director's first start now puts
    `<scratch>/bin` on its own PATH, where the self-heal is about to write the links.
  - `ToolHealthProbe.PathGapReason` takes the shim directory as a parameter, and its one caller passes
    `MachineShimDir`, so the board names the link the repair writes.
  - `BrowserHarnessInstaller.ShimDir` is now `ToolShimPath.MachineShimDir`, the one composition of the rule rather
    than a second copy of it.
- **Python.** `setup_ops._shim_dir_for(install_root, default_root, home, is_windows)` is the Python form of
  `ShimDirFor`, with the same four inputs and the same answers; `_is_same_directory` compares the way
  `IsSameDirectory` does. `_default_root()` is split out of `_install_root()` (the branch that ignores
  CC_DIRECTOR_ROOT). `_shim_dir` and `_shim_paths` both ask `_shim_dir_for`, so `doctor` looks for the links of a
  scratch root in `<scratch>/bin` and `repair`'s PATH notice names that directory.

**Tests, all on inputs the test gives, never the machine (rule 5a).**

- `ToolShimPathTests`: 5 new cases for `ShimDirForRootSetting`: no setting and an empty setting go to the user bin;
  an instance home under the default root goes to the user bin; a scratch root and an instance home under a scratch
  root go to the scratch root's own `bin`. The old `UnixShimDir` test is removed along with the property.
- `ToolHealthProbePathGapTests`: every case now passes a made-up shim directory instead of reading the real home;
  1 new case proves a scratch root's reason names `<scratch>/bin/cc-devthrottle`.
- `tools/cc-devthrottle/tests/test_setup_ops.py`: 5 new cases for `_shim_dir_for` (default root, scratch root,
  trailing separator, and Windows for both). 2 new cases on a scratch root with no default install: `doctor` reports
  no problem when the link is in `<scratch>/bin` and names nothing under `~/.local/bin`, and `repair` names
  `<scratch>/bin`. The `isolated_install` fixture used to pin CC_DIRECTOR_ROOT to a folder that is not the default
  root of its made-up home, which under the new rule is a scratch root. It now pins the default root of that home, so
  every existing case still models the machine's own install.

| Project | Filter | Result |
|---|---|---|
| `src/CcDirector.Core.UnitTests` | `ToolShimPathTests`, `ToolHealthProbePathGapTests`, `ToolReadersUseTheMachineRootTests` | 34 passed, 0 failed |
| `src/CcDirector.Core.Tests` | `BrowserHarnessInstallerTests` | 16 passed, 0 failed |
| `tools/cc-devthrottle` (pytest, in a scratch environment) | `tests/test_setup_ops.py`, `tests/test_setup_machine_root.py` | 50 passed, 0 failed |

On devlinux, .NET 10 at `~/.dotnet`, Debug, one project at a time after checking that no other test run was going.
The Python tests ran with `FORCE_COLOR=1`, as continuous integration runs them, in a throwaway environment made from
the machine's bundled Python; nothing was installed into the machine's own `pyenv`. `PythonToolsHealAndShimTests` and
`ToolReconcilerTests` were not run, as instructed. No real Director was run. Afterwards `~/.local/bin/cc-devthrottle`
and `~/.local/bin/cc-pdf` still point at `/home/soren/.local/share/cc-director/pyenv/bin/`, as before.

### Finding 2 (low): the pull request's own account of the test classes and of PATH is inaccurate in two places

This is about the record, not the code. The record will be read by the next seat as proof.

- "Run on Linux before this change, they would have written real `~/.local/bin` links": true for `cc-pdf` and for the
  legacy alias purge, as shown under check 5, and no test writes a `cc-devthrottle` link. A reader could take the
  sentence as "a test run was the devlinux breakage", which the code does not support.
- "Its own links sit in `<root>/bin`, which is not on PATH; that matches Windows, where only the machine root's
  `bin` is on PATH": the session launcher prepends `CcStorage.Bin()` on every platform, and on a scratch root that is
  the scratch root's own `bin`. Windows matches, but because both are on PATH, not because neither is.

Neither sentence changes what shipped; both would mislead the next reader of the mission record.

#### Developer's answer to finding 2: recorded, and the pull request text corrected

Both sentences were wrong as written, and the pull request description is corrected:

- The two classes marked Windows-only, when run on Linux before this change, wrote real `~/.local/bin/cc-pdf`
  links pointing into temporary folders that were then deleted, and their legacy alias purge removed retired names
  from that directory. No test in either class writes or removes a `cc-devthrottle` link, so a test run was not the
  devlinux `cc-devthrottle` breakage. At most it could have left a broken `cc-pdf`.
- The scratch root's own `bin` is on PATH: the session launcher puts `CcStorage.Bin()` first on every session's PATH
  on every platform, and the Director's own PATH now holds it from the first start (finding 1). Windows matches
  because both are on PATH, not because neither is. The "readers only, unchanged" paragraph is replaced by the fix
  above.

## What I ran, and what it showed

All on devlinux, .NET 10 at `~/.dotnet`, Debug, in the review worktree at `f98471512`, each project built once and
run with `--no-build`, one project at a time, after another Developer's filtered Gateway run on this machine had
finished. The filters name the classes exactly; nothing else in those projects ran.

| Project | Filter | Result |
|---|---|---|
| `src/CcDirector.Core.UnitTests` | `ToolShimPathTests`, `ToolHealthProbePathGapTests`, `ToolReadersUseTheMachineRootTests` | 29 passed, 0 failed |
| `tools/cc-director-setup-engine.Tests` | `InstallLayoutToolShimDirTests`, `MacFileOwnershipTests`, `InstallLayoutMachineRootTests` | 33 passed, 0 failed |
| `src/CcDirector.Core.Tests` | `BrowserHarnessInstallerTests` | 16 passed, 0 failed |

These match the pull request's own counts for the same classes (the Developer also ran `InstallLayoutTests`, whose
four failures are a `C:\root` fixture that is relative on Linux and fail the same way on main; I left that class out
rather than report a known Linux failure as if it were news). Not run, on the brief's instruction:
`PythonToolsHealAndShimTests` and `ToolReconcilerTests`.

After the runs, the real `~/.local/bin/cc-devthrottle` and `~/.local/bin/cc-pdf` on this machine still point at
`/home/soren/.local/share/cc-director/pyenv/bin/`, the machine's own install, and no `cc-shimdir-*` temporary folder
was left behind.
