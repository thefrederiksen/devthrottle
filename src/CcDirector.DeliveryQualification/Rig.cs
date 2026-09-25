using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;

namespace CcDirector.DeliveryQualification;

public static class Rig
{
    /// <summary>A small git repository for one rig slot, with the README the at-reference text names.</summary>
    public static string EnsureRepo(string dir)
    {
        Directory.CreateDirectory(dir);
        var readme = Path.Combine(dir, "README.md");
        if (!File.Exists(readme))
            File.WriteAllText(readme, "# delivery-qa\n\nA scratch repository for the text-delivery qualification rig. Nothing here matters.\n");
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            Git(dir, "init -q");
            Git(dir, "add README.md");
            Git(dir, "-c user.name=rig -c user.email=rig@delivery-qa.invalid commit -q -m init");
        }
        return dir;
    }

    private static void Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} in {dir} failed: {p.StandardError.ReadToEnd()}");
    }

    /// <summary>
    /// Wait until the session has been waiting for input for three seconds in a row - the moment a person would
    /// send their next message. False if that never happens within <paramref name="limit"/>.
    /// </summary>
    public static async Task<bool> WaitUntilIdleAsync(Session session, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        var idleSince = (DateTime?)null;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = session.ActivityState is ActivityState.WaitingForInput or ActivityState.Idle;
            if (waiting) idleSince ??= DateTime.UtcNow; else idleSince = null;
            if (idleSince is { } since && DateTime.UtcNow - since >= TimeSpan.FromSeconds(3)) return true;
            await Task.Delay(250);
        }
        return false;
    }
}

/// <summary>The agent executables the owner's live Director is configured with, so the rig launches exactly them.</summary>
public static class DirectorAgentPaths
{
    public static AgentOptions Load()
    {
        var options = new AgentOptions();
        // The rig has already pointed CC_DIRECTOR_ROOT at its own private home, so Root() would answer the rig's
        // folder. DefaultRoot() ignores that setting and answers the machine's own install, which is where the
        // owner's live default Director keeps its config.
        var config = Path.Combine(CcStorage.DefaultRoot(), "instances", "default", "config", "config.json");
        if (!File.Exists(config))
            throw new FileNotFoundException($"The live Director's config was not found at {config}; the rig takes the agent paths from it.");
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        foreach (var entry in doc.RootElement.GetProperty("agent").GetProperty("entries").EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            var path = entry.TryGetProperty("executable_path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path)) continue;
            switch (type)
            {
                case "ClaudeCode": options.ClaudePath = path; break;
                case "Codex": options.CodexPath = path; break;
                case "Pi": options.PiPath = path; break;
                case "Gemini": options.GeminiPath = path; break;
                case "Grok": options.GrokPath = path; break;
                case "OpenCode": options.OpenCodePath = path; break;
                case "Copilot": options.CopilotPath = path; break;
            }
        }
        return options;
    }
}

public static class Report
{
    public static void Print(IReadOnlyList<SendResult> results) => Console.WriteLine(Render(results));

    public static string Render(IReadOnlyList<SendResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("agent     path   sends  arrived  lost  partial  doubled  silent-loss  false-alarm");
        foreach (var g in results.Where(r => r.Path is "first" or "idle").GroupBy(r => (r.Agent, r.Path)).OrderBy(g => g.Key.Agent).ThenBy(g => g.Key.Path))
        {
            var list = g.ToList();
            sb.AppendLine($"{g.Key.Agent,-9} {g.Key.Path,-6} {list.Count,5}  {list.Count(r => r.Truth == "ARRIVED"),7}  {list.Count(r => r.Truth == "LOST"),4}  " +
                          $"{list.Count(r => r.Truth == "PARTIAL"),7}  {list.Count(r => r.Truth == "DOUBLED"),7}  " +
                          $"{list.Count(r => r.Director == "OK" && r.Truth != "ARRIVED"),11}  {list.Count(r => r.Director != "OK" && r.Truth == "ARRIVED"),11}");
        }
        var starts = results.Count(r => r.Path == "start");
        if (starts > 0) sb.AppendLine($"sessions that did not start: {starts}");
        var bad = results.Where(r => r.Truth != "ARRIVED" || r.Director != "OK").ToList();
        sb.AppendLine(bad.Count == 0 ? "EVERY SEND ARRIVED WHOLE AND THE DIRECTOR SAID SO." : $"{bad.Count} send(s) need attention:");
        foreach (var r in bad)
            sb.AppendLine($"  {r.Agent} {r.Path} {r.Shape} len={r.Length}: director={r.Director} truth={r.Truth} {r.Detail}");
        return sb.ToString();
    }
}

internal static class CancellationExtensions
{
    public static bool IsCancelled(this CancellationTokenSource cts) => cts.IsCancellationRequested;
}
