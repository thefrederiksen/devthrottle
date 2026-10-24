using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// The command-line interface versions whose terminal screens have passed the on-demand conformance run.
/// This is deliberately a warning, not a launch gate: a new version remains usable while its changed screens
/// are made visible in the log.
/// </summary>
public static class CliConformance
{
    public const string ManifestRelativePath = "src/CcDirector.Core/Agents/cli-conformance-passed.json";
    private const string ManifestResourceName = "CcDirector.Core.Agents.cli-conformance-passed.json";
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> PassedVersions = LoadPassedVersions();

    /// <summary>Return one warning for each installed version that has never passed conformance.</summary>
    public static IReadOnlyList<string> WarningsFor(IReadOnlyDictionary<string, string> installedVersions)
    {
        FileLog.Write($"[CliConformance] WarningsFor: installed={installedVersions.Count}");
        var warnings = new List<string>();
        foreach (var (name, version) in installedVersions)
        {
            if (!PassedVersions.TryGetValue(name, out var passed) || !passed.Contains(version))
                warnings.Add($"[CliConformance] WARNING: {name} version '{version}' has never passed the terminal conformance run; session launch remains enabled.");
        }
        FileLog.Write($"[CliConformance] WarningsFor: warnings={warnings.Count}");
        return warnings;
    }

    /// <summary>Read the configured command-line interface versions and log never-passed warnings.</summary>
    public static void LogInstalledVersions(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FileLog.Write("[CliConformance] LogInstalledVersions: checking configured command-line interfaces");
        var installed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Claude Code"] = ReadVersion(options.ClaudePath),
            ["Codex"] = ReadVersion(options.CodexPath),
            ["Pi"] = ReadVersion(options.PiPath),
        };
        foreach (var warning in WarningsFor(installed)) FileLog.Write(warning);
        FileLog.Write("[CliConformance] LogInstalledVersions: complete");
    }

    private static string ReadVersion(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return "not configured";
        var start = new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "could not start";
            if (!process.WaitForExit(2_000))
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
                return "version check did not exit within two seconds";
            }
            var line = process.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(value => value.Trim().Length > 0);
            return string.IsNullOrWhiteSpace(line) ? "version output was empty" : line.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"version check failed: {ex.Message}";
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> LoadPassedVersions()
    {
        using var stream = typeof(CliConformance).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException($"Embedded conformance manifest '{ManifestResourceName}' was not found.");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new InvalidDataException("The embedded conformance manifest was empty.");
        return manifest.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)new HashSet<string>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }
}
