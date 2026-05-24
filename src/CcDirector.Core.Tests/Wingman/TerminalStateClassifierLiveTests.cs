using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// LIVE regression tests for the terminal-state classifier. Each fixture under
/// docs/features/terminal-state-detector/fixtures is a representative Claude Code
/// terminal tail; we run it through the REAL classifier (a fresh "claude --print"
/// Haiku call) and assert the verdict maps to the expected ActivityState.
///
/// The bypass-footer and plan-mode fixtures are the guard for the field bug where
/// the mode footer "bypass permissions on (shift+tab to cycle)" was misread as a
/// permission prompt.
///
/// OPT-IN: these spend tokens and need the claude CLI, so they only run when
/// WINGMAN_LIVE_TESTS=1. They are skipped otherwise (no false green, no false red).
/// Running the suite also writes results.json next to the fixtures so the verdict +
/// the model's own reason can be reported.
/// </summary>
public sealed class TerminalStateClassifierLiveTests
{
    private readonly ITestOutputHelper _out;

    public TerminalStateClassifierLiveTests(ITestOutputHelper output) => _out = output;

    private sealed record FixtureCase(string File, string ExpectedState, string Note);
    private sealed record ResultRow(string File, string Judge, string Expected, string Verdict, string MappedState, string Reason, bool Pass);

    [Fact]
    public async Task Classifier_maps_every_fixture_to_its_expected_state()
    {
        // Opt-in: spends tokens and needs the claude CLI. When not opted in we return
        // (a no-op pass) rather than fail, so normal CI is unaffected.
        if (!string.Equals(Environment.GetEnvironmentVariable("WINGMAN_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            _out.WriteLine("Skipped: set WINGMAN_LIVE_TESTS=1 to run the live classifier regression.");
            return;
        }

        var claude = ResolveClaudePath();
        if (claude is null)
        {
            _out.WriteLine("Skipped: claude CLI not found on PATH or in %USERPROFILE%/.local/bin.");
            return;
        }

        var fixturesDir = ResolveFixturesDir();
        var manifestPath = Path.Combine(fixturesDir, "manifest.json");
        Assert.True(File.Exists(manifestPath), $"manifest.json not found at {manifestPath}");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var cases = manifestDoc.RootElement.GetProperty("fixtures").EnumerateArray()
            .Select(e => new FixtureCase(
                e.GetProperty("file").GetString()!,
                e.GetProperty("expectedState").GetString()!,
                e.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""))
            .ToList();

        var repoRoot = ResolveRepoRoot(fixturesDir);
        var runFull = string.Equals(Environment.GetEnvironmentVariable("WINGMAN_LIVE_FULLSESSION"), "1", StringComparison.Ordinal);

        var rows = new List<ResultRow>();
        foreach (var c in cases)
        {
            var text = File.ReadAllText(Path.Combine(fixturesDir, c.File));

            // Judge 1: tail-paste one-shot.
            var (v1, r1) = await WingmanService.ClassifyTerminalStateAsync(text, "Claude Code", claude!);
            rows.Add(MakeRow(c, "tail-paste", v1, r1));

            // Judge 2: full-power read-only session (Phase 2). Opt-in within the opt-in,
            // because it is slower (spins a real session per fixture).
            if (runFull)
            {
                var (v2, r2) = await WingmanService.ClassifyTerminalStateViaSessionAsync(text, "Claude Code", repoRoot, claude!);
                rows.Add(MakeRow(c, "full-session", v2, r2));
            }
        }

        ResultRow MakeRow(FixtureCase c, string judge, string verdict, string reason)
        {
            var mapped = TerminalStateDetector.MapVerdictToActivityState(verdict).ToString();
            var pass = string.Equals(mapped, c.ExpectedState, StringComparison.Ordinal);
            _out.WriteLine($"{(pass ? "PASS" : "FAIL")}  [{judge}] {c.File}: expected={c.ExpectedState} verdict={verdict} mapped={mapped} reason=\"{reason}\"");
            return new ResultRow(c.File, judge, c.ExpectedState, verdict, mapped, reason, pass);
        }

        var resultsPath = Path.Combine(fixturesDir, "results.json");
        File.WriteAllText(resultsPath, JsonSerializer.Serialize(
            new { generatedUtc = DateTime.UtcNow.ToString("u"), rows },
            new JsonSerializerOptions { WriteIndented = true }));
        _out.WriteLine($"results written to {resultsPath}");

        var failures = rows.Where(r => !r.Pass).ToList();
        Assert.True(failures.Count == 0,
            "Misclassified: " + string.Join("; ", failures.Select(f => $"[{f.Judge}] {f.File} expected {f.Expected} got {f.MappedState} ({f.Verdict}: {f.Reason})")));
    }

    private static string ResolveRepoRoot(string fixturesDir)
    {
        // fixturesDir = <root>/docs/features/terminal-state-detector/fixtures
        var dir = new DirectoryInfo(fixturesDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? fixturesDir;
    }

    private static string? ResolveClaudePath()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude" })
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string ResolveFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "features", "terminal-state-detector", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate docs/features/terminal-state-detector/fixtures by walking up from " + AppContext.BaseDirectory);
    }
}
