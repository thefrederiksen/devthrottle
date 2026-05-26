using System.Text;
using System.Text.Json;
using CcDirector.Core.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Goal harness: run 100 synthetic Claude Code terminal states
/// (docs/features/terminal-state-detector/synthetic/states.json) through the Wingman's LLM
/// turn-state judge (<see cref="WingmanService.ClassifyTerminalStateAsync"/> - the
/// "one LLM call reads the screen" detector, no regex) and prove it classifies every one
/// correctly. Writes results.json + report.html next to the manifest, then asserts 100/100.
///
/// OPT-IN: spends ~100 strong-model calls per run, so it only runs when WINGMAN_SYNTH_TEST=1.
/// Run: WINGMAN_SYNTH_TEST=1 dotnet test --filter FullyQualifiedName~SyntheticStateJudgeTests
/// </summary>
public sealed class SyntheticStateJudgeTests
{
    private readonly ITestOutputHelper _out;
    public SyntheticStateJudgeTests(ITestOutputHelper output) => _out = output;

    private sealed record State(string Id, string Category, string Expected, string Note, string[] Screen);
    private sealed record Result(string Id, string Category, string Expected, string Got, string Reason, bool Pass, string ScreenText);

    [Fact]
    public async Task Judge_classifies_all_100_synthetic_states()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WINGMAN_SYNTH_TEST"), "1", StringComparison.Ordinal))
        {
            _out.WriteLine("Skipped: set WINGMAN_SYNTH_TEST=1 to run the 100-state judge harness.");
            return;
        }
        var claude = ResolveClaudePath();
        if (claude is null) { _out.WriteLine("Skipped: claude CLI not found."); return; }

        var dir = ResolveSyntheticDir();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "states.json")));
        var states = doc.RootElement.GetProperty("states").EnumerateArray()
            .Select(e => new State(
                e.GetProperty("id").GetString()!,
                e.GetProperty("category").GetString()!,
                e.GetProperty("expected").GetString()!,
                e.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
                e.GetProperty("screen").EnumerateArray().Select(r => r.GetString() ?? "").ToArray()))
            .ToList();
        Assert.Equal(100, states.Count);

        var results = new Result[states.Count];
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(states.Select(async (s, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var screen = string.Join("\n", s.Screen);
                var (got, reason, _) = await WingmanService.ClassifyTerminalStateAsync(screen, "Claude Code", claude!);
                var pass = Matches(s.Expected, got);
                results[i] = new Result(s.Id, s.Category, s.Expected, got, reason, pass, screen);
            }
            finally { gate.Release(); }
        }));

        var passed = results.Count(r => r.Pass);
        var failures = results.Where(r => !r.Pass).ToList();

        File.WriteAllText(Path.Combine(dir, "results.json"), JsonSerializer.Serialize(
            new { generatedUtc = DateTime.UtcNow.ToString("u"), passed, total = results.Length, results },
            new JsonSerializerOptions { WriteIndented = true }));
        WriteHtmlReport(Path.Combine(dir, "report.html"), results);

        _out.WriteLine($"PASS {passed}/{results.Length}");
        foreach (var f in failures)
            _out.WriteLine($"  FAIL {f.Id} [{f.Category}] expected={f.Expected} got={f.Got} :: {f.Reason}");

        Assert.True(failures.Count == 0,
            $"{failures.Count}/100 misclassified:\n" + string.Join("\n", failures.Select(f => $"{f.Id} expected {f.Expected} got {f.Got}")));
    }

    /// <summary>Equivalence classes: working and waiting_for_permission are strict; the
    /// "ready / turn-over" verdicts (waiting_for_input/idle/cancelled) are interchangeable for
    /// the cases where the agent is simply done and back at the prompt.</summary>
    private static bool Matches(string expected, string got)
    {
        got = (got ?? "").Trim().ToLowerInvariant();
        var ready = new[] { "waiting_for_input", "idle", "cancelled" };
        return expected switch
        {
            "working" => got == "working",
            "waiting_for_permission" => got == "waiting_for_permission",
            "waiting_for_input" => ready.Contains(got),
            "cancelled" => ready.Contains(got),
            "unknown" => got == "unknown",
            _ => false,
        };
    }

    private static void WriteHtmlReport(string path, IReadOnlyList<Result> results)
    {
        var passed = results.Count(r => r.Pass);
        var byCat = results.GroupBy(r => r.Category)
            .Select(g => $"{g.Key}: {g.Count(x => x.Pass)}/{g.Count()}");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Terminal-State Judge Report</title><style>");
        sb.Append("body{font-family:system-ui,Segoe UI,Arial;margin:24px;background:#0e1116;color:#e6eaf2}h1{margin:0 0 4px}");
        sb.Append(".sum{font-size:20px;margin:8px 0 16px}.ok{color:#5fd08a}.bad{color:#e5484d}");
        sb.Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #2a3140;padding:6px 8px;text-align:left;vertical-align:top;font-size:13px}");
        sb.Append("th{background:#161b22}tr.f{background:#2a1416}pre{margin:0;white-space:pre-wrap;font-family:Consolas,monospace;font-size:11px;color:#9aa4b2;max-height:160px;overflow:auto}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>Wingman terminal-state judge - 100 synthetic states</h1>");
        var cls = passed == results.Count ? "ok" : "bad";
        sb.Append($"<div class='sum'>Detected correctly: <span class='{cls}'>{passed}/{results.Count}</span> &nbsp; <small>{string.Join(" &nbsp; ", byCat)}</small></div>");
        sb.Append($"<div><small>Generated {DateTime.UtcNow:u} . judge=WingmanService.ClassifyTerminalStateAsync (one LLM call, no regex)</small></div><br>");
        sb.Append("<table><tr><th>#</th><th>cat</th><th>expected</th><th>got</th><th>result</th><th>judge reason</th><th>screen</th></tr>");
        foreach (var r in results)
        {
            var row = r.Pass ? "" : " class='f'";
            var res = r.Pass ? "<span class='ok'>PASS</span>" : "<span class='bad'>FAIL</span>";
            sb.Append($"<tr{row}><td>{Esc(r.Id)}</td><td>{Esc(r.Category)}</td><td>{Esc(r.Expected)}</td><td>{Esc(r.Got)}</td><td>{res}</td><td>{Esc(r.Reason)}</td><td><pre>{Esc(r.ScreenText)}</pre></td></tr>");
        }
        sb.Append("</table></body></html>");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string? ResolveClaudePath()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "claude.exe", "claude" })
                if (File.Exists(Path.Combine(d.Trim(), name))) return Path.Combine(d.Trim(), name);
        return null;
    }

    private static string ResolveSyntheticDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, "docs", "features", "terminal-state-detector", "synthetic");
            if (Directory.Exists(c)) return c;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate docs/features/terminal-state-detector/synthetic from " + AppContext.BaseDirectory);
    }
}
