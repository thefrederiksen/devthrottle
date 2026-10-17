using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Python tools bundle install, with the steps taken (for logs / UI).</summary>
/// <param name="HeldBecauseInUse">
/// True when the install did not run because a cc-* tool was using the shared Python or venv, and nothing was
/// changed (issue #3987). <c>Success</c> is false, but this is a hold to retry soon, not a failure.
/// </param>
public sealed record PythonToolsResult(
    bool Success, string Message, IReadOnlyList<string> Steps, int ToolCount, string? BundleVersion,
    bool HeldBecauseInUse = false);

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
    private readonly RunProcess _run;

    public PythonToolsInstaller(InstallLayout layout)
        : this(layout, ProcessRunner.Run)
    {
    }

    /// <summary>
    /// Run the interpreter, the venv create and pip through <paramref name="run"/> instead of starting real
    /// processes, so a test can fix what each step answers.
    /// </summary>
    internal PythonToolsInstaller(InstallLayout layout, RunProcess run)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

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
                && PythonRuntimeProbe.CanImportStdlib(pythonExe, _run);
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

            // 2c. A tools update must never leave the machine with FEWER working tools than it had (issue
            //     #3987). Any cc-* tool that is running - a `cc-secrets ask` pop-up waiting for the owner, a
            //     `cc-secrets run --timeout 0` job, a long cc-outlook call - holds files open inside the shared
            //     python\ and pyenv\ trees, often for hours. Replacing them under it used to delete the shims
            //     and most of the venv and then stop on the locked file, leaving every session on the machine
            //     without cc-devthrottle. So BEFORE anything destructive, check the trees can actually be
            //     replaced. If they cannot, change nothing and report a HOLD: the caller retries soon, and the
            //     tools the machine already has keep working in the meantime.
            if (!ToolTreesAreFree(out var inUseReason))
                return Held(steps, $"the cc-* tools are in use, so the update to bundle {manifest.BundleVersion} " +
                                   $"is held and nothing was changed: {inUseReason}");

            // 3. Build the WHOLE new runtime beside the live one - the base Python AND the venv - and only then
            //    swap it in. The live tools keep working through every slow step (the extract, the venv create
            //    and the pip install, which take a minute or more), because none of those steps touches them.
            //
            //    This used to set the live venv aside and build the new one at its real path, because a venv
            //    bakes its own absolute path into every console script. That left the machine with NO
            //    cc-devthrottle for the whole pip install: on 16 October 2026 a license switch arrived 41 seconds
            //    into such a window and failed with "cc-devthrottle is not installed in this machine's Python
            //    tool environment" (missions/license-switching/phase-6/track-b/tool-health.md). The baked-in
            //    paths are now rewritten to the final location BEFORE the swap (VenvRelocation), so the venv can
            //    be built in a staging folder like the Python is.
            //
            //    The extract happens on the staging copy, so a partial extract never reaches the live tree.
            Step("staging bundled Python");
            var suffix = Guid.NewGuid().ToString("N");
            var staged = _layout.PythonDir + ".staging-" + suffix;
            var stagedVenv = _layout.PyenvDir + ".staging-" + suffix;
            var pythonBackup = _layout.PythonDir + ".old-" + suffix;
            var venvBackup = _layout.PyenvDir + ".prev-" + suffix;
            var stagedVenvPython = Path.Combine(VenvRelocation.ScriptsDirOf(stagedVenv), OperatingSystem.IsWindows() ? "python.exe" : "python3");
            // The scripts of the venv being replaced, so a rollback can put its shims back (issue #3150).
            var restoreScripts = PythonToolsState.LoadScripts(_layout);
            var pythonSetAside = false;
            var venvSetAside = false;
            // Said on every failure BEFORE the swap: nothing installed was touched, so nothing needs putting back.
            const string untouched = " The installed tools were not touched, so every tool that worked before this update still works.";

            // From the moment anything live is moved, every way out of this method other than success must
            // put the previous Python, the previous venv AND their shims back - so a failed update leaves the
            // machine exactly as it was, never with no tools (issues #3150, #3344, #3987).
            //
            // Each flag is cleared the moment its tree is back, so a rollback that is somehow entered twice never
            // reports a restore that already happened as a failure. Nothing in here may throw: it runs from the
            // catch below, and an exception out of it would skip the rest of the restore.
            PythonToolsResult FailAndRollBack(string message, bool held = false)
            {
                var problems = new List<string>();
                if (venvSetAside)
                {
                    if (RestoreDirOver(venvBackup, _layout.PyenvDir, out var venvRestoreError)) venvSetAside = false;
                    else problems.Add($"the previous tools environment is at {venvBackup} ({venvRestoreError})");
                }
                if (pythonSetAside)
                {
                    if (RestoreDirOver(pythonBackup, _layout.PythonDir, out var pyRestoreError)) pythonSetAside = false;
                    else problems.Add($"the previous base Python is at {pythonBackup} ({pyRestoreError})");
                }
                try
                {
                    // Shims the new bundle wrote for scripts the restored venv does not have would point at nothing.
                    RemoveManagedShims(manifest.Scripts);
                    RestoreShims(restoreScripts);
                    // The version and script list go back too: a failure after they were recorded must not leave
                    // the previous venv labelled as the new bundle.
                    var record = InstalledManifest.Load(_layout);
                    if (installedBundle is null) record.Remove(ComponentId);
                    else record.Set(ComponentId, installedBundle);
                    record.Save(_layout);
                    if (restoreScripts.Count > 0) PythonToolsState.SaveScripts(_layout, restoreScripts);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"the shims or the install record could not be rewritten ({ex.Message})");
                }
                if (problems.Count == 0)
                    return held
                        ? Held(steps, $"{message} Nothing was left changed: the previous tools were put back.")
                        : Fail(steps, $"{message} The previous tools were put back, so every tool that worked " +
                                      "before this update still works.");
                return Fail(steps, $"{message} The previous tools could NOT all be put back: {string.Join("; ", problems)}. " +
                                   "Run `cc-devthrottle setup repair` once nothing is using the tools.");
            }

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
                if (!PythonRuntimeProbe.CanImportStdlib(stagedPythonExe, _run))
                    return Fail(steps, "staged Python is incomplete - it cannot import its standard library (a partial extract). The existing Python was left untouched.");

                // 4. Create the new venv from the STAGED Python, in its own staging folder beside the live venv.
                Step("creating the new Python venv beside the current one");
                ResetDir(stagedVenv);
                var (venvExit, venvOut) = _run(stagedPythonExe, $"-m venv \"{stagedVenv}\"", null, VenvCreateTimeout);
                if (venvExit != 0) return Fail(steps, $"venv creation failed ({venvExit}): {Trim(venvOut)}." + untouched);
                // Guard: even on a zero exit, the venv must actually have produced its python. A venv whose
                // interpreter is missing means the create silently did nothing - fail loud now rather than throw
                // a Win32 "file not found" when the pip step tries to run the missing interpreter.
                if (!File.Exists(stagedVenvPython))
                    return Fail(steps, $"venv creation reported success but produced no interpreter at {stagedVenvPython}." + untouched);

                // 5. Install every tool OFFLINE from the wheelhouse, into the staged venv. Percent bands across the
                //    whole bundle install: download 0-20 (byte-level, above), extract 20-25, then two-phase
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
                var sitePackages = ResolveSitePackagesDir(stagedVenv);

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

                var (pipExit, pipOut) = _run(stagedVenvPython, pipArgs, OnPipLine, PipInstallTimeout);
                pollCts.Cancel();
                try { await pollTask; } catch { /* poller cancellation */ }

                // A timeout reports the sentinel exit code; surface it as the loud, bounded failure it is so the
                // user (and the log) see "pip hung and was killed" rather than a generic non-zero exit.
                if (pipExit == ProcessRunner.TimeoutExitCode)
                    return Fail(steps, $"offline pip install timed out after {PipInstallTimeout.TotalMinutes:F0} minutes and was killed: {Trim(pipOut)}." + untouched);
                if (pipExit != 0) return Fail(steps, $"offline pip install failed ({pipExit}): {Trim(pipOut)}." + untouched);
                percent?.Report(100);
                progress?.Report($"Installed {wheelCount} packages");

                // 6. Verify the staged venv is healthy BEFORE anything live moves. Every tool's console script must
                // be on disk. If pip exited 0 but a script is missing (a partial/corrupt wheelhouse), we must NOT
                // swap it in nor stamp a version that would suppress a future repair - we fail loud.
                var stagedScripts = VenvRelocation.ScriptsDirOf(stagedVenv);
                var missing = manifest.Scripts.Where(s => !File.Exists(ConsoleScriptIn(stagedScripts, s))).ToList();
                if (manifest.Scripts.Count == 0 || missing.Count > 0)
                    return Fail(steps, $"venv is incomplete after pip install: missing console scripts [{string.Join(", ", missing)}]. Nothing was swapped in, version not recorded." + untouched);

                // 7. Point the staged venv at where it is about to live: its console scripts, its pyvenv.cfg and
                // (off Windows) its interpreter links name the staging folders until this rewrites them.
                Step("pointing the new tools environment at its final location");
                if (!VenvRelocation.Relocate(stagedVenv, _layout.PyenvDir, staged, _layout.PythonDir,
                        manifest.Scripts.Select(s => ConsoleScriptIn(stagedScripts, s)).ToList(), out var relocateError))
                    return Fail(steps, $"the new tools environment could not be pointed at its final location: {relocateError}." + untouched);

                // 8. Swap. Every move is a whole-directory rename on one volume, so it is atomic and cannot
                //    half-move, and the four of them together take milliseconds - this, not the pip install, is
                //    now the only time the tools are absent. The shims stay where they are: they forward by a path
                //    relative to bin, so they reach the new venv the moment it is in place.
                Step("swapping in the new tools environment");
                if (!SetDirAside(_layout.PyenvDir, venvBackup, out var venvAsideError))
                {
                    // Nothing has moved: a tool started between the check above and here. Hold, exactly as if the
                    // check had caught it.
                    return Held(steps, $"a cc-* tool started using the tools environment, so the update to bundle " +
                                       $"{manifest.BundleVersion} is held and nothing was changed: {venvAsideError}");
                }
                venvSetAside = Directory.Exists(venvBackup);

                if (!SetDirAside(_layout.PythonDir, pythonBackup, out var pyAsideError))
                    return FailAndRollBack($"a cc-* tool started using the base Python, so the update to bundle " +
                                           $"{manifest.BundleVersion} is held: {pyAsideError}.", held: true);
                pythonSetAside = Directory.Exists(pythonBackup);
                Directory.Move(staged, _layout.PythonDir);
                Directory.Move(stagedVenv, _layout.PyenvDir);

                if (!File.Exists(pythonExe))
                    return FailAndRollBack($"bundled python not found at {pythonExe} after the swap.");
                if (!VenvHasAllTools(manifest.Scripts))
                    return FailAndRollBack("the new tools environment is missing console scripts after the swap.");

                // 9. Shims: take off the ones for scripts the previous bundle had and this one does not (their
                // targets just went away with the previous venv), and write one for every script this bundle has.
                Step($"writing {manifest.Scripts.Count} tool shims to bin");
                RemoveManagedShims(restoreScripts.Except(manifest.Scripts, StringComparer.Ordinal).ToList());
                WriteShims(manifest.Scripts);

                // 10. Record the bundle version ONLY now that the venv is healthy and the shims point at real
                // targets. Gating im.Set on VenvHasAllTools means a half-built venv never records a version that
                // would make the version-gated auto-update skip the machine forever (issue #577).
                var im = InstalledManifest.Load(_layout);
                im.Set(ComponentId, manifest.BundleVersion);
                im.Save(_layout);
                // Persist the script list so the auto-updater can probe venv health offline (without re-downloading
                // the bundle just to learn which scripts to expect).
                PythonToolsState.SaveScripts(_layout, manifest.Scripts);

                // 11. The new environment is installed, reachable and recorded. Only NOW are the previous ones no
                // longer needed - deleting them is the one step that cannot be undone, so it is the last one.
                if (venvSetAside) TryDeleteDir(venvBackup);
                if (pythonSetAside) TryDeleteDir(pythonBackup);
                venvSetAside = pythonSetAside = false;
                RemoveLeftoverTrees();

                Step($"Python tools bundle {manifest.BundleVersion} installed ({manifest.Dists.Count} tools)");
                return new PythonToolsResult(true,
                    $"Installed {manifest.Dists.Count} Python tools (bundle {manifest.BundleVersion}).",
                    steps, manifest.Dists.Count, manifest.BundleVersion);
            }
            catch (Exception ex) when (venvSetAside || pythonSetAside)
            {
                // A file-system error (a locked file, a full disk) after something live was moved. It must not
                // escape as a bare exception that skips the rollback - that is exactly how a refresh used to
                // leave the machine with no tools (issue #3987).
                return FailAndRollBack($"the tools update stopped on an error: {ex.Message}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The same kind of error while the new runtime was still being built beside the live one. Nothing
                // live had moved, so there is nothing to put back - but it is still a failed update, said as one.
                return Fail(steps, $"the tools update stopped on an error: {ex.Message}." + untouched);
            }
            finally
            {
                if (Directory.Exists(staged)) TryDeleteDir(staged);
                if (Directory.Exists(stagedVenv)) TryDeleteDir(stagedVenv);
            }
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
        return ConsoleScriptIn(layout.PyenvBinDir, script);
    }

    /// <summary>A tool's console script in a venv's script folder: <c>&lt;name&gt;.exe</c> on Windows, the bare name elsewhere.</summary>
    internal static string ConsoleScriptIn(string venvScriptsDir, string script) =>
        Path.Combine(venvScriptsDir, OperatingSystem.IsWindows() ? $"{script}.exe" : script);

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
    /// Command names the product does not ship, and whose bin shim an older install may still be
    /// carrying. Two groups:
    /// <list type="bullet">
    /// <item>The retired per-tool fleet commands consolidated into the single cc-devthrottle command
    /// (issue #823): cc-send, cc-ask, cc-spawn, cc-sessions, cc-whoami, cc-settings, cc-cron,
    /// cc-fleet-selftest. These were never tools and appear in no registry, so they are listed by
    /// hand.</item>
    /// <item>THE LAUNCHER OF ANY TOOL THE REGISTRY KNOWS AND THE PRODUCT DOES NOT SHIP - every name in
    /// <c>tools/registry.json</c> that is absent from <c>src/CcDirector.Core/Tools/tools-manifest.json</c>.
    /// This used to be a hand-written list that happened to contain cc-playwright and not cc-comm-queue,
    /// while both shims were sitting on the machine that prompted this work. Held to the derivation by
    /// <c>LegacyAliasShimNames_CoverEveryKnownUnshippedTool</c>, so the list cannot drift again.</item>
    /// </list>
    /// In both cases the venv console script is gone, so a leftover shim resolves to a missing target.
    /// For the fleet aliases that is exit 127; for a tool dropped from the manifest it is worse - the
    /// self-checking shim body tells a HEALTHY install that "cc-* tools are not fully installed" and
    /// sends the user to a repair that will never put the tool back. Note the ordinary
    /// <c>RemoveManagedShims</c> pass cannot clean either group: it only walks the CURRENT manifest, and
    /// these names are in no manifest. That is precisely why this explicit purge exists.
    ///
    /// A NAME ON THIS LIST IS NOT ON ITS OWN PERMISSION TO DELETE A FILE - see
    /// <see cref="IsShimTheProductWrote"/>. The list says WHICH names may be considered; the content
    /// test says which FILES were ours to write, and both have to hold. That second condition exists
    /// because this list now names tools the owner runs BY HAND: cc-docgen, cc-excel, cc-video and the
    /// rest are registry-known and unshipped, and his own launcher for one of them is a file the product
    /// did not write and could never put back.
    ///
    /// THIS LIST IS NO LONGER EQUAL TO cc-devthrottle's setup_ops.LEGACY_ALIAS_NAMES, and the sentence
    /// that used to say it was is gone rather than left to be believed. That one is a DIAGNOSTIC: doctor
    /// prints where each name resolves on the path, and it covers the nine retired fleet aliases only.
    /// Widening it to every registry-known unshipped tool would have doctor report the owner's own
    /// working cc-docgen as a retired alias, which is a false statement about his machine. The two lists
    /// answer different questions. Their overlap is NINE names, not eight: the eight retired fleet
    /// commands plus cc-playwright, which the doctor prints and this list also purges. Put another way,
    /// the doctor's whole list is contained in this one - so a name added to the doctor's list should
    /// already be here, while a name added here usually does not belong there.
    ///
    /// These names never overlap the shipping tools, so purging them can never remove a live tool's
    /// shim - guarded by a test against the shipped manifest rather than left as a promise.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyAliasShimNames = new[]
    {
        // The retired per-tool fleet commands (issue #823). These are in no registry - they were never
        // tools, they were command names folded into cc-devthrottle - so the guard test cannot derive
        // them and they are listed here by hand.
        "cc-send", "cc-ask", "cc-spawn", "cc-sessions", "cc-whoami", "cc-settings", "cc-cron",
        "cc-fleet-selftest",

        // Every tool the registry KNOWS and the product does not SHIP. Derived by hand from
        // tools/registry.json minus src/CcDirector.Core/Tools/tools-manifest.json, and held to that
        // derivation by LegacyAliasShimNames_CoverEveryKnownUnshippedTool so it cannot drift again.
        //
        // SOME OF THESE ARE DOTNET TOOLS, AND THAT IS NOT A MISTAKE. The manifest subtracted here is the
        // PYTHON bundle, so a dotnet tool is "unshipped" by this derivation whether or not users get it -
        // cc-hardware has been here on those terms for a long time, and cc-cleanup-storage joins it. It is
        // inert for them either way: IsShimTheProductWrote only ever matches a launcher carrying the
        // product's own relative pyenv\Scripts forwarding line, which a dotnet tool's launcher never has.
        // The name buys permission to LOOK at a file, never to delete one.
        "cc-brandingrecommendations", "cc-browser", "cc-cleanup-storage", "cc-click", "cc-comm-queue",
        "cc-computer", "cc-crawl4ai", "cc-docgen", "cc-excel", "cc-facebook", "cc-hardware",
        "cc-photos", "cc-playwright",
        "cc-posthog", "cc-powerpoint", "cc-reddit", "cc-scrub", "cc-transcribe", "cc-trisight",
        "cc-twitter", "cc-video", "cc-voice", "cc-websiteaudit", "cc-whisper", "cc-youtube",
        "cc-youtube-info",
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
        LegacyAliasShimNames
            .SelectMany(name => LegacyAliasShimPaths(name).Select(path => (Name: name, Path: path)))
            .Where(candidate => File.Exists(candidate.Path) && IsShimTheProductWrote(candidate.Path, candidate.Name))
            .Select(candidate => candidate.Path)
            .ToList();

    /// <summary>
    /// The launcher files on this list's names that the product did NOT write, each with the reason it
    /// is being left alone. Pure detection, no mutation - so the purge can say what it kept as well as
    /// what it removed, and a person can see that a hand-made launcher survived rather than having to
    /// infer it from a file that is still there.
    /// </summary>
    internal IReadOnlyList<(string Path, string Reason)> FindHandWrittenLegacyAliasLaunchers() =>
        LegacyAliasShimNames
            .SelectMany(name => LegacyAliasShimPaths(name).Select(path => (Name: name, Path: path)))
            .Where(candidate => File.Exists(candidate.Path) && !IsShimTheProductWrote(candidate.Path, candidate.Name))
            .Select(candidate => (candidate.Path,
                Reason: $"it does not carry the product's own relative forwarding line for {candidate.Name}, "
                        + "so the product did not write it"))
            .ToList();

    /// <summary>
    /// Did THIS product write THIS file as a shim for THIS name?
    ///
    /// WHY NAME ALONE IS NOT ENOUGH, AND WHY THIS LEANS TO KEEPING. The name list above now covers every
    /// tool the registry knows and the product does not ship, and several of those are tools the owner
    /// runs by hand. On the machine that prompted this work, <c>cc-playwright.cmd</c> and
    /// <c>cc-comm-queue.cmd</c> both carry the product's own forwarding line, and <c>cc-linkedin.cmd</c>
    /// is <c>py -3.11 "D:\ReposFred\cc-linkedin\cc_linkedin.py" %*</c> - a launcher a person wrote,
    /// for a tool that still works, which no install could ever put back.
    ///
    /// Name alone is a rule that can only ever delete MORE than it should. Name-and-content can only ever
    /// delete LESS. Given that a false delete here is unrecoverable and a false keep is a stale file, the
    /// second is the rule to have.
    ///
    /// The positive test is the product's OWN RELATIVE forwarding line for that exact name, in whichever
    /// of the two forms the product writes: <c>%~dp0..\pyenv\Scripts\&lt;name&gt;.exe</c> in the
    /// <c>.cmd</c> (see <see cref="BuildWindowsShimBody"/>) and
    /// <c>$(dirname "$0")/../pyenv/Scripts/&lt;name&gt;.exe</c> in the bare-name bash shim (see
    /// <see cref="BuildWindowsBashShimBody"/>). So this is an allow-list of two shapes and not a list of
    /// things to skip. Anything that cannot be read as text, anything binary, and anything whose body
    /// says something else is KEPT.
    ///
    /// THE RELATIVE COMPOSITION IS THE WHOLE TEST, NOT DECORATION. A hand-written launcher that points
    /// STRAIGHT at the venv executable by absolute path - <c>"C:\...\cc-director\pyenv\Scripts\cc-docgen.exe" %*</c>,
    /// the most natural way for a person to write one - names the same executable and would pass a test
    /// that only looked for <c>pyenv\Scripts\&lt;name&gt;.exe</c>. It is a file the product did not write
    /// and could never put back. The <c>%~dp0..\</c> and the <c>$(dirname "$0")/../</c> are what tell the
    /// product's own relative composition apart from that wrapper, and both forms are required because
    /// the two shims compose the same relative path differently - taking only the <c>.cmd</c> wording
    /// would make the purge inert on every bare-name bash shim, which is the form both of the launchers
    /// this purge exists to remove are written in on the machine that prompted the work.
    ///
    /// ON macOS AND LINUX THIS PURGE IS STRUCTURALLY INERT, AND THAT IS SAID HERE RATHER THAN DISCOVERED.
    /// <see cref="LegacyAliasShimPaths"/> offers exactly one candidate per name off Windows: the
    /// <c>~/.local/bin/&lt;name&gt;</c> SYMLINK that <see cref="WriteUnixShims"/> writes. Reading a
    /// symlink to a venv console script reads the console script - a binary - and a dangling symlink
    /// cannot be read at all, so neither can ever contain a forwarding line and this method answers
    /// false for every one of them. The widened purge therefore removes NOTHING on those platforms, and
    /// <see cref="ToolReconciler"/> reports no orphaned-shim drift there either. That is disk rather than
    /// a safety problem - it leans to keeping - but it is inert by construction and not merely unwatched.
    /// Giving the symlink branch its own positive test (a link whose target resolves inside this
    /// install's own pyenv) is a real change to what gets deleted on a platform nobody here can run, and
    /// it belongs in its own work item with someone on a Mac, not in this one.
    ///
    /// IT NARROWS THE OLD PURGE AND THAT IS DELIBERATE. A <c>bin\&lt;name&gt;.exe</c> left by a
    /// PyInstaller-era install carries no such line and is no longer deleted. That file is dead weight;
    /// the alternative reading of the same bytes is a launcher somebody built on purpose, and the two
    /// cannot be told apart from the outside. Leaving it costs disk. Removing the wrong one costs a tool
    /// the owner uses and cannot get back from us.
    /// </summary>
    internal static bool IsShimTheProductWrote(string path, string name)
    {
        string body;
        try
        {
            body = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            // Unreadable is not permission. A file this process cannot read is a file it cannot prove
            // anything about, and the whole boundary is that only a positive proof deletes.
            EngineLog.Write($"[PythonToolsInstaller] keeping {path} FAILED: it could not be read ({ex.Message}), "
                            + "so it cannot be shown to be a shim this product wrote");
            return false;
        }

        return body.Contains($@"%~dp0..\pyenv\Scripts\{name}.exe", StringComparison.OrdinalIgnoreCase)
            || body.Contains($"$(dirname \"$0\")/../pyenv/Scripts/{name}.exe", StringComparison.OrdinalIgnoreCase);
    }

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
        // Said out loud rather than inferred from a file that is still there. A launcher that survived
        // because the product did not write it looks, from the outside, exactly like one the purge never
        // looked at - and those are different machines.
        foreach (var (kept, reason) in FindHandWrittenLegacyAliasLaunchers())
            EngineLog.Write($"[PythonToolsInstaller] KEPT {kept}: {reason}");

        foreach (var path in FindOrphanedLegacyAliasShims())
        {
            try
            {
                File.Delete(path);
                EngineLog.Write($"[PythonToolsInstaller] removed orphaned legacy alias shim {path}");
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[PythonToolsInstaller] could not remove legacy alias shim {path} FAILED: {ex.Message}");
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
                catch (Exception ex) { EngineLog.Write($"[PythonToolsInstaller] could not remove managed shim {path} FAILED: {ex.Message}"); }
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
                catch (Exception ex) { EngineLog.Write($"[PythonToolsInstaller] could not remove stale {script}.exe FAILED: {ex.Message}"); }
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
                EngineLog.Write($"[PythonToolsInstaller] could not link {script} FAILED: {ex.Message}");
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
    /// Move <paramref name="live"/> out of the way to <paramref name="backup"/>, keeping every byte of it, so
    /// a rebuild that fails can put it back (issues #3150, #3987). A whole-directory rename on one volume:
    /// atomic, and it cannot half-move. Windows refuses to rename a directory with an open handle inside it,
    /// so a running tool makes this return false with the live tree STILL INTACT - never the partial recursive
    /// delete that stripped the runtime in issue #994 and the venv in issue #3987. A live directory that does
    /// not exist is not an error (there is nothing to keep); the caller tells the two apart by whether the
    /// backup now exists.
    /// </summary>
    internal static bool SetDirAside(string live, string backup, out string error)
    {
        error = "";
        if (!Directory.Exists(live)) return true;
        try
        {
            Directory.Move(live, backup);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Put a directory set aside by <see cref="SetDirAside"/> back at <paramref name="live"/>. Whatever the
    /// failed rebuild left at <paramref name="live"/> is RENAMED out of the way first rather than deleted - a
    /// half-built tree can hold a locked file too, and a recursive delete that stops partway is the defect this
    /// exists to remove. Returns false, with the reason, when there is nothing to restore or a move is refused.
    /// </summary>
    internal static bool RestoreDirOver(string backup, string live, out string error)
    {
        error = "";
        if (!Directory.Exists(backup))
        {
            error = $"there is nothing to restore at {backup}";
            return false;
        }
        string? failed = null;
        try
        {
            if (Directory.Exists(live))
            {
                failed = live + ".failed-" + Guid.NewGuid().ToString("N");
                Directory.Move(live, failed);
            }
            Directory.Move(backup, live);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        if (failed is not null) TryDeleteDir(failed);
        EngineLog.Write($"[PythonToolsInstaller] restored {live} from {backup}");
        return true;
    }

    /// <summary>
    /// True when the shared base Python and the tools venv can be replaced right now (issue #3987). The check is
    /// the operation itself: each tree is renamed aside and straight back. Windows refuses that rename while any
    /// process has a file open inside the tree, which is exactly the condition under which a replacement would
    /// stop halfway. On refusal, <paramref name="reason"/> names the processes running from the trees so the log
    /// says WHO is holding the update. (On macOS and Linux a rename is never refused by an open file, and an
    /// open file does not block a replacement either, so the check passes there.)
    /// </summary>
    internal bool ToolTreesAreFree(out string reason)
    {
        reason = "";
        if (!RecoverStrandedProbes(out var recoverError))
        {
            reason = $"a tools tree left under its probe name could not be put back yet ({recoverError})";
            return false;
        }
        foreach (var dir in new[] { _layout.PythonDir, _layout.PyenvDir })
        {
            if (!Directory.Exists(dir)) continue;
            var probe = dir + ".probe-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(dir, probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"{dir} cannot be replaced ({ex.Message}); {DescribeProcessesUsing(_layout.PythonDir, _layout.PyenvDir)}";
                // not-an-error: a tool in use is the expected reason to hold the update; the caller logs the hold.
                EngineLog.Write($"[PythonToolsInstaller] ToolTreesAreFree: {reason}");
                return false;
            }
            MoveBack(probe, dir);
        }
        return true;
    }

    /// <summary>
    /// Put back a live tree that an earlier check left under its probe name, because the rename back was refused
    /// for longer than <see cref="MoveBack"/> retries (a long antivirus scan) or the process stopped in between.
    /// Without this the machine would have no tools, the next cycle would see no live tree and reinstall from
    /// scratch, and the probe copy would sit on disk forever. Acts ONLY when the live tree is missing and exactly
    /// one probe exists - anything else is not a state this code produces, and is left for a person to look at.
    /// </summary>
    private bool RecoverStrandedProbes(out string error)
    {
        error = "";
        foreach (var live in new[] { _layout.PythonDir, _layout.PyenvDir })
        {
            var parent = Path.GetDirectoryName(live)!;
            if (Directory.Exists(live) || !Directory.Exists(parent)) continue;
            var probes = Directory.EnumerateDirectories(parent, Path.GetFileName(live) + ".probe-*").ToList();
            if (probes.Count != 1)
            {
                if (probes.Count > 1)
                    EngineLog.Write($"[PythonToolsInstaller] RecoverStrandedProbes FAILED: {live} is missing and {probes.Count} probe directories exist; not guessing which one is the tools tree");
                continue;
            }
            try
            {
                Directory.Move(probes[0], live);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                EngineLog.Write($"[PythonToolsInstaller] RecoverStrandedProbes FAILED: could not move {probes[0]} back to {live}: {ex.Message}");
                return false;
            }
            EngineLog.Write($"[PythonToolsInstaller] RecoverStrandedProbes: put the stranded tools tree back from {probes[0]} to {live}");
        }
        return true;
    }

    /// <summary>
    /// Undo a probe rename. Nothing can be holding a file inside the probe directory - no shim or tool path
    /// leads into it - so this is retried against a transient refusal (an antivirus scan of a freshly renamed
    /// tree can take seconds) and then fails LOUDLY. If it does give up, the next check puts the tree back
    /// (<see cref="RecoverStrandedProbes"/>).
    /// </summary>
    private static void MoveBack(string probe, string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(probe, dir);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 50)
            {
                // not-an-error: a transient refusal that is retried; the last attempt throws and is reported.
                EngineLog.Write($"[PythonToolsInstaller] MoveBack: attempt {attempt} to rename {probe} back to {dir} refused ({ex.Message}); retrying");
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// "held by: pid 61412 cc-secrets (...\pyenv\Scripts\cc-secrets.exe), ..." for every process whose
    /// executable lives under one of <paramref name="dirs"/>. Diagnostic only - it decides nothing.
    /// </summary>
    private static string DescribeProcessesUsing(params string[] dirs)
    {
        var holders = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                string? path;
                // Reading another process's main module is refused for elevated and system processes. Those are
                // never cc-* tools, so a process whose path cannot be read is simply not one of the holders.
                try { path = p.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { continue; }
                if (path is null) continue;
                if (dirs.Any(d => path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    holders.Add($"pid {p.Id} {p.ProcessName} ({path})");
            }
        }
        return holders.Count == 0
            ? "no process running from the tools folders was found (a file may be open from another program)"
            : $"held by: {string.Join(", ", holders)}";
    }

    /// <summary>
    /// The sibling directories THIS installer names when it moves a tree aside, and which are obsolete once a new
    /// install has been verified healthy. Deliberately an explicit list of what may be deleted: a
    /// <c>.probe-</c> directory is NOT on it, because a probe left behind means the probe rename was never
    /// undone and that directory IS the live tree.
    /// </summary>
    internal static readonly string[] LeftoverSuffixes = { ".old-", ".staging-", ".failed-", ".prev-" };

    /// <summary>
    /// After a verified install, remove the trees earlier runs set aside and could not delete (a
    /// <c>python.old-...</c> a running tool was still holding, a <c>pyenv.prev-...</c> from a rollback).
    /// Best-effort: one that is still in use is left for a later run.
    /// </summary>
    private void RemoveLeftoverTrees()
    {
        foreach (var live in new[] { _layout.PythonDir, _layout.PyenvDir })
        {
            var parent = Path.GetDirectoryName(live)!;
            var name = Path.GetFileName(live);
            if (!Directory.Exists(parent)) continue;
            foreach (var dir in Directory.EnumerateDirectories(parent, name + ".*"))
            {
                var rest = Path.GetFileName(dir)[name.Length..];
                if (!LeftoverSuffixes.Any(s => rest.StartsWith(s, StringComparison.Ordinal))) continue;
                TryDeleteDir(dir);
                EngineLog.Write($"[PythonToolsInstaller] RemoveLeftoverTrees: {(Directory.Exists(dir) ? "could not remove (still in use)" : "removed")} {dir}");
            }
        }
    }

    /// <summary>
    /// Put the bin shims back for a venv that is on disk again after a rollback (issue #3150). Only scripts whose
    /// console script is actually present get a shim, so a rollback can never recreate the dangling shim that
    /// issue #577 exists to prevent: the restored venv belongs to the PREVIOUS bundle and need not have every
    /// script the new one promised.
    /// </summary>
    private void RestoreShims(IReadOnlyList<string> scripts)
    {
        var present = scripts.Where(s => File.Exists(ConsoleScriptPath(s))).ToList();
        if (present.Count == 0)
        {
            EngineLog.Write("[PythonToolsInstaller] RestoreShims: the restored venv has no console scripts; no shims written");
            return;
        }
        WriteShims(present);
        EngineLog.Write($"[PythonToolsInstaller] RestoreShims: rewrote {present.Count} shim(s) for the restored venv");
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

    /// <summary>
    /// The update could not run because the tools are in use, and NOTHING was changed (issue #3987). Not a
    /// failure: the tools the machine has keep working, and the caller retries soon.
    /// </summary>
    private static PythonToolsResult Held(List<string> steps, string message)
    {
        EngineLog.Write($"[PythonToolsInstaller] HELD: {message}");
        return new PythonToolsResult(false, message, steps, 0, null, HeldBecauseInUse: true);
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
