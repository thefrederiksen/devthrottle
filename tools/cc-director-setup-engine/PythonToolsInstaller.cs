using System.IO.Compression;
using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Python tools bundle install, with the steps taken (for logs / UI).</summary>
public sealed record PythonToolsResult(
    bool Success, string Message, IReadOnlyList<string> Steps, int ToolCount, string? BundleVersion);

/// <summary>
/// Installs the Python cc-* tools as ONE shared venv, replacing the per-tool PyInstaller exes.
/// Consumes the two release assets built by scripts/build-python-bundle.(ps1|sh):
///   Windows: cc-python-win-x64.zip + cc-tools-pyenv-win-x64.zip
///   macOS:   cc-python-macos-arm64.tar.gz + cc-tools-pyenv-macos-arm64.tar.gz
/// Each carries a relocatable CPython and a de-duped wheelhouse + requirements.lock + tools-manifest.json.
///
/// Flow: download + SHA-verify both assets, extract the python, create a venv with it, pip-install
/// every tool OFFLINE (--no-index --find-links wheelhouse), then create tool shims (bin\&lt;script&gt;.cmd
/// on Windows; ~/.local/bin/&lt;script&gt; symlinks on macOS). Per-user, no admin. Idempotent: python\ and
/// pyenv\ are rebuilt each run.
/// </summary>
public sealed class PythonToolsInstaller
{
    /// <summary>The bundled-CPython asset for the current OS.</summary>
    public static string PythonAsset =>
        OperatingSystem.IsWindows() ? "cc-python-win-x64.zip" : "cc-python-macos-arm64.tar.gz";

    /// <summary>The tools wheelhouse asset for the current OS.</summary>
    public static string ToolsAsset =>
        OperatingSystem.IsWindows() ? "cc-tools-pyenv-win-x64.zip" : "cc-tools-pyenv-macos-arm64.tar.gz";

    /// <summary>The component id the bundle's version is tracked under in installed.json.</summary>
    public const string ComponentId = "python-tools";

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

            // 2. Extract the python (into the canonical location) and the tools bundle (into temp).
            // The mac archives are .tar.gz extracted with tar so the standalone python's +x bits and
            // symlinks survive (the bundle script lays the python out flat: PythonDir/bin/python3).
            Step("extracting bundled Python");
            percent?.Report(20);
            ResetDir(_layout.PythonDir);
            var (pyOk, pyExtractOut) = Extract(pyZip, _layout.PythonDir);
            if (!pyOk) return Fail(steps, $"extracting {PythonAsset} failed: {Trim(pyExtractOut)}");

            bundleDir = Path.Combine(Path.GetTempPath(), $"cc-pytools-{Guid.NewGuid():N}");
            var (tOk, tExtractOut) = Extract(toolsZip, bundleDir);
            if (!tOk) return Fail(steps, $"extracting {ToolsAsset} failed: {Trim(tExtractOut)}");
            percent?.Report(25);

            var manifestPath = Path.Combine(bundleDir, "tools-manifest.json");
            var wheelhouse = Path.Combine(bundleDir, "wheelhouse");
            if (!File.Exists(manifestPath)) return Fail(steps, "bundle is missing tools-manifest.json.");
            if (!Directory.Exists(wheelhouse)) return Fail(steps, "bundle is missing the wheelhouse.");

            var manifest = ToolsBundleManifest.Load(manifestPath);
            var pythonExe = OperatingSystem.IsWindows()
                ? Path.Combine(_layout.PythonDir, "python.exe")
                : Path.Combine(_layout.PythonDir, "bin", "python3");
            if (!File.Exists(pythonExe)) return Fail(steps, $"bundled python not found at {pythonExe}.");

            // 2b. Early-out: if the on-disk bundle is already at this version AND the venv looks healthy,
            // skip the 5-8 minute venv-reset + offline-pip-install rebuild. Saves most update runs from
            // the long stall when only the Director version (not the tools bundle) has changed. We can't
            // check this before download because release-manifest.json does not yet expose the bundle
            // version — the version lives inside tools-manifest.json, which only exists post-extract.
            var installedAtStart = InstalledManifest.Load(_layout);
            var installedBundle = installedAtStart.Get(ComponentId);
            var venvPython = Path.Combine(_layout.PyenvBinDir, OperatingSystem.IsWindows() ? "python.exe" : "python3");
            // Trust installed.json only when the venv is actually HEALTHY: python present AND every tool's
            // console script on disk. A version match alone is not enough - a venv whose site-packages was
            // stripped or half-built (python.exe present, packages gone) is the exact "half-installed" state
            // that left tools broken in the field (wrappers in bin\ pointing at missing pyenv\Scripts exes).
            // Rejecting it here makes simply re-running the installer repair the venv.
            var venvHealthy = File.Exists(venvPython) && VenvHasAllTools(manifest.Scripts);
            if (installedBundle == manifest.BundleVersion && venvHealthy)
            {
                Step($"Python tools bundle {manifest.BundleVersion} already installed; skipping rebuild");
                percent?.Report(100);
                return new PythonToolsResult(true,
                    $"Python tools bundle {manifest.BundleVersion} already installed.",
                    steps, manifest.Dists.Count, manifest.BundleVersion);
            }
            if (installedBundle == manifest.BundleVersion && !venvHealthy)
                Step($"installed.json claims bundle {manifest.BundleVersion}, but the venv is missing tool scripts; rebuilding to repair");

            // 3. Create the shared venv from the bundled python (on-target, so console-script paths are correct).
            Step("creating the shared Python venv");
            ResetDir(_layout.PyenvDir);
            var (venvExit, venvOut) = ProcessRunner.Run(pythonExe, $"-m venv \"{_layout.PyenvDir}\"");
            if (venvExit != 0) return Fail(steps, $"venv creation failed ({venvExit}): {Trim(venvOut)}");

            // 4. Install every tool OFFLINE from the wheelhouse. Percent bands across the whole
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

            var (pipExit, pipOut) = ProcessRunner.Run(venvPython, pipArgs, OnPipLine);
            pollCts.Cancel();
            try { await pollTask; } catch { /* poller cancellation */ }

            if (pipExit != 0) return Fail(steps, $"offline pip install failed ({pipExit}): {Trim(pipOut)}");
            percent?.Report(100);
            progress?.Report($"Installed {wheelCount} packages");

            // 5. Write bin\<script>.cmd shims that forward to the venv's console scripts.
            Step($"writing {manifest.Scripts.Count} tool shims to bin");
            WriteShims(manifest.Scripts);

            // 6. Record the bundle version + clean up the temp bundle.
            var im = InstalledManifest.Load(_layout);
            im.Set(ComponentId, manifest.BundleVersion);
            im.Save(_layout);

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
    private string ConsoleScriptPath(string script) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(_layout.PyenvScriptsDir, $"{script}.exe")
            : Path.Combine(_layout.PyenvBinDir, script);

    /// <summary>
    /// True only when every tool console script the bundle promises is actually on disk in the venv.
    /// This is the health probe that distinguishes a real install from a half-installed venv (empty or
    /// stripped site-packages). An empty script list returns false so a manifest with nothing to verify
    /// forces a rebuild rather than a false "already installed".
    /// </summary>
    private bool VenvHasAllTools(IReadOnlyList<string> scripts)
    {
        if (scripts.Count == 0) return false;
        foreach (var script in scripts)
            if (!File.Exists(ConsoleScriptPath(script))) return false;
        return true;
    }

    /// <summary>Create the tool shims: bin\&lt;script&gt;.cmd on Windows, ~/.local/bin symlinks on macOS.</summary>
    private void WriteShims(IReadOnlyList<string> scripts)
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
            var body = "@echo off\r\n"
                     + $"\"%~dp0..\\pyenv\\Scripts\\{script}.exe\" %*\r\n";
            File.WriteAllText(cmd, body);
        }
    }

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
