using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// What a scheduled factory run's Claude Code launch adds, as <see cref="ScheduledFactoryRunSettings.Compose"/> worked it
/// out: the run's own settings file, the folders whose instructions it is pointed at, and every ask or deny rule that was
/// set aside, named with the file it came from.
/// </summary>
/// <param name="SettingsPath">The run's own settings file, passed as its one <c>--settings</c>.</param>
/// <param name="InstructionFolders">The working folder first, then each folder above it that holds instructions, each
/// passed as <c>--add-dir</c>.</param>
/// <param name="RulesSetAside">"file: rule" for every ask and deny rule the run does not get.</param>
public sealed record ScheduledFactoryRunLaunch(string SettingsPath, IReadOnlyList<string> InstructionFolders, IReadOnlyList<string> RulesSetAside);

/// <summary>
/// A SCHEDULED FACTORY RUN NEVER PROMPTS (the owner, 26 October 2026: "A factory run never prompts. The Director also
/// ignores ask rules in a scheduled factory run, so no new list can ever stall one again." - deny rules too). At 07:00
/// that day factory seats started by a schedule sat on "Do you want to proceed?" because the seat folder's
/// <c>.claude/settings.json</c> carried an ask list, and nobody is there at 07:00 to answer.
///
/// WHY THE FOLDER'S SETTINGS ARE LEFT OUT RATHER THAN OVERRIDDEN. Claude Code 2.1.310 was driven in a real terminal
/// against a folder with the ask rule <c>Bash(echo *)</c> (proof in <c>docs/proof/scheduled-factory-run-never-prompts.md</c>).
/// It prompted under <c>--dangerously-skip-permissions</c>, under an empty ask list in <c>--settings</c>, under an
/// allow rule for the same command in <c>--allowedTools</c>, under a PreToolUse hook answering "allow", under a
/// PermissionRequest hook answering "allow", and under <c>allowManagedPermissionRulesOnly</c> in <c>--settings</c>. An
/// ask rule from a loaded settings file wins over all of them. The only launch that ran the command was one that did
/// not load the folder's settings files at all: <c>--setting-sources user</c>.
///
/// WHAT THE RUN KEEPS. Leaving the folder's files out would also lose everything else in them and the folder's
/// CLAUDE.md, so the run is given them back:
/// <list type="bullet">
/// <item>its OWN settings file: the folder's <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c> merged
/// the way Claude Code merges them, under the Director's own settings (its hooks, and a seat's kit rules), with
/// <c>permissions.ask</c> and <c>permissions.deny</c> removed and nothing else changed - hooks, environment, model and
/// allow rules all stay;</item>
/// <item><c>--add-dir</c> for the working folder and every folder above it that holds a CLAUDE.md or a <c>.claude</c>
/// rules or skills folder, up to but not including the user's home and the drive's root, with
/// <see cref="InstructionsEnvVar"/> set, which is what makes Claude Code read the CLAUDE.md of an added folder.</item>
/// </list>
/// The user's own settings (<c>~/.claude/settings.json</c>) are still read, as for every session.
///
/// WHICH SESSION IS ONE. A session the Gateway's schedule or trigger started (<see cref="SessionOriginKinds.Schedule"/>)
/// that belongs to a factory (<see cref="Session.Factory"/>). The origin is settled at the Gateway's spawn doors from the
/// verified credential: a person's device is always "human" and a session's key is always "agent", so no session and
/// no person can make an ordinary session look scheduled. A session a person opens, a mission seat, and a schedule that
/// is not a factory's keep their settings exactly as before.
/// </summary>
public static class ScheduledFactoryRunSettings
{
    /// <summary>Claude Code reads the CLAUDE.md of a folder added with <c>--add-dir</c> only when this is set.</summary>
    public const string InstructionsEnvVar = "CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD";

    /// <summary>The folder under the Claude hooks folder that holds each run's own settings file, named by session id.</summary>
    public const string FolderName = "scheduled-factory-runs";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions AsClaudeReadsIt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>True for a session a schedule or trigger started in a factory: the only sessions whose ask and deny
    /// rules are set aside.</summary>
    public static bool IsScheduledFactoryRun(string? originKind, string? factory)
        => string.Equals(originKind, SessionOriginKinds.Schedule, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(factory);

    /// <summary>A run's own settings file under <paramref name="hooksDirectory"/>.</summary>
    public static string SettingsPathFor(Guid sessionId, string hooksDirectory)
        => Path.Combine(hooksDirectory, FolderName, sessionId + ".json");

    /// <summary>
    /// Write the run's own settings file and work out the folders it is pointed at. <paramref name="directorSettingsPath"/>
    /// is the settings file the session would have been given otherwise (the shared hooks file, or a seat's own file),
    /// or null when there is none. Throws <see cref="InvalidOperationException"/> naming the file when one of the
    /// folder's settings files cannot be read as Claude Code reads it: the run is never started on a guess at what it said.
    /// </summary>
    public static ScheduledFactoryRunLaunch Compose(Guid sessionId, string workingDirectory, string? directorSettingsPath,
        string hooksDirectory, string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        FileLog.Write($"[ScheduledFactoryRunSettings] Compose: session={sessionId}, folder={workingDirectory}, director={directorSettingsPath ?? "(none)"}");

        var setAside = new List<string>();
        var merged = new JsonObject();
        // Claude Code's own order: the folder's shared file, then its local file, then the --settings file over both.
        var sources = new List<string>
        {
            Path.Combine(workingDirectory, ".claude", "settings.json"),
            Path.Combine(workingDirectory, ".claude", "settings.local.json"),
        };
        if (!string.IsNullOrEmpty(directorSettingsPath))
            sources.Add(directorSettingsPath);
        foreach (var path in sources)
        {
            if (Read(path) is not { } settings) continue;
            setAside.AddRange(TakeAskAndDeny(settings).Select(rule => $"{path}: {rule}"));
            MergeInto(merged, settings);
        }

        var target = SettingsPathFor(sessionId, hooksDirectory);
        SessionHookFiles.WriteAtomic(target, merged.ToJsonString(Indented) + "\n");
        var folders = InstructionFolders(workingDirectory, home);
        FileLog.Write($"[ScheduledFactoryRunSettings] Compose: session={sessionId}, settings={target}, folders={folders.Count}, setAside={setAside.Count}");
        return new ScheduledFactoryRunLaunch(target, folders, setAside);
    }

    /// <summary>
    /// The Claude Code launch line of a scheduled factory run: <paramref name="args"/>, then <c>--setting-sources user</c>,
    /// an <c>--add-dir</c> per instruction folder, the run's one <c>--settings</c>, and <c>--plugin-dir</c> when a seat
    /// has factory skills. Each <c>--add-dir</c> is followed by another flag, so it never takes a word meant for something else.
    /// </summary>
    public static string ClaudeArguments(string args, ScheduledFactoryRunLaunch launch, string? pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var line = $"{args} --setting-sources user".Trim();
        foreach (var folder in launch.InstructionFolders)
            line += $" --add-dir \"{folder}\"";
        line += $" --settings \"{launch.SettingsPath}\"";
        if (pluginDirectory is not null)
            line += $" --plugin-dir \"{pluginDirectory}\"";
        return line;
    }

    /// <summary>The launch log's line for what <see cref="Compose"/> did.</summary>
    public static string LogLine(ScheduledFactoryRunLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var rules = launch.RulesSetAside.Count == 0
            ? "it had no ask or deny rules to set aside"
            : $"{launch.RulesSetAside.Count} ask or deny rule(s) set aside: {string.Join("; ", launch.RulesSetAside)}";
        return "SCHEDULED FACTORY RUN: started without the folder's ask and deny rules, so nothing waits on a person " +
               $"(--setting-sources user, the folder's other settings in --settings \"{launch.SettingsPath}\", " +
               $"instructions from {string.Join(", ", launch.InstructionFolders)}); {rules}.";
    }

    /// <summary>Remove a run's own settings file. A failure is logged and never thrown: the file is a copy.</summary>
    public static void DeleteFor(Guid sessionId, string hooksDirectory)
    {
        var path = SettingsPathFor(sessionId, hooksDirectory);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                FileLog.Write($"[ScheduledFactoryRunSettings] DeleteFor: removed {path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[ScheduledFactoryRunSettings] DeleteFor FAILED: could not remove {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The working folder, then every folder above it that holds instructions Claude Code would read from it: a
    /// CLAUDE.md, a <c>.claude/CLAUDE.md</c>, or a <c>.claude/rules</c> or <c>.claude/skills</c> folder. The walk stops
    /// at the user's home, whose <c>.claude</c> is the user's own and is read anyway, and never adds a drive's root.
    /// </summary>
    internal static List<string> InstructionFolders(string workingDirectory, string home)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var homeFull = Trim(Path.GetFullPath(home));
        var folders = new List<string> { Trim(Path.GetFullPath(workingDirectory)) };
        for (var dir = Directory.GetParent(folders[0]); dir is not null; dir = dir.Parent)
        {
            var full = Trim(dir.FullName);
            if (string.Equals(full, homeFull, comparison) || dir.Parent is null) break;
            if (HoldsInstructions(full)) folders.Add(full);
        }
        return folders;
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool HoldsInstructions(string dir)
        => File.Exists(Path.Combine(dir, "CLAUDE.md"))
           || File.Exists(Path.Combine(dir, ".claude", "CLAUDE.md"))
           || Directory.Exists(Path.Combine(dir, ".claude", "rules"))
           || Directory.Exists(Path.Combine(dir, ".claude", "skills"));

    /// <summary>The file as Claude Code reads it - comments and trailing commas allowed - or null when it is missing.</summary>
    private static JsonObject? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"No session was started: the scheduled factory run's settings file {path} could not be read ({ex.Message}).", ex);
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text, documentOptions: AsClaudeReadsIt) as JsonObject
                ?? throw new InvalidOperationException($"No session was started: the scheduled factory run's settings file {path} is not a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"No session was started: the scheduled factory run's settings file {path} is not JSON Claude Code can read ({ex.Message}).", ex);
        }
    }

    /// <summary>Remove <c>permissions.ask</c> and <c>permissions.deny</c> and return what they held; an emptied
    /// <c>permissions</c> goes too.</summary>
    private static List<string> TakeAskAndDeny(JsonObject settings)
    {
        var taken = new List<string>();
        if (settings["permissions"] is not JsonObject permissions) return taken;
        foreach (var key in new[] { "ask", "deny" })
        {
            if (!permissions.ContainsKey(key)) continue;
            if (permissions[key] is JsonArray list)
                taken.AddRange(list.Select(rule => $"{key} {rule?.ToJsonString()}"));
            else
                taken.Add($"{key} {permissions[key]?.ToJsonString() ?? "null"}");
            permissions.Remove(key);
        }
        if (permissions.Count == 0) settings.Remove("permissions");
        return taken;
    }

    /// <summary>Claude Code's merge: objects merge key by key, lists are joined without repeats, and any other value
    /// from the later file wins.</summary>
    internal static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (target[key] is JsonObject into && value is JsonObject from)
                MergeInto(into, from);
            else if (target[key] is JsonArray list && value is JsonArray more)
            {
                foreach (var item in more)
                    if (!list.Any(existing => JsonNode.DeepEquals(existing, item)))
                        list.Add(item?.DeepClone());
            }
            else
                target[key] = value?.DeepClone();
        }
    }
}
