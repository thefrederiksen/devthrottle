using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Python tools bundle install, with the steps taken (for logs / UI).</summary>
public sealed record PythonToolsResult(
    bool Success, string Message, IReadOnlyList<string> Steps, int ToolCount, string? BundleVersion);

/// <summary>
/// Installs the Python cc-* tools as ONE shared venv, replacing the per-tool PyInstaller exes.
/// Consumes the two release assets built by scripts/build-python-bundle.(ps1|sh):
///   Windows: cc-python-win-x64.zip + cc-tools-pyenv-win-x64.zip
///   macOS:   cc-python-macos-arm64.tar.gz + cc-tools-pyenv-macos-arm64.tar.gz
///   Linux:   cc-python-linux-x64.tar.gz + cc-tools-pyenv-linux-x64.tar.gz
/// Each carries a relocatable CPython and a de-duped wheelhouse + requirements.lock + tools-manifest.json.
///
/// Flow: download + SHA-verify both assets, read the tools bundle, and ONLY when a rebuild is actually
/// needed (the recorded version differs, or the on-disk runtime is not healthy) re-provision the shared
/// base Python and rebuild the venv, pip-install every tool OFFLINE (--no-index --find-links wheelhouse),
/// then create tool shims (bin\&lt;script&gt;.cmd on Windows; ~/.local/bin/&lt;script&gt; symlinks on macOS).
/// Per-user, no admin.
///
/// Safety (issue #994): the base Python is a runtime other Directors share. A redundant install (the same
/// version, already healthy) is a genuine no-op - it never deletes or re-extracts the shared Python. When a
/// rebuild IS needed the new Python is staged and verified runnable, then swapped in with a whole-directory
/// rename, so a partial or locked extract can never leave the live runtime half-populated. Concurrent
/// installs across processes are serialized by a machine-local lock.
/// </summary>
public sealed class PythonToolsInstaller
{
    /// <summary>
    /// The bundled-CPython asset name for a given platform. The platform is a PARAMETER rather than
    /// read from the environment, following <see cref="Component.AssetFor"/>, so every branch can be
    /// asserted from any development machine. A property that reads the environment can only ever be
    /// tested for the one platform the test run happens to be on, which is how the Linux branch below
    /// came to be missing without a single test going red.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The platform has no Python tools bundle. Deliberately a throw and not a default: returning
    /// somebody else's asset is what the two-way branch used to do, and it produced a Linux Director
    /// that downloaded the macOS arm64 CPython, staged a Mach-O binary, failed to import its own
    /// standard library, and reported a corrupt-download reason for a wrong-platform cause.
    /// </exception>
    public static string PythonAssetFor(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "cc-python-win-x64.zip"
        : platform == OSPlatform.OSX ? "cc-python-macos-arm64.tar.gz"
        : platform == OSPlatform.Linux ? "cc-python-linux-x64.tar.gz"
        : throw new PlatformNotSupportedException($"There is no Python tools bundle for {platform}.");

    /// <summary>
    /// The tools wheelhouse asset name for a given platform. See <see cref="PythonAssetFor"/> for why
    /// the platform is a parameter and why an unknown one throws.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The platform has no Python tools bundle.</exception>
    public static string ToolsAssetFor(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "cc-tools-pyenv-win-x64.zip"
        : platform == OSPlatform.OSX ? "cc-tools-pyenv-macos-arm64.tar.gz"
        : platform == OSPlatform.Linux ? "cc-tools-pyenv-linux-x64.tar.gz"
        : throw new PlatformNotSupportedException($"There is no Python tools bundle for {platform}.");

    /// <summary>The bundled-CPython asset for the current OS.</summary>
    public static string PythonAsset => PythonAssetFor(HostPlatform.Current);

    /// <summary>The tools wheelhouse asset for the current OS.</summary>
    public static string ToolsAsset => ToolsAssetFor(HostPlatform.Current);

    /// <summary>The component id the bundle's version is tracked under in installed.json.</summary>
    public const string ComponentId = "python-tools";

    /// <summary>
    /// Bound for the venv-create step. Creating an empty venv is quick (seconds); a multi-minute bound is
    /// plenty and only exists to keep a wedged "python -m venv" from hanging the install forever.
    /// </summary>
    public static readonly TimeSpan VenvCreateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bound for the offline pip install of the whole wheelhouse (roughly twenty wheels). This is the long,
    /// legitimately-slow step, so the bound is generous; but it IS bounded, so a pip that hangs (the field
    /// failure behind issue #577) fails loudly in finite time instead of stalling the wizard forever.
    /// </summary>
    public static readonly TimeSpan PipInstallTimeout = TimeSpan.FromMinutes(15);

    private readonly InstallLayout _layout;

    public PythonToolsInstaller(InstallLayout layout)
        => _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    public async Task<PythonToolsResult> InstallAsync(
        ResolvedRelease release, ReleaseSource source,
        IProgress<string>? progress = null,
        IProgress<int>? percent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var steps = new List<string>();
        void Step(string m) { steps.Add(m); EngineLog.Write($"[PythonToolsInstaller] {m}"); progress?.Report(m); }

        var pyAsset = release.Manifest.TryGetAsset(PythonAsset);
        var toolsAsset = release.Manifest.TryGetAsset(ToolsAsset);
        if (pyAsset is null || toolsAsset is null)
            return Fail(steps, $"release is missing the Python bundle assets ({PythonAsset} / {ToolsAsset}).");

        string? pyZip = null, toolsZip = null, bundleDir = null;
        try
        {
            // 0. Heal: purge any orphaned legacy alias shims left by older installs (issue #823).
            //    The retired per-tool fleet commands (cc-send, cc-whoami, ...) were consolidated into
            //    the single cc-devthrottle command, so their venv exes no longer ship. A bin\cc-send.cmd
            //    left over from an older install therefore points at a missing pyenv\Scripts\cc-send.exe
            //    and fails with exit 127. These names are in no current manifest, so the managed-shim
            //    removal below never touches them - we purge them explicitly. This runs BEFORE the
            //    already-installed early-out so a "repair" on an up-to-date machine still heals them.
            Step("removing orphaned legacy alias shims");
            RemoveLegacyAliasShims();

            // 1. Download + verify both assets. Byte-level progress drives the row's status text
            //    ("Downloading 118.2 MB / 334.5 MB") and the 0-20% band of the bar. Both zips share
            //    one combined total (matching the size shown on the UI row) so the bar never resets
            //    between the two downloads. Reports arrive ~once per MiB (throttled in ReleaseSource).
            var totalDownload = pyAsset.Size + toolsAsset.Size;
            var downloadGate = new object();
            long reportedDownload = 0;
            void ReportDownload(long overall)
            {
                if (totalDownload <= 0) return;
                // Progress<T> posts its callbacks asynchronously, so a late report from the
                // first zip can arrive after the second download has started. Never let the
                // counter move backwards.
                var current = Math.Min(overall, totalDownload);
                lock (downloadGate)
                {
                    if (current <= reportedDownload) return;
                    reportedDownload = current;
                }
                percent?.Report((int)(current * 20 / totalDownload));
                progress?.Report($"Downloading {FormatMb(current)} / {FormatMb(totalDownload)}");
            }

            Step($"downloading {PythonAsset} ({FormatMb(pyAsset.Size)})");
            pyZip = await source.DownloadAssetAsync(PythonAsset, release.DownloadUrls, ct,
                new Progress<(long downloaded, long total)>(p => ReportDownload(p.downloaded)));
            if (!Hashing.Sha256Matches(pyZip, pyAsset.Sha256))
                return Fail(steps, $"{PythonAsset} SHA-256 mismatch; download rejected.");

            Step($"downloading {ToolsAsset} ({FormatMb(toolsAsset.Size)})");
            toolsZip = await source.DownloadAssetAsync(ToolsAsset, release.DownloadUrls, ct,
                new Progress<(long downloaded, long total)>(p => ReportDownload(pyAsset.Size + p.downloaded)));
            if (!Hashing.Sha256Matches(toolsZip, toolsAsset.Sha256))
                return Fail(steps, $"{ToolsAsset} SHA-256 mismatch; download rejected.");

            // 2. Extract the TOOLS bundle to a temp dir (non-destructive) so we can read the bundle version
            //    and health-check the installed runtime BEFORE touching the shared base Python. The base
            //    Python is a runtime other Directors share; re-extracting it when nothing has changed is
            //    what corrupted it in the field (issue #994) - a redundant second install (the Gateway step
            //    re-running the tools install) blew the working Python away and then died on a file a running
            //    Director held open, leaving it with no standard library.
            Step("reading the tools bundle");
            percent?.Report(20);
            bundleDir = Path.Combine(Path.GetTempPath(), $"cc-pytools-{Guid.NewGuid():N}");
            var (tOk, tExtractOut) = Extract(toolsZip, bundleDir);
            if (!tOk) return Fail(steps, $"extracting {ToolsAsset} failed: {Trim(tExtractOut)}");

            var manifestPath = Path.Combine(bundleDir, "tools-manifest.json");
            var wheelhouse = Path.Combine(bundleDir, "wheelhouse");
            if (!File.Exists(manifestPath)) return Fail(steps, "bundle is missing tools-manifest.json.");
            if (!Directory.Exists(wheelhouse)) return Fail(steps, "bundle is missing the wheelhouse.");

            var manifest = ToolsBundleManifest.Load(manifestPath);
            percent?.Report(25);

            var pythonExe = OperatingSystem.IsWindows()
                ? Path.Combine(_layout.PythonDir, "python.exe")
                : Path.Combine(_layout.PythonDir, "bin", "python3");
            var venvPython = Path.Combine(_layout.PyenvBinDir, OperatingSystem.IsWindows() ? "python.exe" : "python3");

            // Serialize the whole decision-and-rebuild across processes. Several Directors plus the installer
            // can run at once and all target the same shared python\ and pyenv\; two concurrent resets/extracts
            // of one tree is the race that half-destroyed the runtime (issue #994). Holding the lock across the
            // early-out too means a second install that waits behind a first one wakes to find the runtime
            // already current and healthy, and simply no-ops instead of rebuilding.
            SharedInstallLock installLock;
            try
            {
                installLock = SharedInstallLock.Acquire(PipInstallTimeout, _layout.LocalRoot);
            }
            catch (TimeoutException ex)
            {
                return Fail(steps, $"another Python tools install is in progress and did not finish in time ({ex.Message}); nothing was changed.");
            }
            using var heldLock = installLock;

            // 2b. Early-out BEFORE any destructive work: the recorded version matches AND the runtime is
            //     genuinely HEALTHY - the venv has every tool's console script, the venv python runs, and the
            //     base Python runs (its standard library is intact). Nothing to do; do NOT delete or
            //     re-extract the shared base Python. A version match alone is not enough: a venv whose
            //     site-packages was stripped, or a base Python whose standard library went missing (issue
            //     #994), is the exact half-installed state that must trigger a repair, not a false skip.
            var installedAtStart = InstalledManifest.Load(_layout);
            var installedBundle = installedAtStart.Get(ComponentId);
            var runtimeHealthy = File.Exists(venvPython)
                && VenvHasAllTools(manifest.Scripts)
                && PythonRuntimeProbe.CanImportStdlib(pythonExe);
            if (installedBundle == manifest.BundleVersion && runtimeHealthy)
            {
                Step($"Python tools bundle {manifest.BundleVersion} already installed and healthy; skipping rebuild");
                percent?.Report(100);
                return new PythonToolsResult(true,
                    $"Python tools bundle {manifest.BundleVersion} already installed.",
                    steps, manifest.Dists.Count, manifest.BundleVersion);
            }
            if (installedBundle == manifest.BundleVersion && !runtimeHealthy)
                Step($"installed.json claims bundle {manifest.BundleVersion}, but the runtime is not healthy; rebuilding to repair");

            // 3. (Re)provision the base Python CRASH-SAFELY. Extract it to a staging dir, verify it is a
            //    COMPLETE, runnable interpreter (python.exe present AND it can import its own standard
            //    library), then swap it into place with a whole-directory rename. The delete/extract happens
            //    on the staging copy, so a partial or locked extract can no longer half-populate the live
            //    tree: on ANY failure the previous working Python is left untouched.
            Step("staging bundled Python");
            var staged = _layout.PythonDir + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                ResetDir(staged);
                var (pyOk, pyExtractOut) = Extract(pyZip, staged);
                if (!pyOk) return Fail(steps, $"extracting {PythonAsset} failed: {Trim(pyExtractOut)}");

                var stagedPythonExe = OperatingSystem.IsWindows()
                    ? Path.Combine(staged, "python.exe")
                    : Path.Combine(staged, "bin", "python3");
                if (!File.Exists(stagedPythonExe))
                    return Fail(steps, $"staged Python is missing its interpreter at {stagedPythonExe}; the existing Python was left untouched.");
                if (!PythonRuntimeProbe.CanImportStdlib(stagedPythonExe))
                    return Fail(steps, "staged Python is incomplete - it cannot import its standard library (a partial extract). The existing Python was left untouched.");

                Step("swapping in the verified Python");
                if (!SwapDir(staged, _layout.PythonDir, out var swapError))
                    return Fail(steps, $"could not replace the base Python (a running Director may be holding its files): {swapError}. The existing Python was left untouched.");
            }
            finally
            {
                if (Directory.Exists(staged)) TryDeleteDir(staged);
            }

            if (!File.Exists(pythonExe))
                return Fail(steps, $"bundled python not found at {pythonExe} after the swap.");

            // 4. Create the shared venv from the freshly swapped-in python (on-target, so console-script
            // paths are correct). Remove the managed bin shims FIRST, so that if anything below fails or is
            // interrupted (a hung pip killed by the timeout, the wizard closed mid-run, a crash), we never
            // leave a bin\<name>.cmd whose pyenv\Scripts\<name>.exe target the venv reset just deleted. The
            // shim and its target exe live and die together: shims are (re)written ONLY after pip succeeds
            // AND the venv is verified healthy. This is the atomic-shim guarantee for issue #577.
            Step("creating the shared Python venv");
            RemoveManagedShims(manifest.Scripts);
            ResetDir(_layout.PyenvDir);
            var (venvExit, venvOut) = ProcessRunner.Run(pythonExe, $"-m venv \"{_layout.PyenvDir}\"", onStdoutLine: null, VenvCreateTimeout);
            if (venvExit != 0) return Fail(steps, $"venv creation failed ({venvExit}): {Trim(venvOut)}");
            // Guard: even on a zero exit, the venv must actually have produced its python. A venv whose
            // interpreter is missing means the create silently did nothing - fail loud now rather than throw
            // a Win32 "file not found" when the pip step tries to run the missing interpreter.
            if (!File.Exists(venvPython))
                return Fail(steps, $"venv creation reported success but produced no interpreter at {venvPython}.");

            // 5. Install every tool OFFLINE from the wheelhouse. Percent bands across the whole
            //    bundle install: download 0-20 (byte-level, above), extract 20-25, then two-phase
            //    pip progress for honest pacing:
            //    a. Parse phase (~10 s): pip prints "Processing <wheel>" for all wheels in a burst.
            //       Drives status+percent 25->40% so the user sees motion immediately.
            //    b. Install phase (3-8 min, silent in pip's stdout): poll site-packages\*.dist-info
            //       directory count on a 1.5 s timer — each installed package writes one .dist-info
            //       dir, so that count IS real progress. Drives percent 40->95%.
            //    Using "Processing" lines for the whole 0-100% (as we originally did) was misleading:
            //    the bar shot to 99% in 10 s then sat there motionless for the 5-minute middle.
            var wheelCount = Directory.GetFiles(wheelhouse, "*.whl").Length;
            Step($"installing {manifest.Dists.Count} tools offline from the wheelhouse ({wheelCount} wheels)");
            var distArgs = string.Join(" ", manifest.Dists.Select(d => $"\"{d}\""));
            var pipArgs = $"-m pip install --no-index --find-links \"{wheelhouse}\" --no-warn-script-location --progress-bar=off {distArgs}";

            // Where pip will land .dist-info dirs once it starts installing. Resolved relative to the
            // venv layout: Lib\site-packages on Windows, lib\python*\site-packages on Unix.
            var sitePackages = ResolveSitePackagesDir(_layout.PyenvDir);

            var installing = false;
            int processed = 0;
            void OnPipLine(string line)
            {
                EngineLog.Write($"[pip] {line}");
                if (line.StartsWith("Processing ", StringComparison.Ordinal))
                {
                    processed++;
                    var pkg = ExtractWheelPackageName(line);
                    progress?.Report(wheelCount > 0
                        ? $"Parsing {processed}/{wheelCount}: {pkg}"
                        : $"Parsing: {pkg}");
                    if (wheelCount > 0) percent?.Report(Math.Min(40, 25 + processed * 15 / wheelCount));
                }
                else if (line.StartsWith("Installing collected packages", StringComparison.Ordinal))
                {
                    installing = true;
                    progress?.Report(wheelCount > 0
                        ? $"Installing {wheelCount} packages (this takes a few minutes)..."
                        : "Installing packages (this takes a few minutes)...");
                    percent?.Report(40);
                }
            }

            // Background poller: count .dist-info dirs once pip enters the install phase. Real progress.
            using var pollCts = new CancellationTokenSource();
            var pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    try { await Task.Delay(1500, pollCts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (!installing || !Directory.Exists(sitePackages)) continue;
                    try
                    {
                        var done = Directory.GetDirectories(sitePackages, "*.dist-info").Length;
                        if (wheelCount > 0)
                        {
                            var p = Math.Min(95, 40 + (done * 55 / wheelCount));
                            percent?.Report(p);
                            progress?.Report($"Installing {done}/{wheelCount} packages...");
                            EngineLog.Write($"[PythonToolsInstaller] install-progress: {done}/{wheelCount} ({p}%)");
                        }
                    }
                    catch { /* polling must never throw; pip is the source of truth */ }
                }
            });

            var (pipExit, pipOut) = ProcessRunner.Run(venvPython, pipArgs, OnPipLine, PipInstallTimeout);
            pollCts.Cancel();
            try { await pollTask; } catch { /* poller cancellation */ }

            // A timeout reports the sentinel exit code; surface it as the loud, bounded failure it is so the
            // user (and the log) see "pip hung and was killed" rather than a generic non-zero exit.
            if (pipExit == ProcessRunner.TimeoutExitCode)
                return Fail(steps, $"offline pip install timed out after {PipInstallTimeout.TotalMinutes:F0} minutes and was killed: {Trim(pipOut)}");
            if (pipExit != 0) return Fail(steps, $"offline pip install failed ({pipExit}): {Trim(pipOut)}");
            percent?.Report(100);
            progress?.Report($"Installed {wheelCount} packages");

            // 6. Verify the venv is healthy BEFORE writing any shim or recording the version. Every tool's
            // console script must be on disk. If pip exited 0 but a script is missing (a partial/corrupt
            // wheelhouse), we must NOT write shims to missing targets nor stamp a version that would suppress
            // a future repair - we fail loud and leave no stale shim behind.
            if (!VenvHasAllTools(manifest.Scripts))
            {
                var missing = manifest.Scripts.Where(s => !File.Exists(ConsoleScriptPath(s))).ToList();
                return Fail(steps, $"venv is incomplete after pip install: missing console scripts [{string.Join(", ", missing)}]. No shims written, version not recorded.");
            }

            // 7. The venv is healthy: NOW write bin\<script>.cmd shims (target exes are guaranteed present).
            Step($"writing {manifest.Scripts.Count} tool shims to bin");
            WriteShims(manifest.Scripts);

            // 8. Record the bundle version ONLY now that the venv is healthy and the shims point at real
            // targets. Gating im.Set on VenvHasAllTools means a half-built venv never records a version that
            // would make the version-gated auto-update skip the machine forever (issue #577).
            var im = InstalledManifest.Load(_layout);
            im.Set(ComponentId, manifest.BundleVersion);
            im.Save(_layout);
            // Persist the script list so the auto-updater can probe venv health offline (without re-downloading
            // the bundle just to learn which scripts to expect).
            PythonToolsState.SaveScripts(_layout, manifest.Scripts);

            Step($"Python tools bundle {manifest.BundleVersion} installed ({manifest.Dists.Count} tools)");
            return new PythonToolsResult(true,
                $"Installed {manifest.Dists.Count} Python tools (bundle {manifest.BundleVersion}).",
                steps, manifest.Dists.Count, manifest.BundleVersion);
        }
        finally
        {
            TryDelete(pyZip);
            TryDelete(toolsZip);
            TryDeleteDir(bundleDir);
        }
    }

    /// <summary>The venv console-script path for a tool script (used as an on-disk presence probe).</summary>
    private string ConsoleScriptPath(string script) => ConsoleScriptPath(_layout, script);

    /// <summary>The venv console-script path for a tool script under a given layout.</summary>
    public static string ConsoleScriptPath(InstallLayout layout, string script)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return OperatingSystem.IsWindows()
            ? Path.Combine(layout.PyenvScriptsDir, $"{script}.exe")
            : Path.Combine(layout.PyenvBinDir, script);
    }

    /// <summary>
    /// True only when every tool console script the bundle promises is actually on disk in the venv.
    /// This is the health probe that distinguishes a real install from a half-installed venv (empty or
    /// stripped site-packages). An empty script list returns false so a manifest with nothing to verify
    /// forces a rebuild rather than a false "already installed".
    /// </summary>
    private bool VenvHasAllTools(IReadOnlyList<string> scripts) => VenvHasAllTools(_layout, scripts);

    /// <summary>
    /// True only when every tool console script in <paramref name="scripts"/> is on disk in the venv for
    /// <paramref name="layout"/>. Exposed so the auto-updater (<see cref="ToolUpdater"/>) can reuse the exact
    /// same health probe to decide whether an on-disk venv needs repairing, without re-downloading the
    /// bundle. An empty list returns false (nothing to verify is treated as not-healthy, forcing a rebuild).
    /// </summary>
    public static bool VenvHasAllTools(InstallLayout layout, IReadOnlyList<string> scripts)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count == 0) return false;
        foreach (var script in scripts)
            if (!File.Exists(ConsoleScriptPath(layout, script))) return false;
        return true;
    }

    /// <summary>
    /// Command names that no longer ship, and whose bin shim an older install may still be carrying.
    /// Two groups:
    /// <list type="bullet">
    /// <item>The retired per-tool fleet commands consolidated into the single cc-devthrottle command
    /// (issue #823): cc-send, cc-ask, cc-spawn, cc-sessions, cc-whoami, cc-settings, cc-cron,
    /// cc-fleet-selftest.</item>
    /// <item>cc-playwright, cut from the shipped toolbelt (issue #1002).</item>
    /// </list>
    /// In both cases the venv console script is gone, so a leftover shim resolves to a missing target.
    /// For the fleet aliases that is exit 127; for a tool dropped from the manifest it is worse - the
    /// self-checking shim body tells a HEALTHY install that "cc-* tools are not fully installed" and
    /// sends the user to a repair that will never put the tool back. Note the ordinary
    /// <c>RemoveManagedShims</c> pass cannot clean either group: it only walks the CURRENT manifest, and
    /// these names are in no manifest. That is precisely why this explicit purge exists, and why cutting
    /// a tool from the shipped set means adding its name here.
    /// The installer purges these on every install/repair. Kept in sync with cc-devthrottle's
    /// setup_ops.LEGACY_ALIAS_NAMES (the doctor diagnostic) so the same retired names are reported and
    /// cleaned. These names never overlap the shipping tools, so purging them can never remove a live
    /// tool's shim - guarded by a test against the shipped manifest rather than left as a promise.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyAliasShimNames = new[]
    {
        "cc-send", "cc-ask", "cc-spawn", "cc-sessions", "cc-whoami", "cc-settings", "cc-cron", "cc-fleet-selftest",
        "cc-playwright",
    };

    /// <summary>
    /// All possible on-disk shim file paths for one legacy alias name: on Windows a bin\&lt;name&gt;.cmd,
    /// a bin\&lt;name&gt;.exe (from an even older PyInstaller install), and a bare-name bash shim; on macOS
    /// a ~/.local/bin/&lt;name&gt; symlink. This is the single definition of WHERE a legacy alias shim can
    /// live, shared by the detection (<see cref="FindOrphanedLegacyAliasShims"/>) and the purge
    /// (<see cref="RemoveLegacyAliasShims"/>) so neither duplicates the path set.
    /// </summary>
    private IEnumerable<string> LegacyAliasShimPaths(string name) =>
        OperatingSystem.IsWindows()
            ? new[]
              {
                  Path.Combine(_layout.BinDir, $"{name}.cmd"),
                  Path.Combine(_layout.BinDir, $"{name}.exe"),
                  Path.Combine(_layout.BinDir, name), // bare-name bash shim
              }
            : new[] { Path.Combine(_layout.MacUserBinDir, name) };

    /// <summary>
    /// The orphaned legacy alias shim files that currently exist on disk (issue #823) - pure detection, no
    /// mutation. Exposed so the reconciler (<see cref="ToolReconciler"/>) can decide whether a purge is even
    /// needed BEFORE touching the filesystem (its happy path performs no mutation), reusing the same legacy
    /// name list and path set the purge uses instead of re-deriving them.
    /// </summary>
    internal IReadOnlyList<string> FindOrphanedLegacyAliasShims() =>
        LegacyAliasShimNames.SelectMany(LegacyAliasShimPaths).Where(File.Exists).ToList();

    /// <summary>
    /// Delete any orphaned legacy alias shims from the install (issue #823). Each retired alias may have left
    /// a bin\&lt;name&gt;.cmd, a bare-name bash shim, and (from an even older PyInstaller install) a
    /// bin\&lt;name&gt;.exe on Windows, or a ~/.local/bin/&lt;name&gt; symlink on macOS - all pointing at a
    /// venv exe that no longer exists. Removing them is what satisfies "no shim points at a missing exe":
    /// the command becomes absent and the fleet banner directs the agent to the cc-devthrottle subcommand
    /// instead. Best-effort: a shim we cannot delete is logged, never thrown. Public so the reconciler can
    /// reuse it as the corrective action for orphaned-shim drift.
    /// </summary>
    public void RemoveLegacyAliasShims()
    {
        foreach (var path in FindOrphanedLegacyAliasShims())
        {
            try
            {
                File.Delete(path);
                EngineLog.Write($"[PythonToolsInstaller] removed orphaned legacy alias shim {path}");
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[PythonToolsInstaller] could not remove legacy alias shim {path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Remove the managed bin shims for the given scripts up front, before a venv rebuild deletes their
    /// targets. This is what makes the install atomic: a failed/interrupted run leaves NO shim pointing at a
    /// now-missing console script. The shims are rewritten only after the venv is verified healthy.
    /// </summary>
    private void RemoveManagedShims(IReadOnlyList<string> scripts)
    {
        foreach (var script in scripts)
        {
            var paths = OperatingSystem.IsWindows()
                ? new[]
                  {
                      Path.Combine(_layout.BinDir, $"{script}.cmd"),
                      Path.Combine(_layout.BinDir, $"{script}.exe"),
                      Path.Combine(_layout.BinDir, script), // bare-name bash shim
                  }
                : new[] { Path.Combine(_layout.MacUserBinDir, script) };
            foreach (var path in paths)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { EngineLog.Write($"[PythonToolsInstaller] could not remove managed shim {path}: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Create the tool shims: bin\&lt;script&gt;.cmd (plus a bare-name bash shim) on Windows, ~/.local/bin
    /// symlinks on macOS. Public so the reconciler can reuse it to (re)create a single tool's missing shim
    /// whose venv console-script target already exists - the lightweight corrective action for shim-only
    /// drift, which must never duplicate the shim-body or path conventions defined here.
    /// </summary>
    public void WriteShims(IReadOnlyList<string> scripts)
    {
        if (OperatingSystem.IsWindows()) WriteWindowsShims(scripts);
        else WriteUnixShims(scripts);
    }

    /// <summary>
    /// Each shim is a tiny .cmd in bin (already on PATH) that forwards to the venv's console-script
    /// exe via a path relative to bin, so the whole install tree stays movable as a unit.
    /// </summary>
    private void WriteWindowsShims(IReadOnlyList<string> scripts)
    {
        Directory.CreateDirectory(_layout.BinDir);
        foreach (var script in scripts)
        {
            // Migration: a prior (PyInstaller) install may have left bin\<script>.exe. Windows
            // PATHEXT prefers .exe over .cmd, so a leftover exe would shadow the new shim - remove it.
            var staleExe = Path.Combine(_layout.BinDir, $"{script}.exe");
            if (File.Exists(staleExe))
            {
                try { File.Delete(staleExe); EngineLog.Write($"[PythonToolsInstaller] removed stale {script}.exe (would shadow the shim)"); }
                catch (Exception ex) { EngineLog.Write($"[PythonToolsInstaller] could not remove stale {script}.exe: {ex.Message}"); }
            }

            var cmd = Path.Combine(_layout.BinDir, $"{script}.cmd");
            File.WriteAllText(cmd, BuildWindowsShimBody(script));

            // ALSO write a bare-name (no extension) shell shim. CMD and PowerShell resolve the .cmd via
            // PATHEXT, but Git Bash does NOT - so an agent that drives Git Bash and runs a cc-* tool by
            // bare name ("cc-devthrottle") otherwise gets "command not found". Git Bash runs this extensionless
            // file via its shebang and execs the same venv exe. CMD/PowerShell ignore it (no PATHEXT
            // match), so there is no conflict. This is what lets agents call each other from bash.
            var bare = Path.Combine(_layout.BinDir, script);
            File.WriteAllText(bare, BuildWindowsBashShimBody(script));
        }
    }

    /// <summary>
    /// The body of the bare-name bash shim (no extension) for Git Bash. Uses LF line endings and a
    /// shebang so msys runs it; forwards to the venv console-script exe via a path relative to bin so
    /// the install tree stays movable.
    /// </summary>
    internal static string BuildWindowsBashShimBody(string script) =>
        "#!/bin/sh\n"
        + $"# bash-runnable bare-name shim for '{script}' (Git Bash does not resolve the .cmd via PATHEXT).\n"
        + $"exec \"$(dirname \"$0\")/../pyenv/Scripts/{script}.exe\" \"$@\"\n";

    /// <summary>
    /// The body of a Windows tool shim. It forwards to the venv console script, but FIRST checks the target
    /// exe exists. If a half-install ever slips through (so the target is missing), the shim prints a clear,
    /// actionable repair message and exits non-zero - instead of cmd.exe's raw "is not recognized". This is
    /// the defense-in-depth user-facing fix for issues #445 / #452.
    /// </summary>
    internal static string BuildWindowsShimBody(string script) =>
        "@echo off\r\n"
        + $"if not exist \"%~dp0..\\pyenv\\Scripts\\{script}.exe\" (\r\n"
        + $"  echo cc-* tools are not fully installed - run the repair: Home ^> Fix it 1^>^&2\r\n"
        + "  exit /b 1\r\n"
        + ")\r\n"
        + $"\"%~dp0..\\pyenv\\Scripts\\{script}.exe\" %*\r\n";

    /// <summary>
    /// On macOS each shim is a symlink in ~/.local/bin pointing at the venv's console script. The
    /// Director .app launcher already prepends ~/.local/bin to PATH, and InstallFinalizer ensures it
    /// is on the user's shell PATH too. Replaces any existing entry of the same name (migration).
    /// </summary>
    private void WriteUnixShims(IReadOnlyList<string> scripts)
    {
        Directory.CreateDirectory(_layout.MacUserBinDir);
        foreach (var script in scripts)
        {
            var link = Path.Combine(_layout.MacUserBinDir, script);
            var target = Path.Combine(_layout.PyenvBinDir, script);
            try
            {
                if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[PythonToolsInstaller] could not link {script}: {ex.Message}");
            }
        }
    }

    /// <summary>Extract an archive: ZipFile on Windows; tar on macOS/Unix (preserves +x bits and symlinks).</summary>
    private static (bool ok, string output) Extract(string archive, string destDir)
    {
        Directory.CreateDirectory(destDir);
        if (OperatingSystem.IsWindows())
        {
            try { ZipFile.ExtractToDirectory(archive, destDir, overwriteFiles: true); return (true, ""); }
            catch (Exception ex) { return (false, ex.Message); }
        }
        var (exit, output) = ProcessRunner.Run("/usr/bin/tar", $"-xzf \"{archive}\" -C \"{destDir}\"");
        return (exit == 0, output);
    }

    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Replace directory <paramref name="live"/> with <paramref name="staged"/> using whole-directory
    /// renames, so the swap is atomic and can never half-populate the live tree. The live dir is first
    /// renamed aside; Windows refuses to rename a directory that has an open handle inside it, so if a
    /// running Director is holding a Python file open this throws and we abort with the live tree STILL
    /// INTACT - rather than the partial recursive delete that corrupted the runtime in issue #994. On
    /// success the aside copy is removed best-effort; on a mid-swap failure it is rolled back into place.
    /// </summary>
    internal static bool SwapDir(string staged, string live, out string error)
    {
        error = "";
        string? aside = null;
        try
        {
            if (Directory.Exists(live))
            {
                aside = live + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(live, aside);
            }
            Directory.Move(staged, live);
            if (aside is not null) TryDeleteDir(aside);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (aside is not null && Directory.Exists(aside) && !Directory.Exists(live))
                    Directory.Move(aside, live);
            }
            catch { /* best-effort rollback; the live tree may already be restored */ }
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort temp cleanup */ }
    }

    private static void TryDeleteDir(string? dir)
    {
        if (dir is null) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] : s;

    private static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>Locate the venv's site-packages dir for progress polling. Windows: pyenv\Lib\site-packages.
    /// Unix: pyenv\lib\python&lt;X.Y&gt;\site-packages (python version is bundle-determined, so we discover it).</summary>
    private static string ResolveSitePackagesDir(string pyenvDir)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(pyenvDir, "Lib", "site-packages");
        var libDir = Path.Combine(pyenvDir, "lib");
        if (Directory.Exists(libDir))
        {
            var pyDir = Directory.GetDirectories(libDir, "python*").FirstOrDefault();
            if (pyDir is not null) return Path.Combine(pyDir, "site-packages");
        }
        return Path.Combine(libDir, "site-packages");
    }

    /// <summary>Pull the distribution name out of a pip "Processing /path/scipy-1.14.0-cp312-...-win_amd64.whl" line.</summary>
    private static string ExtractWheelPackageName(string processingLine)
    {
        const string prefix = "Processing ";
        if (!processingLine.StartsWith(prefix, StringComparison.Ordinal)) return processingLine;
        var path = processingLine[prefix.Length..].Trim();
        var filename = Path.GetFileName(path);
        if (filename.EndsWith(".whl", StringComparison.Ordinal)) filename = filename[..^4];
        var dash = filename.IndexOf('-');
        return dash > 0 ? filename[..dash] : filename;
    }

    private static PythonToolsResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[PythonToolsInstaller] FAILED: {message}");
        return new PythonToolsResult(false, message, steps, 0, null);
    }
}

/// <summary>
/// A machine-local, cross-process lock serializing installs against the shared python\ and pyenv\ trees.
/// Several Directors plus the installer target the same folders; two concurrent resets/extracts of one
/// tree is the race that corrupted the runtime (issue #994). Session-scoped (a plain, unqualified Mutex
/// name), which covers the processes that actually collide: the update wizard, the Gateway install CLI it
/// shells, and the user's running Directors - all in one login session.
/// </summary>
internal sealed class SharedInstallLock : IDisposable
{
    private const string MutexPrefix = "cc-director-python-tools-install";
    private readonly Mutex? _mutex;

    private SharedInstallLock(Mutex? mutex) => _mutex = mutex;

    /// <summary>
    /// The mutex name for a specific install tree. THE SCOPE IS THE TREE, NOT THE MACHINE.
    ///
    /// This used to be one fixed name with nothing appended, which made it a single machine-wide lock
    /// that every caller on the box contended for. That is wrong in two ways. In production it made two
    /// Directors installing into DIFFERENT roots block each other for no reason - the race being guarded
    /// (issue #994) is two concurrent rebuilds of ONE tree, and installs into separate roots cannot
    /// collide. In tests it was worse than useless: every test that installs takes the same global lock,
    /// so a suite running its tests in parallel had them queueing behind one another and timing out
    /// against a holder in an unrelated test. That is shared mutable state across tests, and it produced
    /// a different failure on each run of the 453-test assembly.
    ///
    /// Keying on the normalised root makes the lock mean what its comment always claimed: one install
    /// into one tree at a time. Tests use their own temporary roots and therefore stop contending
    /// altogether, without any test-only switch in production code.
    ///
    /// The path is hashed rather than appended: a mutex name is length-limited and may not contain a
    /// directory separator, and a raw path would breach both.
    /// </summary>
    internal static string NameFor(string installRoot)
    {
        var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot))
            .ToLowerInvariant();
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised)))[..16];
        return $"{MutexPrefix}-{hash}";
    }

    /// <summary>
    /// Block until the lock for <paramref name="installRoot"/> is free or <paramref name="timeout"/>
    /// elapses. Throws <see cref="TimeoutException"/> if another install into THE SAME TREE holds it for
    /// the whole bound. An abandoned mutex (a previous holder that crashed without releasing) is treated
    /// as acquired, so one crashed install never wedges every future one.
    /// </summary>
    public static SharedInstallLock Acquire(TimeSpan timeout, string installRoot)
    {
        var mutex = new Mutex(initiallyOwned: false, NameFor(installRoot));
        bool owned;
        try { owned = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            mutex.Dispose();
            throw new TimeoutException($"waited {timeout.TotalMinutes:F0} min for the shared Python install lock");
        }
        return new SharedInstallLock(mutex);
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch { /* not owned / already released */ }
        _mutex.Dispose();
    }
}

/// <summary>The parsed tools-manifest.json shipped inside the tools bundle.</summary>
internal sealed record ToolsBundleManifest(string BundleVersion, IReadOnlyList<string> Dists, IReadOnlyList<string> Scripts)
{
    public static ToolsBundleManifest Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var version = root.TryGetProperty("bundleVersion", out var v) && v.GetString() is { } bv
            ? bv
            : throw new FormatException("tools-manifest.json has no 'bundleVersion'.");

        var dists = new List<string>();
        var scripts = new List<string>();
        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsEl.EnumerateArray())
            {
                if (t.TryGetProperty("dist", out var d) && d.GetString() is { } dist)
                    dists.Add(dist);
                if (t.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Array)
                    foreach (var sc in s.EnumerateArray())
                        if (sc.GetString() is { } script) scripts.Add(script);
            }
        }
        if (dists.Count == 0) throw new FormatException("tools-manifest.json lists no tools.");
        return new ToolsBundleManifest(version, dists, scripts);
    }
}
