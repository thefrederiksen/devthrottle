using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;

namespace CcDirector.DeliveryQualification;

public static class Rig
{
    /// <summary>A small git repository for one rig slot, with the README the at-reference text names. With
    /// <paramref name="trust"/> it is marked trusted for Claude Code through the product's own writer, as an owner who
    /// had opened it once would have - so the rig measures delivery, not the folder-trust question, which is a
    /// scenario of its own (untrusted-folder).</summary>
    public static string EnsureRepo(string dir, bool trust = true)
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
        if (trust)
        {
            var folderTrust = new CcDirector.Core.Claude.ClaudeFolderTrust(CcDirector.Core.Claude.ClaudeFolderTrust.ConfigPathForThisUser());
            if (!folderTrust.IsRecorded(dir)) folderTrust.Trust(dir);
        }
        return dir;
    }

    /// <summary>A new linked worktree of <paramref name="repo"/> at <paramref name="dir"/>, on a detached head. Each
    /// attempt gets its own, as each reviewer seat does; the rig removes none of them.</summary>
    public static string AddWorktree(string repo, string dir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Git(repo, $"worktree add -q --detach \"{dir}\"");
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

/// <summary>
/// A PRIVATE CLAUDE CODE CONFIGURATION FOLDER FOR ONE ATTEMPT, so a scenario can put Claude Code in a state the owner's
/// own records are not in, without touching those records. It holds a copy of the owner's configuration file with the
/// scenario's changes, a settings file of its own, and a copy of the sign-in. While it is alive, CLAUDE_CONFIG_DIR names
/// it, so the agents the rig starts and the Director's own checks both read it; disposing it restores the variable and
/// deletes the copy of the sign-in.
///
/// THE SIGN-IN IS NEVER REFRESHED FROM HERE. A copy that refreshed would rotate the owner's sign-in out from under the
/// owner's own file, so a token with less than half an hour left is refused before anything starts.
/// </summary>
public sealed class ScratchClaudeConfig : IDisposable
{
    private readonly string? _previous;

    public string Folder { get; }

    private ScratchClaudeConfig(string folder)
    {
        Folder = folder;
        _previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", folder);
    }

    /// <summary>
    /// A folder in which Claude Code, started in auto mode in <paramref name="trustedFolder"/>, offers "Teach auto mode
    /// about your environment?" after the first turn: started often enough, auto mode has been turned down often
    /// enough, no environment is described, and the auto mode warning is already seen. With
    /// <paramref name="ownerChoseDontShowAgain"/> the owner has answered it "Don't show again".
    /// </summary>
    public static ScratchClaudeConfig ForAutoModeQuestion(string folder, string trustedFolder, bool ownerChoseDontShowAgain)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ownerFolder = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(home, ".claude");
        var credentials = Path.Combine(ownerFolder, ".credentials.json");
        if (!File.Exists(credentials))
            throw new InvalidOperationException($"No Claude Code sign-in file at {credentials} to copy (a machine that keeps it elsewhere cannot run this scenario).");
        using (var doc = JsonDocument.Parse(File.ReadAllText(credentials)))
        {
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(doc.RootElement.GetProperty("claudeAiOauth").GetProperty("expiresAt").GetInt64());
            if (expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(30))
                throw new InvalidOperationException($"The Claude Code sign-in expires at {expires:u}; a copy could refresh it and rotate the owner's own out. Run again after Claude Code has refreshed it.");
        }

        var ownerConfig = CcDirector.Core.Claude.ClaudeFolderTrust.ConfigPathForThisUser();
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ownerConfig))!.AsObject();
        // Claude Code asks only when its server setting says so; the copy keeps the owner's, so a machine that is not
        // offered the question cannot run the scenario, and says so, instead of passing it (review of pull request 4180, finding 2).
        if (config["cachedGrowthBookFeatures"]?["tengu_auto_mode_config"]?["envOnboarding"] is not System.Text.Json.Nodes.JsonValue offer
            || !offer.TryGetValue<bool>(out var offered) || !offered)
            throw new InvalidOperationException("Claude Code on this machine is not offered the auto mode question (its cached tengu_auto_mode_config.envOnboarding is not true), so the scenario cannot put a seat in front of it.");
        config["numStartups"] = 10;
        config["hasSeenAutoModeEntryWarning"] = true;
        config["autoModeEnvSetup"] = ownerChoseDontShowAgain
            ? new System.Text.Json.Nodes.JsonObject { ["denials"] = 5, ["dismissed"] = true }
            : new System.Text.Json.Nodes.JsonObject { ["denials"] = 5 };
        config["projects"] = new System.Text.Json.Nodes.JsonObject
        {
            [CcDirector.Core.Claude.ClaudeFolderTrust.KeyFor(trustedFolder)] = new System.Text.Json.Nodes.JsonObject { ["hasTrustDialogAccepted"] = true },
        };

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".claude.json"), config.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
        File.Copy(credentials, Path.Combine(folder, ".credentials.json"), overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(folder, ".credentials.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new ScratchClaudeConfig(folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _previous);
        File.Delete(Path.Combine(Folder, ".credentials.json"));
    }
}
