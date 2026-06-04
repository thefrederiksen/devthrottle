using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Python tools bundle install, with the steps taken (for logs / UI).</summary>
public sealed record PythonToolsResult(
    bool Success, string Message, IReadOnlyList<string> Steps, int ToolCount, string? BundleVersion);

/// <summary>
/// Installs the Python cc-* tools as ONE shared venv, replacing the per-tool PyInstaller exes.
/// Consumes the two release assets built by scripts/build-python-bundle.ps1:
///   cc-python-win-x64.zip       -> a relocatable CPython, extracted to InstallLayout.PythonDir
///   cc-tools-pyenv-win-x64.zip  -> wheelhouse/ + requirements.lock + tools-manifest.json
///
/// Flow: download + SHA-verify both assets, extract the python, create a venv with it, pip-install
/// every tool OFFLINE (--no-index --find-links wheelhouse) from the de-duped wheelhouse, then write
/// bin\&lt;script&gt;.cmd shims that forward to the venv's console-script exes. Windows-only; per-user,
/// no admin. The whole thing is idempotent: each install rebuilds python\ and pyenv\ from scratch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsInstaller
{
    public const string PythonAsset = "cc-python-win-x64.zip";
    public const string ToolsAsset = "cc-tools-pyenv-win-x64.zip";

    /// <summary>The component id the bundle's version is tracked under in installed.json.</summary>
    public const string ComponentId = "python-tools";

    private readonly InstallLayout _layout;

    public PythonToolsInstaller(InstallLayout layout)
        => _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    public async Task<PythonToolsResult> InstallAsync(
        ResolvedRelease release, ReleaseSource source, IProgress<string>? progress = null, CancellationToken ct = default)
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
            // 1. Download + verify both assets.
            Step($"downloading {PythonAsset}");
            pyZip = await source.DownloadAssetAsync(PythonAsset, release.DownloadUrls, ct);
            if (!Hashing.Sha256Matches(pyZip, pyAsset.Sha256))
                return Fail(steps, $"{PythonAsset} SHA-256 mismatch; download rejected.");

            Step($"downloading {ToolsAsset}");
            toolsZip = await source.DownloadAssetAsync(ToolsAsset, release.DownloadUrls, ct);
            if (!Hashing.Sha256Matches(toolsZip, toolsAsset.Sha256))
                return Fail(steps, $"{ToolsAsset} SHA-256 mismatch; download rejected.");

            // 2. Extract the python (into the canonical location) and the tools bundle (into temp).
            Step("extracting bundled Python");
            ResetDir(_layout.PythonDir);
            ZipFile.ExtractToDirectory(pyZip, _layout.PythonDir);

            bundleDir = Path.Combine(Path.GetTempPath(), $"cc-pytools-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(toolsZip, bundleDir);

            var manifestPath = Path.Combine(bundleDir, "tools-manifest.json");
            var wheelhouse = Path.Combine(bundleDir, "wheelhouse");
            if (!File.Exists(manifestPath)) return Fail(steps, "bundle is missing tools-manifest.json.");
            if (!Directory.Exists(wheelhouse)) return Fail(steps, "bundle is missing the wheelhouse.");

            var manifest = ToolsBundleManifest.Load(manifestPath);
            var pythonExe = Path.Combine(_layout.PythonDir, "python.exe");
            if (!File.Exists(pythonExe)) return Fail(steps, $"bundled python.exe not found at {pythonExe}.");

            // 3. Create the shared venv from the bundled python (on-target, so console-script paths are correct).
            Step("creating the shared Python venv");
            ResetDir(_layout.PyenvDir);
            var (venvExit, venvOut) = ProcessRunner.Run(pythonExe, $"-m venv \"{_layout.PyenvDir}\"");
            if (venvExit != 0) return Fail(steps, $"venv creation failed ({venvExit}): {Trim(venvOut)}");

            // 4. Install every tool OFFLINE from the wheelhouse.
            Step($"installing {manifest.Dists.Count} tools offline from the wheelhouse");
            var venvPython = Path.Combine(_layout.PyenvScriptsDir, "python.exe");
            var distArgs = string.Join(" ", manifest.Dists.Select(d => $"\"{d}\""));
            var pipArgs = $"-m pip install --no-index --find-links \"{wheelhouse}\" --no-warn-script-location {distArgs}";
            var (pipExit, pipOut) = ProcessRunner.Run(venvPython, pipArgs);
            if (pipExit != 0) return Fail(steps, $"offline pip install failed ({pipExit}): {Trim(pipOut)}");

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

    /// <summary>
    /// Each shim is a tiny .cmd in bin (already on PATH) that forwards to the venv's console-script
    /// exe via a path relative to bin, so the whole install tree stays movable as a unit.
    /// </summary>
    private void WriteShims(IReadOnlyList<string> scripts)
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
        var version = root.TryGetProperty("bundleVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException("tools-manifest.json has no 'bundleVersion'.");

        var dists = new List<string>();
        var scripts = new List<string>();
        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in toolsEl.EnumerateArray())
            {
                if (t.TryGetProperty("dist", out var d) && d.ValueKind == JsonValueKind.String)
                    dists.Add(d.GetString()!);
                if (t.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Array)
                    foreach (var sc in s.EnumerateArray())
                        if (sc.ValueKind == JsonValueKind.String) scripts.Add(sc.GetString()!);
            }
        }
        if (dists.Count == 0) throw new FormatException("tools-manifest.json lists no tools.");
        return new ToolsBundleManifest(version, dists, scripts);
    }
}
