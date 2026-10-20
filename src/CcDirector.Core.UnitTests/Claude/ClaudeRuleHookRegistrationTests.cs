using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Session Rules part 2, phase 1, rulings P9, P12 and P16: every Director Claude Code session gets a
/// PreToolUse hook for the shell tools that runs cc-rule-hook. The Director carries the executable inside
/// itself; the installer writes it into the instance's claude-hooks folder beside the hook scripts and
/// registers THAT path. A Director build that carries no hook registers no PreToolUse hook - a hook
/// pointed at a missing file would error on every shell command of every session.
///
/// The registration lives in the installer's ONE settings builder, beside the SessionStart hooks, so the
/// shared settings file and a factory seat's own settings file can never disagree about it.
/// </summary>
public sealed class ClaudeRuleHookRegistrationTests : IDisposable
{
    private static readonly byte[] HookBytes = [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4];
    private static readonly byte[] OlderHookBytes = [0x7f, (byte)'E', (byte)'L', (byte)'F', 9, 9];

    // A space in the folder, like real per-user data folders ("Application Support", user names).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc rule hook registration " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string HooksDirectory => Path.Combine(_root, "claude-hooks");

    private static JsonObject Hooks(string settingsPath)
        => JsonNode.Parse(File.ReadAllText(settingsPath))!["hooks"]!.AsObject();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookCarried_WritesItBesideTheScriptsAndRegistersThatPathForBashAndPowerShell(bool forWindows)
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, HookBytes)!;

        var written = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows);
        Assert.Equal(Path.Combine(HooksDirectory, forWindows ? "cc-rule-hook.exe" : "cc-rule-hook"), written);
        Assert.Equal(HookBytes, File.ReadAllBytes(written));

        var preToolUse = Assert.Single(Hooks(settings)["PreToolUse"]!.AsArray())!;
        Assert.Equal("^(Bash|PowerShell)$", (string?)preToolUse["matcher"]);
        var hook = Assert.Single(preToolUse["hooks"]!.AsArray())!;
        Assert.Equal("command", (string?)hook["type"]);
        Assert.True(Path.IsPathFullyQualified(written));
        Assert.Equal(10, (int)hook["timeout"]!);
        AssertExecForm(hook, written);
    }

    /// <summary>
    /// Whole-phase review finding 1. A hook with no <c>args</c> is SHELL form: Claude Code hands the string
    /// to sh on Linux and macOS, to Git Bash on Windows, and to PowerShell on a Windows machine without Git
    /// Bash - where a quoted path is a string expression, not a call, and where the vendor documents that a
    /// failing shell-form PreToolUse hook can BLOCK the tool call. EXEC form - <c>args</c> present, even
    /// empty - spawns <c>command</c> directly as the program, with no shell on any platform. So the
    /// evaluator is registered as its bare absolute path (no quotes, which nothing would strip), an empty
    /// argument list, and no <c>shell</c> field.
    /// </summary>
    private static void AssertExecForm(JsonNode hook, string evaluatorPath)
    {
        Assert.Equal(evaluatorPath, (string?)hook["command"]);
        var args = Assert.IsType<JsonArray>(hook["args"]);
        Assert.Empty(args);
        Assert.Null(hook["shell"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookCarried_RegistersItInExecFormWithNoShell(bool forWindows)
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, HookBytes)!;

        // Read from the file as Claude Code reads it: the args key must be there, as an empty array.
        var hook = Hooks(settings)["PreToolUse"]![0]!["hooks"]![0]!.AsObject();
        Assert.True(hook.ContainsKey("args"));
        Assert.Equal(["type", "command", "args", "timeout"], hook.Select(p => p.Key));
        AssertExecForm(hook, ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookCarried_KeepsTheFourSessionStartHooks(bool forWindows)
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, HookBytes)!;

        var sessionStart = Hooks(settings)["SessionStart"]!.AsArray();
        Assert.Equal(["startup", "resume", "clear", "compact"], sessionStart.Select(e => (string?)e!["matcher"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_NoRuleHookCarried_RegistersNoPreToolUseAndStillTheSessionStartHooks(bool forWindows)
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, ruleHook: null)!;

        var hooks = Hooks(settings);
        Assert.False(hooks.ContainsKey("PreToolUse"));
        Assert.Equal(4, hooks["SessionStart"]!.AsArray().Count);
        Assert.False(File.Exists(ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows)));
    }

    [Fact]
    public void EnsureInstalled_NoRuleHookCarriedButAnOlderOneOnDisk_StillRegistersNone()
    {
        Directory.CreateDirectory(HooksDirectory);
        File.WriteAllBytes(ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: false), OlderHookBytes);

        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, ruleHook: null)!;

        Assert.False(Hooks(settings).ContainsKey("PreToolUse"));
    }

    [Fact]
    public void EnsureInstalled_SameBytesAlreadyThere_DoesNotRewriteThem()
    {
        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, HookBytes);
        var path = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: false);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, HookBytes);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void EnsureInstalled_OlderBytesThere_ReplacesThemAndLeavesNoTemporaryFile()
    {
        Directory.CreateDirectory(HooksDirectory);
        var path = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: false);
        File.WriteAllBytes(path, OlderHookBytes);

        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, HookBytes);

        Assert.Equal(HookBytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(HooksDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureInstalled_OnLinuxOrMac_TheWrittenHookIsExecutable()
    {
        if (OperatingSystem.IsWindows())
            return; // The executable bit is a POSIX permission; Windows runs a .exe by its name.

        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, HookBytes);

        var mode = File.GetUnixFileMode(ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: false));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"mode is {mode}");
    }

    /// <summary>
    /// Windows will not replace an executable that is running, and a hook from another session may be
    /// running at the moment a new session launches. That is never fatal: the replace is retried on the
    /// next launch, and meanwhile the hook already on disk - a working hook from the previous build - is
    /// what the session runs.
    /// </summary>
    [Fact]
    public void EnsureInstalled_ReplaceRefusedBecauseInUse_KeepsTheOldHookRegisteredAndRemovesTheTemporaryFile()
    {
        Directory.CreateDirectory(HooksDirectory);
        var path = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: true);
        File.WriteAllBytes(path, OlderHookBytes);

        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes,
            replace: (_, _) => throw new IOException("The process cannot access the file because it is being used by another process."))!;

        Assert.NotNull(settings);
        Assert.Equal(OlderHookBytes, File.ReadAllBytes(path));
        AssertExecForm(Hooks(settings)["PreToolUse"]![0]!["hooks"]![0]!, path);
        Assert.DoesNotContain(Directory.GetFiles(HooksDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureInstalled_ReplaceRefusedAndNothingOnDisk_RegistersNoPreToolUse()
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes,
            replace: (_, _) => throw new UnauthorizedAccessException("Access to the path is denied."))!;

        Assert.NotNull(settings);
        Assert.False(Hooks(settings).ContainsKey("PreToolUse"));
    }

    [Fact]
    public void WriteSessionSettings_RuleHookCarried_CarriesTheSamePreToolUseAsTheSharedFile()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes)!;
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);

        var own = ClaudeHookInstaller.WriteSessionSettings(kit, HooksDirectory, forWindows: true,
            new JsonObject { ["deny"] = new JsonArray("Bash(git push:*)") }, HookBytes);

        Assert.NotNull(Hooks(own)["PreToolUse"]);
        Assert.True(JsonNode.DeepEquals(Hooks(shared)["PreToolUse"], Hooks(own)["PreToolUse"]));
    }

    /// <summary>
    /// Review finding 2, in the Director's REAL launch order: a factory seat's own settings are written
    /// (SessionManager.PrepareFactoryKitLaunch) BEFORE the shared hooks are installed. On a fresh instance -
    /// a new install, a new slot - nothing has written the evaluator yet, and the seat's file is its ONE
    /// --settings. It must still register the rule hook, or that seat's shell commands are never evaluated
    /// and nothing anywhere says so.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteSessionSettings_FreshInstanceBeforeTheSharedInstall_StillWritesAndRegistersTheRuleHook(bool forWindows)
    {
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);
        Assert.False(Directory.Exists(HooksDirectory));

        var own = ClaudeHookInstaller.WriteSessionSettings(kit, HooksDirectory, forWindows, new JsonObject(), HookBytes);

        var path = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows);
        Assert.Equal(HookBytes, File.ReadAllBytes(path));
        AssertExecForm(Hooks(own)["PreToolUse"]![0]!["hooks"]![0]!, path);

        // And the shared install that follows agrees with it.
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, HookBytes)!;
        Assert.True(JsonNode.DeepEquals(Hooks(shared)["PreToolUse"], Hooks(own)["PreToolUse"]));
    }

    [Fact]
    public void WriteSessionSettings_NoRuleHookCarried_RegistersNone()
    {
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);
        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes);

        var own = ClaudeHookInstaller.WriteSessionSettings(kit, HooksDirectory, forWindows: true, new JsonObject(), ruleHook: null);

        Assert.False(Hooks(own).ContainsKey("PreToolUse"));
    }

    // ---------- Review finding 1: nothing about the evaluator may cost the session its other hooks ----------

    /// <summary>
    /// The SessionStart hooks (session pointer across /clear and compaction, the fleet preamble) worked
    /// before the rule hook existed. A failure of the optional evaluator step must never take them away:
    /// EnsureInstalled returning null launches the session with NO --settings at all.
    /// </summary>
    [Fact]
    public void EnsureInstalled_ExistingEvaluatorCannotBeRead_StillWritesTheSettingsWithTheSessionStartHooks()
    {
        if (OperatingSystem.IsWindows())
            return; // Removing read permission is a POSIX mode; on Windows the same failure is a sharing lock.
        Directory.CreateDirectory(HooksDirectory);
        var path = ClaudeHookInstaller.RuleHookPathIn(HooksDirectory, forWindows: false);
        File.WriteAllBytes(path, OlderHookBytes);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: false, HookBytes);

            Assert.NotNull(settings);
            Assert.Equal(4, Hooks(settings!)["SessionStart"]!.AsArray().Count);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void EnsureInstalled_EvaluatorStepThrowsAnything_StillWritesTheSettingsWithTheSessionStartHooks(Type failure)
    {
        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes,
            replace: (_, _) => throw (Exception)Activator.CreateInstance(failure, "simulated")!);

        Assert.NotNull(settings);
        Assert.Equal(4, Hooks(settings!)["SessionStart"]!.AsArray().Count);
        Assert.False(Hooks(settings!).ContainsKey("PreToolUse"));
        Assert.DoesNotContain(Directory.GetFiles(HooksDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void RuleHookBinary_TestHostCarriesNone_IsNotCarried()
    {
        // The test host is the entry assembly here, and it embeds no hook - only a Director published for
        // Windows or Linux does. A presence check on the real Director is in the build scripts.
        Assert.False(RuleHookBinary.IsCarried());
        Assert.Null(RuleHookBinary.Carried());
    }
}
