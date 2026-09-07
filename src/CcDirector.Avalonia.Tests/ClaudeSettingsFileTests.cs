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

    // ---- The merge contract, field by field --------------------------------

    /// <summary>
    /// Every field the dialog owns, all set at once, each asserted by name and value - and every
    /// field it does NOT own asserted to survive. Without this the suite stayed green if the $schema
    /// URL, any of the four environment variable names, the deny list, the plugin map or the
    /// empty-env removal were wrong, because the shared edits helper left them all empty.
    /// </summary>
    [Fact]
    public void Save_EveryEditedFieldIsWritten_AndEveryUnknownFieldSurvives()
    {
        File.WriteAllText(_path, """
        {
          "hooks": { "SessionStart": [ { "hooks": [ { "command": "cc-director-preamble" } ] } ] },
          "mcpServers": { "vault": { "command": "cc-vault" } },
          "statusLine": { "type": "command", "command": "my-status" },
          "permissions": { "additionalDirectories": ["/srv"] },
          "env": { "SOMETHING_ELSE": "keep me" }
        }
        """);

        var result = ClaudeSettingsFile.Save(_path, new ClaudeSettingsEdits(
            PermissionMode: "bypassPermissions",
            Allow: new List<string> { "Bash(git status)", "Read(*)" },
            Deny: new List<string> { "Bash(rm -rf *)" },
            Model: "  claude-opus-5  ",
            EffortLevel: "high",
            MaxOutputTokens: "8192",
            BashTimeoutMs: "600000",
            EnabledPlugins: new Dictionary<string, bool> { ["a@repo"] = true, ["b@repo"] = false }));

        Assert.Equal(SettingsSaveKind.Merged, result.Kind);
        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();

        Assert.Equal("https://json.schemastore.org/claude-code-settings.json", root["$schema"]!.GetValue<string>());

        var perms = root["permissions"]!.AsObject();
        Assert.Equal("bypassPermissions", perms["defaultMode"]!.GetValue<string>());
        Assert.Equal(new[] { "Bash(git status)", "Read(*)" },
            perms["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "Bash(rm -rf *)" },
            perms["deny"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("/srv", perms["additionalDirectories"]![0]!.GetValue<string>());

        var env = root["env"]!.AsObject();
        Assert.Equal("claude-opus-5", env["ANTHROPIC_MODEL"]!.GetValue<string>());   // trimmed
        Assert.Equal("high", env["CLAUDE_CODE_EFFORT_LEVEL"]!.GetValue<string>());
        Assert.Equal("8192", env["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]!.GetValue<string>());
        Assert.Equal("600000", env["BASH_DEFAULT_TIMEOUT_MS"]!.GetValue<string>());
        Assert.Equal("keep me", env["SOMETHING_ELSE"]!.GetValue<string>());

        var plugins = root["enabledPlugins"]!.AsObject();
        Assert.True(plugins["a@repo"]!.GetValue<bool>());
        Assert.False(plugins["b@repo"]!.GetValue<bool>());

        Assert.Equal("cc-director-preamble",
            root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("cc-vault", root["mcpServers"]!["vault"]!["command"]!.GetValue<string>());
        Assert.Equal("my-status", root["statusLine"]!["command"]!.GetValue<string>());
    }

    /// <summary>Blank edits REMOVE their environment variables, and an env left empty is removed entirely.</summary>
    [Fact]
    public void Save_BlankEnvEdits_RemoveTheVariablesAndThenTheEnvObject()
    {
        File.WriteAllText(_path, """
        { "env": { "ANTHROPIC_MODEL": "old", "CLAUDE_CODE_EFFORT_LEVEL": "low",
                   "CLAUDE_CODE_MAX_OUTPUT_TOKENS": "1", "BASH_DEFAULT_TIMEOUT_MS": "2" } }
        """);

        ClaudeSettingsFile.Save(_path, Edits());

        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.False(root.ContainsKey("env"));
    }

    /// <summary>A variable this dialog does not own keeps the env object alive when ours are cleared.</summary>
    [Fact]
    public void Save_BlankEnvEdits_KeepEnvWhenSomebodyElsesVariableIsThere()
    {
        File.WriteAllText(_path, """
        { "env": { "ANTHROPIC_MODEL": "old", "SOMETHING_ELSE": "keep me" } }
        """);

        ClaudeSettingsFile.Save(_path, Edits());

        var env = JsonNode.Parse(File.ReadAllText(_path))!.AsObject()["env"]!.AsObject();
        Assert.False(env.ContainsKey("ANTHROPIC_MODEL"));
        Assert.Equal("keep me", env["SOMETHING_ELSE"]!.GetValue<string>());
    }

    // ---- States found by re-reading this diff for the same defect it fixes ----

    /// <summary>
    /// ABSENT IS REACHED ONLY BY PROVEN ABSENCE. The reader used to ask File.Exists first and treat
    /// false as "no file", but .NET returns false from that probe when the path cannot be PROBED -
    /// permissions among them - so an existing file the user could not open answered Absent and was
    /// replaced. The refusal was bypassed by the most likely reason a file is unreadable.
    ///
    /// WHAT THIS TEST DOES NOT PROVE, checked by mutation rather than assumed: reverting the reader
    /// to the File.Exists-first form leaves this test PASSING, because a locked file still answers
    /// File.Exists true and both versions then fail on the open. The mutation is caught instead by
    /// the two directory tests below, which is the discriminating case reachable without touching
    /// access-control lists. The permission-denied half - where File.Exists itself answers false for
    /// a file that exists - rests on the .NET documentation and on attempting the read FIRST, and is
    /// not covered by any test here; arranging it means denying directory traversal, which leaves
    /// undeletable fixtures behind. This test is kept because it pins the locked-file answer, not
    /// because it proves the probe change.
    /// </summary>
    [Fact]
    public void Read_FileExistsButCannotBeOpened_IsNeverAbsent()
    {
        File.WriteAllText(_path, WithAHook);

        using var _ = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        var read = ClaudeSettingsFile.Read(_path);

        Assert.NotEqual(ConfigReadKind.Absent, read.Kind);
        Assert.Equal(ConfigReadKind.Unreadable, read.Kind);
    }

    /// <summary>
    /// A DIRECTORY at the settings path. File.Exists answers false for one, which folded it into
    /// Absent - the permissive branch, the one that says "go ahead and create" - and the create then
    /// threw out of a button click that has nowhere to put an exception. Something is at that path,
    /// so the answer is Unreadable, not Absent.
    /// </summary>
    [Fact]
    public void Read_PathIsADirectory_IsUnreadableNotAbsent()
    {
        var asDirectory = Path.Combine(_dir, "settings-as-a-directory.json");
        Directory.CreateDirectory(asDirectory);

        var read = ClaudeSettingsFile.Read(asDirectory);

        Assert.Equal(ConfigReadKind.Unreadable, read.Kind);
        Assert.Contains("is a directory", read.Problem);
    }

    /// <summary>And a save against it refuses rather than throwing.</summary>
    [Fact]
    public void Save_PathIsADirectory_RefusesInsteadOfThrowing()
    {
        var asDirectory = Path.Combine(_dir, "settings-as-a-directory.json");
        Directory.CreateDirectory(asDirectory);

        var result = ClaudeSettingsFile.Save(asDirectory, Edits());

        Assert.Equal(SettingsSaveKind.RefusedUnreadable, result.Kind);
        Assert.False(result.Saved);
    }

    /// <summary>
    /// The write goes through a sibling temp file and a move, so a crash part-way cannot leave a
    /// half-written settings file - which would be this fix manufacturing the exact state it refuses
    /// to write over.
    ///
    /// WHAT THIS TEST ACTUALLY PROVES, which is less: that the temp file is cleaned up and the result
    /// parses. It does NOT prove atomicity. Reverting the move to an in-place WriteAllText was tried
    /// and all tests still passed, so this test does not distinguish the two implementations. The
    /// atomicity is correct by construction and matches how config.json and the key store in this
    /// repository already write; it is not covered by a test, and saying so here is cheaper than a
    /// name that implies otherwise.
    /// </summary>
    [Fact]
    public void Save_LeavesNoTempFileBehind_AndTheResultParses()
    {
        File.WriteAllText(_path, WithAHook);

        var result = ClaudeSettingsFile.Save(_path, Edits(mode: "acceptEdits"));

        // Assert the save ACTUALLY HAPPENED first. Without this the whole test passed on a Save that
        // did nothing at all: the fixture already held one valid settings.json, which satisfied both
        // assertions below on its own. A test that a no-op passes is not a test.
        Assert.Equal(SettingsSaveKind.Merged, result.Kind);
        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal("acceptEdits", root["permissions"]!["defaultMode"]!.GetValue<string>());

        Assert.Equal(new[] { "settings.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    /// <summary>
    /// Saved is a POSITIVE test of the kinds that actually wrote. A refusal and a failed write are
    /// both "not saved", and neither may drift onto the success path if another kind is added.
    /// </summary>
    [Fact]
    public void Saved_IsTrueOnlyForTheTwoKindsThatWrote()
    {
        Assert.True(new SettingsSaveResult(SettingsSaveKind.Created, null).Saved);
        Assert.True(new SettingsSaveResult(SettingsSaveKind.Merged, null).Saved);
        Assert.False(new SettingsSaveResult(SettingsSaveKind.RefusedUnreadable, "x").Saved);
        Assert.False(new SettingsSaveResult(SettingsSaveKind.WriteFailed, "x").Saved);
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
