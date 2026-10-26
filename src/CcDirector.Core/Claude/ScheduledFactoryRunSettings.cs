using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// What a scheduled factory run's Claude Code launch adds, as <see cref="ScheduledFactoryRunSettings.Compose"/> worked it
/// out: the run's own settings file, the folders whose instructions it is pointed at, the folders whose skills it loads,
/// its own server list, and every ask or deny rule that was set aside, named with the file it came from.
/// </summary>
/// <param name="SettingsPath">The run's own settings file, passed as its one <c>--settings</c>.</param>
/// <param name="InstructionFolders">The working folder first, then each folder above it that holds instructions, then
/// the folder holding the user's own instructions, each passed as <c>--add-dir</c>.</param>
/// <param name="SkillFolders">The user's configuration folder and each <c>.claude</c> folder from the working folder up
/// that holds skills, commands or agents, each passed as <c>--plugin-dir</c>.</param>
/// <param name="McpConfigPath">The run's own server list, passed as <c>--mcp-config</c>; null when there is no server.</param>
/// <param name="RulesSetAside">"file: rule" for every ask and deny rule the run does not get.</param>
public sealed record ScheduledFactoryRunLaunch(string SettingsPath, IReadOnlyList<string> InstructionFolders,
    IReadOnlyList<string> SkillFolders, string? McpConfigPath, IReadOnlyList<string> RulesSetAside);

/// <summary>
/// A SCHEDULED FACTORY RUN NEVER PROMPTS (the owner, 26 October 2026: "A factory run never prompts. The Director also
/// ignores ask rules in a scheduled factory run, so no new list can ever stall one again." - deny rules too). At 07:00
/// that day factory seats started by a schedule sat on "Do you want to proceed?" because the seat folder's
/// <c>.claude/settings.json</c> carried an ask list, and nobody is there at 07:00 to answer.
///
/// WHY NO SETTINGS FILE IS LOADED AT ALL. Claude Code 2.1.310 was driven in a real terminal (proof in
/// <c>docs/proof/scheduled-factory-run-never-prompts.md</c>). An ask rule from a loaded settings file prompted under
/// <c>--dangerously-skip-permissions</c>, an empty ask list in <c>--settings</c>, <c>--allowedTools</c>, a PreToolUse or
/// PermissionRequest hook answering "allow", and <c>allowManagedPermissionRulesOnly</c> in <c>--settings</c> or in a
/// policy folder named by <c>CLAUDE_CODE_MANAGED_SETTINGS_PATH</c> (which this build does not read). The only launch
/// that ran the command was one that did not load the file. That holds for the user's own
/// <c>~/.claude/settings.json</c> as much as for the folder's (review 2, finding 1), and the Director itself writes ask
/// rules there (<see cref="Agents.DenyRulesBecomeAsk"/>), so the run loads none: <c>--setting-sources ""</c>.
///
/// WHAT THE RUN IS GIVEN BACK. Not loading a source also loses what else it brings, so each is handed back by a flag
/// Claude Code reads without loading a settings file:
/// <list type="bullet">
/// <item>its OWN settings file: the user's settings, the folder's <c>.claude/settings.json</c> and
/// <c>.claude/settings.local.json</c> merged the way Claude Code merges them, under the Director's own settings (its
/// hooks, and a seat's kit rules), with <c>permissions.ask</c> and <c>permissions.deny</c> removed and nothing else
/// changed - hooks, environment, model, allow rules and enabled plugins all stay;</item>
/// <item><c>--add-dir</c> for the working folder, every folder above it that holds a CLAUDE.md, a <c>.claude/CLAUDE.md</c>
/// or a <c>.claude/rules</c> folder, and the folder holding the user's own <c>.claude</c>, with
/// <see cref="InstructionsEnvVar"/> set, which is what makes Claude Code read the instructions of an added folder;</item>
/// <item><c>--plugin-dir</c> for the user's <c>.claude</c> and every <c>.claude</c> from the working folder up that holds
/// skills, commands or agents, so they are in the run's skill list (named <c>&lt;folder&gt;:&lt;skill&gt;</c>; a bare
/// <c>/skill</c> still runs them) - <c>--add-dir</c> alone does not list them (review 2, finding 2);</item>
/// <item><c>--mcp-config</c> with the servers Claude Code would have started: the user's, every <c>.mcp.json</c> from the
/// working folder up but those the settings disable, and the folder's own from <c>~/.claude.json</c>.</item>
/// </list>
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

    /// <summary>The folder under the Claude hooks folder that holds each run's own files, named by session id.</summary>
    public const string FolderName = "scheduled-factory-runs";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions AsClaudeReadsIt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True for a session a schedule or trigger started in a factory: the only sessions whose ask and deny
    /// rules are set aside.</summary>
    public static bool IsScheduledFactoryRun(string? originKind, string? factory)
        => string.Equals(originKind, SessionOriginKinds.Schedule, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(factory);

    /// <summary>A run's own settings file under <paramref name="hooksDirectory"/>.</summary>
    public static string SettingsPathFor(Guid sessionId, string hooksDirectory)
        => Path.Combine(hooksDirectory, FolderName, sessionId + ".json");

    /// <summary>A run's own server list under <paramref name="hooksDirectory"/>.</summary>
    public static string McpConfigPathFor(Guid sessionId, string hooksDirectory)
        => Path.Combine(hooksDirectory, FolderName, sessionId + ".mcp.json");

    /// <summary>
    /// Write the run's own settings file and server list, and work out the folders it is pointed at.
    /// <paramref name="directorSettingsPath"/> is the settings file the session would have been given otherwise (the
    /// shared hooks file, or a seat's own file), or null when there is none. <paramref name="environment"/> answers
    /// CLAUDE_CONFIG_DIR, which moves the user's configuration folder. Throws <see cref="InvalidOperationException"/>
    /// naming the file when a settings file or <c>.claude.json</c> cannot be read as Claude Code reads it: the run is
    /// never started on a guess at what it said.
    /// </summary>
    public static ScheduledFactoryRunLaunch Compose(Guid sessionId, string workingDirectory, string? directorSettingsPath,
        string hooksDirectory, string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);
        var userFolder = Trim(Path.GetFullPath(ClaudeSettingsFiles.ConfigFolder(environment, home)));
        var stateFile = string.IsNullOrWhiteSpace(environment("CLAUDE_CONFIG_DIR"))
            ? Path.Combine(home, ".claude.json")
            : Path.Combine(userFolder, ".claude.json");
        FileLog.Write($"[ScheduledFactoryRunSettings] Compose: session={sessionId}, folder={workingDirectory}, director={directorSettingsPath ?? "(none)"}, user={userFolder}");

        var setAside = new List<string>();
        var merged = new JsonObject();
        // Claude Code's own order: the user's file, the folder's shared file, its local file, then the --settings file over all.
        var sources = new List<string>
        {
            Path.Combine(userFolder, "settings.json"),
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

        var folders = FoldersUp(workingDirectory);
        var state = Read(stateFile);
        var servers = McpServers(folders, merged, state);

        var target = SettingsPathFor(sessionId, hooksDirectory);
        SessionHookFiles.WriteAtomic(target, merged.ToJsonString(Indented) + "\n");
        string? mcpConfig = null;
        if (servers.Count > 0)
        {
            mcpConfig = McpConfigPathFor(sessionId, hooksDirectory);
            SessionHookFiles.WriteAtomic(mcpConfig, new JsonObject { ["mcpServers"] = servers }.ToJsonString(Indented) + "\n");
        }
        var instructions = InstructionFolders(workingDirectory, userFolder);
        var skills = SkillFolders(workingDirectory, userFolder);
        FileLog.Write($"[ScheduledFactoryRunSettings] Compose: session={sessionId}, settings={target}, instructions={instructions.Count}, " +
                      $"skills={skills.Count}, servers={servers.Count}, setAside={setAside.Count}");
        return new ScheduledFactoryRunLaunch(target, instructions, skills, mcpConfig, setAside);
    }

    /// <summary>
    /// The Claude Code launch line of a scheduled factory run: <paramref name="args"/>, then <c>--setting-sources ""</c>,
    /// an <c>--add-dir</c> per instruction folder, the run's one <c>--settings</c>, a <c>--plugin-dir</c> per skill folder
    /// and one more when a seat has factory skills, and <c>--mcp-config</c> when the run has servers. Every folder flag is
    /// followed by another flag, so none takes a word meant for something else.
    /// </summary>
    public static string ClaudeArguments(string args, ScheduledFactoryRunLaunch launch, string? pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var line = $"{args} --setting-sources \"\"".Trim();
        foreach (var folder in launch.InstructionFolders)
            line += $" --add-dir \"{folder}\"";
        line += $" --settings \"{launch.SettingsPath}\"";
        foreach (var folder in launch.SkillFolders)
            line += $" --plugin-dir \"{folder}\"";
        if (pluginDirectory is not null)
            line += $" --plugin-dir \"{pluginDirectory}\"";
        if (launch.McpConfigPath is not null)
            line += $" --mcp-config \"{launch.McpConfigPath}\"";
        return line;
    }

    /// <summary>The launch log's line for what <see cref="Compose"/> did.</summary>
    public static string LogLine(ScheduledFactoryRunLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var rules = launch.RulesSetAside.Count == 0
            ? "it had no ask or deny rules to set aside"
            : $"{launch.RulesSetAside.Count} ask or deny rule(s) set aside: {string.Join("; ", launch.RulesSetAside)}";
        var skills = launch.SkillFolders.Count == 0 ? "none" : string.Join(", ", launch.SkillFolders);
        return "SCHEDULED FACTORY RUN: started without any settings file's ask and deny rules, so nothing waits on a person " +
               $"(--setting-sources \"\", the other settings in --settings \"{launch.SettingsPath}\", " +
               $"instructions from {string.Join(", ", launch.InstructionFolders)}, skills from {skills}, " +
               $"servers from {launch.McpConfigPath ?? "nowhere"}); {rules}.";
    }

    /// <summary>Remove a run's own files. A failure is logged and never thrown: the files are copies.</summary>
    public static void DeleteFor(Guid sessionId, string hooksDirectory)
    {
        foreach (var path in new[] { SettingsPathFor(sessionId, hooksDirectory), McpConfigPathFor(sessionId, hooksDirectory) })
        {
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
    }

    /// <summary>
    /// The working folder, then every folder above it that holds instructions Claude Code would read from it (a
    /// CLAUDE.md, a <c>.claude/CLAUDE.md</c> or a <c>.claude/rules</c> folder), never a drive's root; then the folder
    /// that holds the user's own <c>CLAUDE.md</c> and <c>rules</c> - the parent of the user's <c>.claude</c> folder, which
    /// Claude Code reads as an added folder's <c>.claude</c> - unless the walk already added it.
    /// </summary>
    internal static List<string> InstructionFolders(string workingDirectory, string userFolder)
    {
        var up = FoldersUp(workingDirectory);
        var folders = new List<string> { up[0] };
        folders.AddRange(up.Skip(1).Where(HoldsInstructions));
        var user = Trim(Path.GetFullPath(userFolder));
        // A configuration folder not named .claude (CLAUDE_CONFIG_DIR) is added itself: its CLAUDE.md is then read as the
        // added folder's own. Its rules folder is not, because Claude Code looks for an added folder's rules in .claude.
        var holder = string.Equals(Path.GetFileName(user), ".claude", PathComparison) ? Path.GetDirectoryName(user)! : user;
        var holds = File.Exists(Path.Combine(user, "CLAUDE.md")) || Directory.Exists(Path.Combine(user, "rules"));
        if (holds && !folders.Any(f => string.Equals(f, holder, PathComparison)))
            folders.Add(holder);
        return folders;
    }

    /// <summary>
    /// The user's configuration folder, then every <c>.claude</c> folder from the working folder up, never a drive's
    /// root's, that holds a <c>skills</c>, <c>commands</c> or <c>agents</c> folder. Claude Code loads each as a plugin.
    /// </summary>
    internal static List<string> SkillFolders(string workingDirectory, string userFolder)
    {
        var user = Trim(Path.GetFullPath(userFolder));
        var folders = new List<string>();
        if (HoldsSkills(user)) folders.Add(user);
        foreach (var dir in FoldersUp(workingDirectory))
        {
            var claude = Path.Combine(dir, ".claude");
            if (HoldsSkills(claude) && !folders.Any(f => string.Equals(f, claude, PathComparison)))
                folders.Add(claude);
        }
        return folders;
    }

    /// <summary>
    /// The servers an ordinary start would run, later entries winning a name: the user's (<c>mcpServers</c> in
    /// <c>.claude.json</c>), every <c>.mcp.json</c> from the farthest folder down to the working folder - less those a
    /// settings file or <c>.claude.json</c> names in <c>disabledMcpjsonServers</c> - and then the folder's own servers
    /// in <c>.claude.json</c>, which Claude Code files under the folder's repository root.
    /// </summary>
    private static JsonObject McpServers(List<string> foldersUp, JsonObject settings, JsonObject? state)
    {
        var servers = new JsonObject();
        if (state?["mcpServers"] is JsonObject user)
            MergeInto(servers, user);

        var projects = new List<JsonObject>();
        if (state?["projects"] is JsonObject known)
        {
            // Farthest first, so the entry nearest the working folder wins a name.
            foreach (var dir in Enumerable.Reverse(foldersUp))
            {
                var key = Normalise(dir);
                foreach (var (name, value) in known)
                    if (string.Equals(Normalise(name), key, PathComparison) && value is JsonObject project)
                        projects.Add(project);
            }
        }
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in projects.Select(p => p["disabledMcpjsonServers"]).Append(settings["disabledMcpjsonServers"]))
            if (list is JsonArray names)
                disabled.UnionWith(names.Select(n => n?.GetValue<string>()).OfType<string>());

        foreach (var dir in Enumerable.Reverse(foldersUp))
        {
            var file = Path.Combine(dir, ".mcp.json");
            if (Read(file)?["mcpServers"] is not JsonObject declared) continue;
            foreach (var (name, value) in declared)
                if (!disabled.Contains(name))
                    servers[name] = value?.DeepClone();
        }

        foreach (var project in projects)
            if (project["mcpServers"] is JsonObject own)
                foreach (var (name, value) in own)
                    servers[name] = value?.DeepClone();
        return servers;
    }

    /// <summary>The working folder and every folder above it, nearest first, never a drive's root.</summary>
    private static List<string> FoldersUp(string workingDirectory)
    {
        var folders = new List<string> { Trim(Path.GetFullPath(workingDirectory)) };
        for (var dir = Directory.GetParent(folders[0]); dir is not null && dir.Parent is not null; dir = dir.Parent)
            folders.Add(Trim(dir.FullName));
        return folders;
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // Claude Code files a folder in .claude.json under its path with forward slashes on Windows.
    private static string Normalise(string path) => Trim(path.Replace('\\', '/')).TrimEnd('/');

    private static bool HoldsInstructions(string dir)
        => File.Exists(Path.Combine(dir, "CLAUDE.md"))
           || File.Exists(Path.Combine(dir, ".claude", "CLAUDE.md"))
           || Directory.Exists(Path.Combine(dir, ".claude", "rules"));

    private static bool HoldsSkills(string claudeFolder)
        => Directory.Exists(Path.Combine(claudeFolder, "skills"))
           || Directory.Exists(Path.Combine(claudeFolder, "commands"))
           || Directory.Exists(Path.Combine(claudeFolder, "agents"));

    /// <summary>The file as Claude Code reads it - comments and trailing commas allowed - or null when it is missing.
    /// Opened for reading only, beside a Claude Code that may be rewriting it.</summary>
    private static JsonObject? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
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
