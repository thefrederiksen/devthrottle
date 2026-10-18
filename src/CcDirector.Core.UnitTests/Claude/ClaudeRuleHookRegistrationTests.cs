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
        Assert.Equal("Bash|PowerShell", (string?)preToolUse["matcher"]);
        var hook = Assert.Single(preToolUse["hooks"]!.AsArray())!;
        Assert.Equal("command", (string?)hook["type"]);
        // The absolute path, quoted for the spaces in per-user folders.
        Assert.Equal($"\"{written}\"", (string?)hook["command"]);
        Assert.True(Path.IsPathFullyQualified(written));
        Assert.Equal(10, (int)hook["timeout"]!);
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
        Assert.Equal($"\"{path}\"", (string?)Hooks(settings)["PreToolUse"]![0]!["hooks"]![0]!["command"]);
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
            new JsonObject { ["deny"] = new JsonArray("Bash(git push:*)") }, ruleHookCarried: true);

        Assert.NotNull(Hooks(own)["PreToolUse"]);
        Assert.True(JsonNode.DeepEquals(Hooks(shared)["PreToolUse"], Hooks(own)["PreToolUse"]));
    }

    [Fact]
    public void WriteSessionSettings_NoRuleHookCarried_RegistersNone()
    {
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);
        ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, HookBytes);

        var own = ClaudeHookInstaller.WriteSessionSettings(kit, HooksDirectory, forWindows: true, new JsonObject(), ruleHookCarried: false);

        Assert.False(Hooks(own).ContainsKey("PreToolUse"));
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
