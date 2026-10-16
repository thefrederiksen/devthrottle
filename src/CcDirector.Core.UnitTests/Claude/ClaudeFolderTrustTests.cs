using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// MARKING A SEAT'S FOLDER TRUSTED IN CLAUDE CODE'S OWN RECORD (Factory Definition mission, phase 5, P7). Every case
/// writes a scratch configuration file, never the user's own. The lock is Claude Code's: a directory beside the file. A
/// fake clock decides a lock's age and how long a start has waited, and the pause between looks advances it, so no case
/// sleeps.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class ClaudeFolderTrustTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-claude-trust-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly List<TimeSpan> _pauses = new();

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Config => Path.Combine(_scratch, ".claude.json");

    private string Folder(string name) => Path.Combine(_scratch, name);

    private ClaudeFolderTrust Trust(Action? onPause = null) => new(Config, _clock, pause =>
    {
        _pauses.Add(pause);
        _clock.Advance(pause);
        onPause?.Invoke();
    });

    private const string OtherProjectKey = "D:/Somewhere/else";

    private void WriteConfig(string json) => File.WriteAllText(Config, json);

    private void WriteTypicalConfig() => WriteConfig(
        "{\n  \"numStartups\": 412,\n  \"userID\": \"abc\",\n  \"tipsHistory\": {\"x\": 3},\n  \"projects\": {\n" +
        $"    \"{OtherProjectKey}\": {{\"allowedTools\": [], \"hasTrustDialogAccepted\": true, \"lastCost\": 0.25}}\n  }}\n}}\n");

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Config))!;

    private JsonObject Projects() => (JsonObject)Read()["projects"]!;

    [Fact]
    public void Trust_AFolderNotRecorded_RecordsThatFolderAloneAndKeepsEveryOtherKeyAsItWas()
    {
        WriteTypicalConfig();
        var before = Read();
        var folder = Folder("factory-run");

        Trust().Trust(folder);

        var after = Read();
        var projects = (JsonObject)after["projects"]!;
        Assert.Equal(2, projects.Count);
        Assert.Equal("{\"hasTrustDialogAccepted\":true}", projects[ClaudeFolderTrust.KeyFor(folder)]!.ToJsonString());
        Assert.Equal(before["projects"]![OtherProjectKey]!.ToJsonString(), projects[OtherProjectKey]!.ToJsonString());
        foreach (var key in new[] { "numStartups", "userID", "tipsHistory" })
            Assert.Equal(before[key]!.ToJsonString(), after[key]!.ToJsonString());
    }

    [Fact]
    public void Trust_AnEntryThatIsNotTrusted_SetsOnlyItsFlagAndKeepsItsOtherFields()
    {
        var folder = Folder("slot");
        var key = ClaudeFolderTrust.KeyFor(folder);
        WriteConfig($"{{\"projects\":{{\"{key}\":{{\"hasTrustDialogAccepted\":false,\"lastSessionId\":\"s-1\"}}}}}}");

        Trust().Trust(folder);

        var entry = (JsonObject)Projects()[key]!;
        Assert.True(entry["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.Equal("s-1", entry["lastSessionId"]!.GetValue<string>());
    }

    [Fact]
    public void Trust_AFolderAlreadyTrusted_WritesNothingAndTakesNoLock()
    {
        var folder = Folder("slot");
        WriteConfig($"{{\"projects\":{{\"{ClaudeFolderTrust.KeyFor(folder)}\":{{\"hasTrustDialogAccepted\":true}}}}}}");
        var bytes = File.ReadAllBytes(Config);
        // A held lock would make any write wait; nothing waits, because nothing is written.
        Directory.CreateDirectory(Config + ".lock");
        Directory.SetLastWriteTimeUtc(Config + ".lock", _clock.GetUtcNow().UtcDateTime);

        Trust().Trust(folder);

        Assert.Equal(bytes, File.ReadAllBytes(Config));
        Assert.Empty(_pauses);
    }

    [Fact]
    public void Trust_AWrite_LeavesNoLockAndNoTemporaryFileBehind()
    {
        WriteTypicalConfig();

        Trust().Trust(Folder("factory-run"));

        Assert.False(Directory.Exists(Config + ".lock"));
        Assert.Equal(new[] { ".claude.json" }, Directory.GetFileSystemEntries(_scratch).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Trust_AWrite_IsTwoSpaceJsonWithUnixLineEndsAndUnescapedText_AsClaudeCodeWritesIt()
    {
        WriteConfig("{\"name\":\"S\u00f8ren <x>\",\"projects\":{}}");

        Trust().Trust(Folder("work"));

        var text = File.ReadAllText(Config);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\n  \"name\": \"S\u00f8ren <x>\",\n", text);
    }

    [Fact]
    public void Trust_AConfigWithNoProjectsEntry_GainsOneHoldingTheFolder()
    {
        WriteConfig("{\"numStartups\": 1}");
        var folder = Folder("work");

        Trust().Trust(folder);

        Assert.True(Projects()[ClaudeFolderTrust.KeyFor(folder)]!["hasTrustDialogAccepted"]!.GetValue<bool>());
    }

    [Fact]
    public void Trust_NoConfigFile_RefusesInWordsAndCreatesNothing()
    {
        var error = Assert.Throws<ClaudeFolderTrustException>(() => Trust().Trust(Folder("work")));

        Assert.Equal($"No seat was started: Claude Code has not been run on this machine as this user (there is no {Config}), " +
                     $"so the seat would stop at Claude Code's first questions in {Folder("work")} with nobody to answer them. " +
                     "Open Claude Code once on this machine, then start the seat again.", error.Message);
        Assert.False(File.Exists(Config));
    }

    [Fact]
    public void Trust_AConfigThatIsNotJson_RefusesInWordsAndLeavesTheFileAsItWas()
    {
        WriteConfig("{\"projects\": {");
        var bytes = File.ReadAllBytes(Config);

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Trust().Trust(Folder("work")));

        Assert.StartsWith($"No seat was started: the folder {Folder("work")} could not be marked trusted, because {Config} could not be read (it is not valid JSON: ", error.Message);
        Assert.EndsWith("and the seat would wait at Claude Code's folder-trust question with nobody to answer it. Fix that file, then start the seat again.", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void Trust_AProjectsEntryThatIsNotAnObject_RefusesInWords()
    {
        WriteConfig("{\"projects\": []}");

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Trust().Trust(Folder("work")));

        Assert.Contains("could not be read (its \"projects\" entry is not a JSON object)", error.Message);
    }

    [Fact]
    public void Trust_ALockAnotherProcessKeepsRenewing_RefusesInWordsAfterTenSecondsAndWritesNothing()
    {
        WriteTypicalConfig();
        var bytes = File.ReadAllBytes(Config);
        var lockDir = Config + ".lock";
        Directory.CreateDirectory(lockDir);
        void Renew() => Directory.SetLastWriteTimeUtc(lockDir, _clock.GetUtcNow().UtcDateTime);
        Renew();

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Trust(onPause: Renew).Trust(Folder("work")));

        Assert.Equal($"No seat was started: the folder {Folder("work")} could not be marked trusted, because the lock on {Config} " +
                     "that Claude Code takes to write it stayed in place for the 10 seconds this Director waited, and the seat " +
                     "would wait at Claude Code's folder-trust question with nobody to answer it. Start the seat again in a moment.",
            error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(Config));
        Assert.True(Directory.Exists(lockDir), "a lock another process holds is never removed");
        Assert.Equal(TimeSpan.FromSeconds(10), _pauses.Aggregate(TimeSpan.Zero, (a, b) => a + b));
    }

    [Fact]
    public void Trust_ALockReleasedWhileWaiting_IsTakenAndTheWriteGoesAhead()
    {
        WriteTypicalConfig();
        var lockDir = Config + ".lock";
        Directory.CreateDirectory(lockDir);
        Directory.SetLastWriteTimeUtc(lockDir, _clock.GetUtcNow().UtcDateTime);
        var folder = Folder("work");

        Trust(onPause: () => { if (Directory.Exists(lockDir)) Directory.Delete(lockDir); }).Trust(folder);

        Assert.Single(_pauses);
        Assert.True(Projects()[ClaudeFolderTrust.KeyFor(folder)]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.False(Directory.Exists(lockDir));
    }

    [Fact]
    public void Trust_ALockItDidNotMake_IsNeverRemovedHoweverOldAndTheStartRefuses()
    {
        WriteTypicalConfig();
        var bytes = File.ReadAllBytes(Config);
        var lockDir = Config + ".lock";
        Directory.CreateDirectory(lockDir);
        Directory.SetLastWriteTimeUtc(lockDir, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(3));

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Trust().Trust(Folder("work")));

        Assert.Contains("stayed in place for the 10 seconds this Director waited", error.Message);
        Assert.True(Directory.Exists(lockDir), "a lock this Director did not make is never removed");
        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void KeyFor_AWindowsOrUnixFolder_IsItsFullPathWithForwardSlashesAndNoTrailingSlash()
    {
        var folder = Folder("a");

        Assert.Equal(Path.GetFullPath(folder).Replace('\\', '/'), ClaudeFolderTrust.KeyFor(folder + Path.DirectorySeparatorChar));
        Assert.DoesNotContain("\\", ClaudeFolderTrust.KeyFor(folder));
    }

    [Fact]
    public void ConfigPathForThisUser_NoConfigDirectorySet_IsDotClaudeJsonInTheHomeFolder()
    {
        var home = Folder("home");

        Assert.Equal(Path.Combine(home, ".claude.json"), ClaudeFolderTrust.ConfigPathForThisUser(_ => null, home));
    }

    [Fact]
    public void ConfigPathForThisUser_ClaudeConfigDirSet_IsDotClaudeJsonInThatFolder()
    {
        var dir = Folder("cfg");

        var path = ClaudeFolderTrust.ConfigPathForThisUser(name => name == "CLAUDE_CONFIG_DIR" ? dir : null, Folder("home"));

        Assert.Equal(Path.Combine(dir, ".claude.json"), path);
    }

    [Fact]
    public void ConfigPathForThisUser_AnOlderConfigJsonThatIsNotEmpty_WinsAsItDoesForClaudeCode()
    {
        var home = Folder("home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", ".config.json"), "{}");

        Assert.Equal(Path.Combine(home, ".claude", ".config.json"), ClaudeFolderTrust.ConfigPathForThisUser(_ => null, home));
    }
}
