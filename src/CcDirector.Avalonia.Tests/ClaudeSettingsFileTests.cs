using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The settings dialog edits a few fields inside a file it does not own. Saving is a MERGE into
/// whatever else is in there - hooks above all, because on this fleet the hooks are what inject the
/// preamble that tells every session the rules it runs under.
///
/// The defect these tests exist for: the read returned null for "no file" AND for "there is a file
/// and I could not read it", and the save turned that null into a fresh empty object and wrote it.
/// A settings file locked for an instant by another process, or hand-edited into invalid JSON, was
/// replaced by only the handful of fields the dialog knows about. Everything else - hooks included -
/// was gone, and the window said "Saved".
///
/// Every test below writes a real file to a real temporary path, because the thing being asserted is
/// what is left ON DISK afterwards.
/// </summary>
public sealed class ClaudeSettingsFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ClaudeSettingsFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* the temp sweeper gets it */ }
    }

    private static ClaudeSettingsEdits Edits(string? mode = "plan") => new(
        PermissionMode: mode,
        Allow: new List<string> { "Bash(git status)" },
        Deny: new List<string>(),
        Model: null,
        EffortLevel: null,
        MaxOutputTokens: null,
        BashTimeoutMs: null,
        EnabledPlugins: new Dictionary<string, bool>());

    /// <summary>A settings file carrying a hook, exactly as Claude Code writes one.</summary>
    private const string WithAHook = """
    {
      "$schema": "https://json.schemastore.org/claude-code-settings.json",
      "hooks": {
        "SessionStart": [
          {
            "hooks": [
              { "type": "command", "command": "cc-director-preamble" }
            ]
          }
        ]
      },
      "mcpServers": { "vault": { "command": "cc-vault" } }
    }
    """;

    // ---- The defect itself -------------------------------------------------

    /// <summary>
    /// THE REGRESSION TEST. A settings file that is present but not parseable must not be written
    /// over. Before the fix this failed on the first assertion: the save merged into a fresh empty
    /// object and the hook was gone from disk.
    /// </summary>
    [Fact]
    public void Save_FileExistsButIsNotValidJson_RefusesAndLeavesTheHooksOnDisk()
    {
        // A half-written file - the shape a crash or a concurrent writer leaves behind.
        var truncated = WithAHook.Substring(0, WithAHook.Length / 2);
        File.WriteAllText(_path, truncated);

        var result = ClaudeSettingsFile.Save(_path, Edits());

        Assert.Equal(SettingsSaveKind.RefusedUnreadable, result.Kind);
        Assert.True(result.Refused);
        Assert.Contains("not valid JSON", result.Problem);

        // The file is byte-for-byte as it was found. Nothing was salvaged, nothing was destroyed.
        Assert.Equal(truncated, File.ReadAllText(_path));
    }

    /// <summary>
    /// The same refusal when the file cannot be OPENED rather than parsed - the transient case, and
    /// the one most likely to happen to a real person: another process holds it for an instant.
    /// </summary>
    [Fact]
    public void Save_FileIsLockedByAnotherProcess_RefusesAndLeavesTheHooksOnDisk()
    {
        File.WriteAllText(_path, WithAHook);

        using (var _ = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = ClaudeSettingsFile.Save(_path, Edits());

            Assert.Equal(SettingsSaveKind.RefusedUnreadable, result.Kind);
            Assert.Contains("could not be opened", result.Problem);
        }

        // Once the lock is gone the hook is still there, because the save never touched the file.
        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal(
            "cc-director-preamble",
            root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    /// <summary>
    /// A file holding valid JSON that is not an object - "null", an array, a number. There is content
    /// there, it cannot be merged into, and it must not be written over either. Deliberately NOT
    /// folded into Absent.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    public void Save_FileHoldsJsonThatIsNotAnObject_Refuses(string content)
    {
        File.WriteAllText(_path, content);

        var result = ClaudeSettingsFile.Save(_path, Edits());

        Assert.Equal(SettingsSaveKind.RefusedUnreadable, result.Kind);
        Assert.Equal(content, File.ReadAllText(_path));
    }

    // ---- The two answers that were always right, kept so the fix cannot over-correct ----

    /// <summary>
    /// A readable file is still merged into, and every field the dialog does not edit survives. This
    /// is the behaviour the refusal must not cost us: refusing to write ALWAYS would be just as wrong
    /// in the other direction.
    /// </summary>
    [Fact]
    public void Save_FileIsReadable_MergesAndPreservesEverythingItDoesNotEdit()
    {
        File.WriteAllText(_path, WithAHook);

        var result = ClaudeSettingsFile.Save(_path, Edits(mode: "acceptEdits"));

        Assert.Equal(SettingsSaveKind.Merged, result.Kind);

        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();

        // What it does not own, carried through untouched.
        Assert.Equal(
            "cc-director-preamble",
            root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("cc-vault", root["mcpServers"]!["vault"]!["command"]!.GetValue<string>());

        // What it does own, written.
        Assert.Equal("acceptEdits", root["permissions"]!["defaultMode"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", root["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    /// <summary>An absent file is genuinely absent and may be created - the third state has not eaten this one.</summary>
    [Fact]
    public void Save_NoFileAtAll_CreatesIt()
    {
        Assert.False(File.Exists(_path));

        var result = ClaudeSettingsFile.Save(_path, Edits());

        Assert.Equal(SettingsSaveKind.Created, result.Kind);
        Assert.Null(result.Problem);

        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal("plan", root["permissions"]!["defaultMode"]!.GetValue<string>());
    }

    /// <summary>The parent directory is created when the file is genuinely new.</summary>
    [Fact]
    public void Save_NoFileAndNoDirectory_CreatesBoth()
    {
        var nested = Path.Combine(_dir, "deeper", "settings.json");

        var result = ClaudeSettingsFile.Save(nested, Edits());

        Assert.Equal(SettingsSaveKind.Created, result.Kind);
        Assert.True(File.Exists(nested));
    }

    // ---- The read itself: three answers, never two -------------------------

    [Fact]
    public void Read_NoFile_IsAbsent()
    {
        var read = ClaudeSettingsFile.Read(_path);

        Assert.Equal(ConfigReadKind.Absent, read.Kind);
        Assert.Null(read.Root);
        Assert.Null(read.Problem);
    }

    [Fact]
    public void Read_GoodFile_IsLoadedAndCarriesTheContent()
    {
        File.WriteAllText(_path, WithAHook);

        var read = ClaudeSettingsFile.Read(_path);

        Assert.Equal(ConfigReadKind.Loaded, read.Kind);
        Assert.NotNull(read.Root);
        Assert.True(read.Root!.ContainsKey("hooks"));
    }

    /// <summary>
    /// The distinction the whole fix rests on: absent and unreadable are DIFFERENT answers. Before the
    /// fix both were a null and no caller could tell them apart.
    /// </summary>
    [Fact]
    public void Read_AbsentAndUnreadableAreDifferentAnswers()
    {
        var absent = ClaudeSettingsFile.Read(_path);

        File.WriteAllText(_path, "{ this is not json");
        var unreadable = ClaudeSettingsFile.Read(_path);

        Assert.NotEqual(absent.Kind, unreadable.Kind);
        Assert.Equal(ConfigReadKind.Absent, absent.Kind);
        Assert.Equal(ConfigReadKind.Unreadable, unreadable.Kind);
        Assert.NotNull(unreadable.Problem);
    }

    /// <summary>A read never throws - the failure is the return value, so no caller can skip handling it.</summary>
    [Fact]
    public void Read_UnreadableFile_DoesNotThrow()
    {
        File.WriteAllText(_path, "{ this is not json");

        var read = ClaudeSettingsFile.Read(_path);

        Assert.Equal(ConfigReadKind.Unreadable, read.Kind);
    }
}
