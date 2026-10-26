using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
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

        var launch = ScheduledFactoryRunSettings.Compose(id, folder, director, HooksDirectory, Home);

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

        ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home);

        Assert.Equal(before.Item1, File.ReadAllBytes(settings));
        Assert.Equal(before.Item2, File.ReadAllBytes(local));
    }

    [Fact]
    public void Compose_AFolderWithNoSettings_GivesTheDirectorsSettingsAlone()
    {
        var folder = Folder(settings: null);
        var director = DirectorSettings("{\"hooks\": {\"Stop\": []}}");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, director, HooksDirectory, Home);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"hooks\": {\"Stop\": []}}"), ReadJson(launch.SettingsPath)));
        Assert.Empty(launch.RulesSetAside);
    }

    [Fact]
    public void Compose_ReadsCommentsAndTrailingCommasAsClaudeCodeDoes()
    {
        var folder = Folder("{\n  // the seat's list\n  \"permissions\": {\"ask\": [\"Bash(echo *)\",],},\n  \"model\": \"haiku\",\n}\n");

        var launch = ScheduledFactoryRunSettings.Compose(Guid.NewGuid(), folder, null, HooksDirectory, Home);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"model\": \"haiku\"}"), ReadJson(launch.SettingsPath)));
        Assert.Single(launch.RulesSetAside);
    }

    [Fact]
    public void Compose_AFolderSettingsFileThatIsNotJson_StopsTheStartNamingTheFile()
    {
        var folder = Folder("{ \"permissions\": ");
        var id = Guid.NewGuid();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduledFactoryRunSettings.Compose(id, folder, null, HooksDirectory, Home));

        Assert.StartsWith($"No session was started: the scheduled factory run's settings file {Path.Combine(folder, ".claude", "settings.json")} is not JSON", ex.Message);
        Assert.False(File.Exists(ScheduledFactoryRunSettings.SettingsPathFor(id, HooksDirectory)));
    }

    // ---------- the folders it is pointed at ----------

    [Fact]
    public void InstructionFolders_TheWorkingFolderThenEachFolderAboveItWithInstructions_StoppingAtHome()
    {
        var home = Home;
        var repo = Directory.CreateDirectory(Path.Combine(home, "repos", "internal")).FullName;
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "# repo\n");
        var factory = Directory.CreateDirectory(Path.Combine(repo, "factory")).FullName;
        Directory.CreateDirectory(Path.Combine(factory, ".claude", "skills"));
        var plain = Directory.CreateDirectory(Path.Combine(factory, "plain")).FullName;
        var seat = Directory.CreateDirectory(Path.Combine(plain, "seat")).FullName;
        File.WriteAllText(Path.Combine(home, "CLAUDE.md"), "# the user's own, read anyway\n");

        var folders = ScheduledFactoryRunSettings.InstructionFolders(seat, home);

        Assert.Equal(new[] { seat, factory, repo }, folders);
    }

    // ---------- the launch line ----------

    [Fact]
    public void ClaudeArguments_LeavesTheFolderFilesOut_AddsEachFolderAndTheOneSettingsFile_ThenThePlugin()
    {
        var launch = new ScheduledFactoryRunLaunch(@"C:\hooks\run.json", new[] { @"D:\repo\seat", @"D:\repo" }, Array.Empty<string>());

        var line = ScheduledFactoryRunSettings.ClaudeArguments("--dangerously-skip-permissions", launch, @"C:\kit\claude-plugin");

        Assert.Equal("--dangerously-skip-permissions --setting-sources user --add-dir \"D:\\repo\\seat\" --add-dir \"D:\\repo\" " +
                     "--settings \"C:\\hooks\\run.json\" --plugin-dir \"C:\\kit\\claude-plugin\"", line);
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
    public void CreateSession_AScheduledFactoryRun_StartsWithoutTheFoldersAskAndDenyRules()
    {
        var folder = Folder();
        using var manager = Manager();

        var session = Create(manager, folder, SessionOriginKinds.Schedule, "marketing");

        var arguments = Backend(session).StartedArguments;
        var own = ScheduledFactoryRunSettings.SettingsPathFor(session.Id, HooksDirectory);
        Assert.Contains("--setting-sources user", arguments);
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

        manager.RemoveSession(session.Id);
        Assert.False(File.Exists(own));
    }

    [Theory]
    [InlineData(SessionOriginKinds.Human, "marketing")]
    [InlineData(SessionOriginKinds.Agent, "marketing")]
    [InlineData(SessionOriginKinds.Schedule, null)]
    public void CreateSession_AnyOtherSession_KeepsTodaysLineAndTheFoldersRules(string origin, string? factory)
    {
        var folder = Folder();
        using var manager = Manager();

        var session = Create(manager, folder, origin, factory);

        var arguments = Backend(session).StartedArguments;
        Assert.DoesNotContain("--setting-sources", arguments);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.Contains($"--settings \"{Path.Combine(HooksDirectory, ClaudeHookInstaller.SharedSettingsFileName)}\"", arguments);
        Assert.False(Backend(session).StartedEnvironment!.ContainsKey(ScheduledFactoryRunSettings.InstructionsEnvVar));
        Assert.False(File.Exists(ScheduledFactoryRunSettings.SettingsPathFor(session.Id, HooksDirectory)));
    }
}
