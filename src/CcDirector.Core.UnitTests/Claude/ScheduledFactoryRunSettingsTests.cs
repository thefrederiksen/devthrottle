using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnixPty;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// A SCHEDULED FACTORY RUN NEVER PROMPTS (the owner, 26 October 2026). The pure parts - which session is one, the run's
/// own settings file, the folders it is pointed at, the launch line - and then the real create on the Studio backend,
/// whose start runs no process: a scheduled factory run is started without the folder's settings files and with
/// everything in them but their ask and deny rules; a session a person opens, a session an agent opens and a schedule
/// that is not a factory's get exactly today's line. Every file is in a scratch folder.
/// </summary>
public sealed class ScheduledFactoryRunSettingsTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-scheduled-run-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string HooksDirectory => Path.Combine(_scratch, "claude-hooks");

    private string Home => Directory.CreateDirectory(Path.Combine(_scratch, "home")).FullName;

    private const string FolderSettings =
        "{\n  \"env\": {\"FACTORY_MODE\": \"nightly\"},\n  \"model\": \"sonnet\",\n" +
        "  \"permissions\": {\n    \"allow\": [\"Bash(git status)\"],\n    \"ask\": [\"Bash(echo *)\", \"Bash(git push:*)\"],\n" +
        "    \"deny\": [\"Read(./secret.txt)\"],\n    \"defaultMode\": \"bypassPermissions\"\n  },\n" +
        "  \"hooks\": {\"Stop\": [{\"hooks\": [{\"type\": \"command\", \"command\": \"python .claude/stop.py\"}]}]}\n}\n";

    private string Folder(string? settings = FolderSettings, string? local = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_scratch, "repo", "factory", "seat")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, ".claude"));
        if (settings is not null) File.WriteAllText(Path.Combine(folder, ".claude", "settings.json"), settings);
        if (local is not null) File.WriteAllText(Path.Combine(folder, ".claude", "settings.local.json"), local);
        return folder;
    }

    private string DirectorSettings(string text)
    {
        Directory.CreateDirectory(HooksDirectory);
        var path = Path.Combine(HooksDirectory, "settings.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static string? NoEnvironment(string name) => null;

    /// <summary>The user's own configuration folder in the scratch home, with its settings file when one is given.</summary>
    private string UserFolder(string? settings = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Home, ".claude")).FullName;
        if (settings is not null) File.WriteAllText(Path.Combine(folder, "settings.json"), settings);
        return folder;
    }

    // The walk goes to the drive's root, so a test folder's real ancestors (a TEMP inside the user's real home) can add
    // folders of their own. Each assertion is about the scratch folder only.
    private List<string> InScratch(IEnumerable<string> folders)
        => folders.Where(f => f.StartsWith(_scratch, StringComparison.Ordinal)).ToList();

    // ---------- which session is one ----------

    [Theory]
    [InlineData("schedule", "marketing", true)]
    [InlineData("schedule", null, false)]
    [InlineData("schedule", " ", false)]
    [InlineData("human", "marketing", false)]
    [InlineData("agent", "marketing", false)]
    [InlineData("unknown", "marketing", false)]
    [InlineData(null, "marketing", false)]
    public void IsScheduledFactoryRun_OnlyAScheduleInAFactory(string? origin, string? factory, bool expected)
        => Assert.Equal(expected, ScheduledFactoryRunSettings.IsScheduledFactoryRun(origin, factory));

    // ---------- the run's own settings file ----------

    [Fact]
    public void Compose_KeepsEverythingButAskAndDeny_FromTheFolderTheLocalFileAndTheDirector()
    {
        var folder = Folder(local: "{\"permissions\": {\"ask\": [\"Bash(rm *)\"], \"allow\": [\"Bash(ls)\"]}, \"env\": {\"LOCAL\": \"1\"}}");
        var director = DirectorSettings(
            "{\"hooks\": {\"Stop\": [{\"hooks\": [{\"type\": \"command\", \"command\": \"director-stop\"}]}]}, " +
            "\"permissions\": {\"ask\": [\"Bash(git tag:*)\"]}}");
        var id = Guid.NewGuid();

        var launch = ScheduledFactoryRunSettings.Compose(id, folder, director, HooksDirectory, Home, NoEnvironment);

        Assert.Equal(ScheduledFactoryRunSettings.SettingsPathFor(id, HooksDirectory), launch.SettingsPath);
        var written = ReadJson(launch.SettingsPath);
        var expected = JsonNode.Parse(
            "{\"env\": {\"FACTORY_MODE\": \"nightly\", \"LOCAL\": \"1\"}, \"model\": \"sonnet\", " +
            "\"permissions\": {\"allow\": [\"Bash(git status)\", \"Bash(ls)\"], \"defaultMode\": \"bypassPermissions\"}, " +
            "\"hooks\": {\"Stop\": [{\"hooks\": [{\"type\": \"command\", \"command\": \"python .claude/stop.py\"}]}, " +
            "{\"hooks\": [{\"type\": \"command\", \"command\": \"director-stop\"}]}]}}");
        Assert.True(JsonNode.DeepEquals(expected, written), written.ToJsonString());
        var settings = Path.Combine(folder, ".claude", "settings.json");
        var local = Path.Combine(folder, ".claude", "settings.local.json");
        Assert.Equal(new[]
        {
            $"{settings}: ask \"Bash(echo *)\"", $"{settings}: ask \"Bash(git push:*)\"", $"{settings}: deny \"Read(./secret.txt)\"",
            $"{local}: ask \"Bash(rm *)\"", $"{director}: ask \"Bash(git tag:*)\"",
        }, launch.RulesSetAside);
    }

    [Fact]
    public void Compose_NeverChangesTheFoldersOwnFiles()
    {
        var folder = Folder(local: "{\"permissions\": {\"ask\": [\"Bash(rm *)\"]}}");
        var settings = Path.Combine(folder, ".claude", "settings.json");
        var local = Path.Combine(folder, ".claude", "settings.local.json");
        var before = (File.ReadAllBytes(settings), File.ReadAllBytes(local));

        ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        Assert.Equal(before.Item1, File.ReadAllBytes(settings));
        Assert.Equal(before.Item2, File.ReadAllBytes(local));
    }

    [Fact]
    public void Compose_AFolderWithNoSettings_GivesTheDirectorsSettingsAlone()
    {
        var folder = Folder(settings: null);
        var director = DirectorSettings("{\"hooks\": {\"Stop\": []}}");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, director, HooksDirectory, Home, NoEnvironment);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"hooks\": {\"Stop\": []}}"), ReadJson(launch.SettingsPath)));
        Assert.Empty(launch.RulesSetAside);
    }

    [Fact]
    public void Compose_ReadsCommentsAndTrailingCommasAsClaudeCodeDoes()
    {
        var folder = Folder("{\n  // the seat's list\n  \"permissions\": {\"ask\": [\"Bash(echo *)\",],},\n  \"model\": \"haiku\",\n}\n");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"model\": \"haiku\"}"), ReadJson(launch.SettingsPath)));
        Assert.Single(launch.RulesSetAside);
    }

    [Fact]
    public void Compose_AFolderSettingsFileThatIsNotJson_StopsTheStartNamingTheFile()
    {
        var folder = Folder("{ \"permissions\": ");
        var id = Guid.NewGuid();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduledFactoryRunSettings.Compose(id, folder, null, HooksDirectory, Home, NoEnvironment));

        Assert.StartsWith($"No session was started: the scheduled factory run's settings file {Path.Combine(folder, ".claude", "settings.json")} is not JSON", ex.Message);
        Assert.False(File.Exists(ScheduledFactoryRunSettings.SettingsPathFor(id, HooksDirectory)));
    }

    // ---------- the user's own settings (review 2, finding 1) ----------

    [Fact]
    public void Compose_TheUsersOwnAskAndDeny_AreSetAsideToo_AndEverythingElseOfTheirsKept()
    {
        var user = UserFolder("{\"permissions\": {\"ask\": [\"Bash(git push:*)\"], \"deny\": [\"WebFetch\"], \"allow\": [\"Bash(ls)\"]}, " +
                              "\"env\": {\"USER_LEVEL\": \"1\"}, \"enabledPlugins\": {\"pp@market\": true}, \"model\": \"opus\"}");
        var folder = Folder();

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        var written = ReadJson(launch.SettingsPath);
        Assert.Null(written["permissions"]!["ask"]);
        Assert.Null(written["permissions"]!["deny"]);
        Assert.Equal("1", written["env"]!["USER_LEVEL"]!.GetValue<string>());
        Assert.True(written["enabledPlugins"]!["pp@market"]!.GetValue<bool>());
        // The folder's value wins over the user's, as in Claude Code.
        Assert.Equal("sonnet", written["model"]!.GetValue<string>());
        Assert.Equal(new[] { "Bash(ls)", "Bash(git status)" }, written["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()));
        var userSettings = Path.Combine(user, "settings.json");
        Assert.Equal(new[] { $"{userSettings}: ask \"Bash(git push:*)\"", $"{userSettings}: deny \"WebFetch\"" }, launch.RulesSetAside.Take(2));
    }

    [Fact]
    public void Compose_ConfigFolderMovedByTheEnvironment_ReadsTheUsersSettingsAndStateFromThere()
    {
        var config = Directory.CreateDirectory(Path.Combine(_scratch, "elsewhere", "claude-config")).FullName;
        File.WriteAllText(Path.Combine(config, "settings.json"), "{\"permissions\": {\"ask\": [\"Bash(echo *)\"]}}");
        File.WriteAllText(Path.Combine(config, ".claude.json"), "{\"mcpServers\": {\"moved\": {\"command\": \"m\"}}}");
        File.WriteAllText(Path.Combine(Home, ".claude.json"), "{\"mcpServers\": {\"home\": {\"command\": \"h\"}}}");
        var folder = Folder(settings: null);

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home,
            name => name == "CLAUDE_CONFIG_DIR" ? config : null);

        Assert.Equal(new[] { $"{Path.Combine(config, "settings.json")}: ask \"Bash(echo *)\"" }, launch.RulesSetAside);
        var servers = ReadJson(launch.McpConfigPath!)["mcpServers"]!.AsObject();
        Assert.Equal(new[] { "moved" }, servers.Select(p => p.Key));
    }

    // ---------- a custom output style (review 2b) ----------

    private static void Style(string claudeFolder, string file, string? frontMatterName)
    {
        Directory.CreateDirectory(Path.Combine(claudeFolder, "output-styles"));
        var head = frontMatterName is null ? "" : $"---\nname: {frontMatterName}\ndescription: d\n---\n";
        File.WriteAllText(Path.Combine(claudeFolder, "output-styles", file), head + "Be brief.\n");
    }

    [Fact]
    public void Compose_AUsersCustomOutputStyle_IsNamedByThePluginThatLoadsIt()
    {
        var user = UserFolder("{\"outputStyle\": \"probe-style\"}");
        Style(user, "probe.md", "probe-style");
        var folder = Folder(settings: null);

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        Assert.Equal(".claude:probe-style", ReadJson(launch.SettingsPath)["outputStyle"]!.GetValue<string>());
        Assert.Contains(user, launch.SkillFolders);
    }

    [Fact]
    public void Compose_AStyleInTheFolderAndTheUsers_TheFoldersWins()
    {
        var user = UserFolder("{\"outputStyle\": \"house\"}");
        Style(user, "house.md", null);
        var folder = Folder(settings: null);
        var folderClaude = Path.Combine(folder, ".claude");
        Style(folderClaude, "house.md", null);

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        Assert.Equal(".claude:house", ReadJson(launch.SettingsPath)["outputStyle"]!.GetValue<string>());
        Assert.Contains(folderClaude, launch.SkillFolders);
    }

    [Fact]
    public void Compose_ABuiltInOutputStyle_IsLeftAsItIs()
    {
        UserFolder("{\"outputStyle\": \"Explanatory\"}");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), Folder(settings: null), null, HooksDirectory, Home, NoEnvironment);

        Assert.Equal("Explanatory", ReadJson(launch.SettingsPath)["outputStyle"]!.GetValue<string>());
    }

    // ---------- the servers it is given ----------

    [Fact]
    public void Compose_TheServersAnOrdinaryStartRuns_UserThenEachMcpJsonThenTheFoldersOwn_LessTheDisabled()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_scratch, "repo")).FullName;
        File.WriteAllText(Path.Combine(repo, ".mcp.json"),
            "{\"mcpServers\": {\"shared\": {\"command\": \"repo\"}, \"off\": {\"command\": \"off\"}, \"parent-only\": {\"command\": \"p\"}}}");
        var folder = Folder("{\"disabledMcpjsonServers\": [\"off\"]}");
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), "{\"mcpServers\": {\"shared\": {\"command\": \"seat\"}}}");
        var repoKey = repo.Replace('\\', '/');
        File.WriteAllText(Path.Combine(Home, ".claude.json"),
            "{\"mcpServers\": {\"user\": {\"command\": \"u\"}, \"shared\": {\"command\": \"user\"}}, " +
            $"\"projects\": {{\"{repoKey}\": {{\"mcpServers\": {{\"local\": {{\"command\": \"l\"}}}}}}}}}}");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home, NoEnvironment);

        var servers = ReadJson(launch.McpConfigPath!)["mcpServers"]!.AsObject();
        Assert.Equal(new[] { "local", "parent-only", "shared", "user" }, servers.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("seat", servers["shared"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Compose_NoServerAnywhere_WritesNoServerList()
    {
        var id = Guid.NewGuid();

        var launch = ScheduledFactoryRunSettings.Compose(id, Folder(), null, HooksDirectory, Home, NoEnvironment);

        Assert.Null(launch.McpConfigPath);
        Assert.False(File.Exists(ScheduledFactoryRunSettings.McpConfigPathFor(id, HooksDirectory)));
    }

    [Fact]
    public void Compose_AStateFileThatIsNotJson_StopsTheStartNamingTheFile()
    {
        File.WriteAllText(Path.Combine(Home, ".claude.json"), "{ \"mcpServers\": ");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), Folder(), null, HooksDirectory, Home, NoEnvironment));

        Assert.Contains(Path.Combine(Home, ".claude.json"), ex.Message);
    }

    // ---------- the folders it is pointed at ----------

    [Fact]
    public void InstructionFolders_TheWorkingFolderThenEachFolderAboveItWithInstructions_ThenTheUsersOwn()
    {
        var home = Home;
        var user = UserFolder();
        File.WriteAllText(Path.Combine(user, "CLAUDE.md"), "# the user's own\n");
        var repo = Directory.CreateDirectory(Path.Combine(_scratch, "repos", "internal")).FullName;
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "# repo\n");
        var factory = Directory.CreateDirectory(Path.Combine(repo, "factory")).FullName;
        Directory.CreateDirectory(Path.Combine(factory, ".claude", "rules"));
        var skillsOnly = Directory.CreateDirectory(Path.Combine(factory, "skills-only")).FullName;
        Directory.CreateDirectory(Path.Combine(skillsOnly, ".claude", "skills"));
        var seat = Directory.CreateDirectory(Path.Combine(skillsOnly, "seat")).FullName;

        var folders = ScheduledFactoryRunSettings.InstructionFolders(seat, user);

        // A folder with only skills is not one: its skills come in as a plugin. The user's CLAUDE.md is read through
        // the folder that holds the user's .claude.
        Assert.Equal(new[] { seat, factory, repo, home }, InScratch(folders));
    }

    [Fact]
    public void InstructionFolders_AUserWithNoInstructions_AddsNoUserFolder()
    {
        var home = Home;
        var seat = Folder();

        var folders = ScheduledFactoryRunSettings.InstructionFolders(seat, UserFolder());

        Assert.Equal(seat, folders[0]);
        Assert.DoesNotContain(home, folders);
    }

    [Fact]
    public void InstructionFolders_AConfigFolderNotNamedClaude_IsAddedItself()
    {
        var config = Directory.CreateDirectory(Path.Combine(_scratch, "elsewhere", "claude-config")).FullName;
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "# moved\n");
        var seat = Folder();

        var folders = ScheduledFactoryRunSettings.InstructionFolders(seat, config);

        Assert.Contains(config, folders);
    }

    // ---------- the skills it loads (review 2, finding 2) ----------

    [Fact]
    public void SkillFolders_TheUsersThenEachClaudeFolderFromTheWorkingFolderUp_ThatHoldsSkillsCommandsOrAgents()
    {
        var user = UserFolder();
        Directory.CreateDirectory(Path.Combine(user, "skills", "fleet"));
        var repo = Directory.CreateDirectory(Path.Combine(_scratch, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".claude", "skills", "website-ceo"));
        var factory = Directory.CreateDirectory(Path.Combine(repo, "factory")).FullName;
        Directory.CreateDirectory(Path.Combine(factory, ".claude", "agents"));
        var plain = Directory.CreateDirectory(Path.Combine(factory, "plain")).FullName;
        Directory.CreateDirectory(Path.Combine(plain, ".claude", "rules"));
        var seat = Directory.CreateDirectory(Path.Combine(plain, "seat")).FullName;
        Directory.CreateDirectory(Path.Combine(seat, ".claude", "commands"));

        var folders = ScheduledFactoryRunSettings.SkillFolders(seat, user);

        Assert.Equal(new[]
        {
            user, Path.Combine(seat, ".claude"), Path.Combine(factory, ".claude"), Path.Combine(repo, ".claude"),
        }, InScratch(folders));
    }

    [Fact]
    public void SkillFolders_TheUsersFolderMetAgainOnTheWayUp_IsLoadedOnce()
    {
        var user = UserFolder();
        Directory.CreateDirectory(Path.Combine(user, "skills", "fleet"));
        var seat = Directory.CreateDirectory(Path.Combine(Home, "repo", "seat")).FullName;

        var folders = ScheduledFactoryRunSettings.SkillFolders(seat, user);

        Assert.Single(folders, f => f == user);
    }

    // ---------- the launch line ----------

    [Fact]
    public void ClaudeArguments_LoadsNoSettingsFile_AddsEachFolderTheOneSettingsFileThePluginsAndTheServers()
    {
        var launch = new ScheduledFactoryRunLaunch(@"C:\hooks\run.json", new[] { @"D:\repo\seat", @"D:\repo", @"C:\Users\owner" },
            new[] { @"C:\Users\owner\.claude", @"D:\repo\.claude" }, @"C:\hooks\run.mcp.json", Array.Empty<string>());

        var line = ScheduledFactoryRunSettings.ClaudeArguments("--dangerously-skip-permissions", launch, @"C:\kit\claude-plugin");

        Assert.Equal("--dangerously-skip-permissions --setting-sources= --add-dir \"D:\\repo\\seat\" --add-dir \"D:\\repo\" " +
                     "--add-dir \"C:\\Users\\owner\" --settings \"C:\\hooks\\run.json\" --plugin-dir \"C:\\Users\\owner\\.claude\" " +
                     "--plugin-dir \"D:\\repo\\.claude\" --plugin-dir \"C:\\kit\\claude-plugin\" --mcp-config \"C:\\hooks\\run.mcp.json\"", line);
    }

    [Fact]
    public void ClaudeArguments_NoServers_AddsNoServerList()
    {
        var launch = new ScheduledFactoryRunLaunch("/hooks/run.json", new[] { "/repo/seat" }, Array.Empty<string>(), null, Array.Empty<string>());

        var line = ScheduledFactoryRunSettings.ClaudeArguments("--dangerously-skip-permissions", launch, null);

        Assert.Equal("--dangerously-skip-permissions --setting-sources= --add-dir \"/repo/seat\" --settings \"/hooks/run.json\"", line);
    }

    [Fact]
    public void ClaudeArguments_OnLinuxAndMacOS_TheEmptySourceListReachesClaudeCodeAsOneWord()
    {
        var launch = new ScheduledFactoryRunLaunch("/hooks/run.json", new[] { "/repo/seat" }, Array.Empty<string>(), null, Array.Empty<string>());

        var argv = UnixProcessHost.TokenizeArgs(ScheduledFactoryRunSettings.ClaudeArguments("--dangerously-skip-permissions", launch, null));

        Assert.Equal(new[] { "--dangerously-skip-permissions", "--setting-sources=", "--add-dir", "/repo/seat", "--settings", "/hooks/run.json" }, argv);
    }

    // ---------- through the real create ----------

    private SessionManager Manager() => new(new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 })
    {
        ClaudeHooksDirectory = HooksDirectory,
        SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
        SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
        SessionRulesDirectory = Path.Combine(_scratch, "session-rules"),
        FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
        FactoryMemoryDownload = (_, _) => Array.Empty<FactoryMemoryNoteDto>(),
        MachineSettingsHome = Home,
        MachineSettingsEnvironment = _ => null,
    };

    private string AgentExe()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    private Session Create(SessionManager manager, string folder, string origin, string? factory)
        => manager.CreateSession(folder,
            new ClaudeAgent(new AgentOptions { ClaudePath = AgentExe(), DefaultClaudeArgs = "--dangerously-skip-permissions" }),
            userArgs: null, SessionBackendType.Studio, resumeSessionId: null,
            beforeLaunch: s =>
            {
                s.StampOrigin(SessionOrigin.Compose(origin, null, null));
                s.StampFactory(factory);
            });

    private static StudioBackend Backend(Session session) => Assert.IsType<StudioBackend>(session.Backend);

    [Fact]
    public void CreateSession_AScheduledFactoryRun_StartsWithoutAnyAskOrDenyRule()
    {
        var folder = Folder();
        UserFolder("{\"permissions\": {\"ask\": [\"Bash(curl *)\"]}}");
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), "{\"mcpServers\": {\"seat\": {\"command\": \"s\"}}}");
        using var manager = Manager();

        var session = Create(manager, folder, SessionOriginKinds.Schedule, "marketing");

        var arguments = Backend(session).StartedArguments;
        var own = ScheduledFactoryRunSettings.SettingsPathFor(session.Id, HooksDirectory);
        Assert.Contains("--setting-sources=", arguments);
        Assert.Contains($"--add-dir \"{folder}\"", arguments);
        Assert.Contains($"--settings \"{own}\"", arguments);
        Assert.Equal(1, arguments.Split("--settings ").Length - 1);
        Assert.Equal("1", Backend(session).StartedEnvironment![ScheduledFactoryRunSettings.InstructionsEnvVar]);
        var written = ReadJson(own);
        Assert.Null(written["permissions"]!["ask"]);
        Assert.Null(written["permissions"]!["deny"]);
        Assert.Equal("sonnet", written["model"]!.GetValue<string>());
        // The Director's own hooks are still there beside the folder's.
        Assert.NotNull(written["hooks"]!["PreToolUse"]);
        Assert.NotNull(written["hooks"]!["Stop"]);

        var servers = ScheduledFactoryRunSettings.McpConfigPathFor(session.Id, HooksDirectory);
        Assert.Contains($"--mcp-config \"{servers}\"", arguments);

        manager.RemoveSession(session.Id);
        Assert.False(File.Exists(own));
        Assert.False(File.Exists(servers));
    }

    [Theory]
    [InlineData(SessionOriginKinds.Human, "marketing")]
    [InlineData(SessionOriginKinds.Agent, "marketing")]
    [InlineData(SessionOriginKinds.Schedule, null)]
    public void CreateSession_AnyOtherSession_KeepsTodaysLineAndTheFoldersRules(string origin, string? factory)
    {
        var folder = Folder();
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), "{\"mcpServers\": {\"seat\": {\"command\": \"s\"}}}");
        using var manager = Manager();

        var session = Create(manager, folder, origin, factory);

        var arguments = Backend(session).StartedArguments;
        Assert.DoesNotContain("--setting-sources", arguments);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.Contains($"--settings \"{Path.Combine(HooksDirectory, ClaudeHookInstaller.SharedSettingsFileName)}\"", arguments);
        Assert.False(Backend(session).StartedEnvironment!.ContainsKey(ScheduledFactoryRunSettings.InstructionsEnvVar));
        Assert.False(File.Exists(ScheduledFactoryRunSettings.SettingsPathFor(session.Id, HooksDirectory)));
    }
}
