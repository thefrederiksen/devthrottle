using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Session Rules part 2, phase 1, rulings P9, P12 and P15: every Director Claude Code session gets a
/// PreToolUse hook for the shell tools that runs cc-rule-hook from beside the Director's own executable -
/// but ONLY when that executable is there. A hook pointed at a missing file would error on every shell
/// command of every session.
///
/// The registration lives in the installer's ONE settings builder, beside the SessionStart hooks, so the
/// shared settings file and a factory seat's own settings file can never disagree about it.
/// </summary>
public sealed class ClaudeRuleHookRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-rule-hook-registration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string HooksDirectory => Path.Combine(_root, "claude-hooks");

    /// <summary>A stand-in for the shipped executable, in a folder with a space like real install folders.</summary>
    private string PlaceRuleHook(bool forWindows)
    {
        var path = Path.Combine(_root, "Director App", ClaudeHookInstaller.RuleHookExecutableName(forWindows));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stand-in");
        return path;
    }

    private static JsonObject Hooks(string settingsPath)
        => JsonNode.Parse(File.ReadAllText(settingsPath))!["hooks"]!.AsObject();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookPresent_RegistersPreToolUseForBashAndPowerShellRunningIt(bool forWindows)
    {
        var ruleHook = PlaceRuleHook(forWindows);

        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, ruleHook)!;

        var preToolUse = Assert.Single(Hooks(settings)["PreToolUse"]!.AsArray())!;
        Assert.Equal("Bash|PowerShell", (string?)preToolUse["matcher"]);
        var hook = Assert.Single(preToolUse["hooks"]!.AsArray())!;
        Assert.Equal("command", (string?)hook["type"]);
        // The absolute path, quoted: install folders have spaces in them.
        Assert.Equal($"\"{ruleHook}\"", (string?)hook["command"]);
        Assert.True(Path.IsPathFullyQualified(ruleHook));
        Assert.Equal(10, (int)hook["timeout"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookPresent_KeepsTheFourSessionStartHooks(bool forWindows)
    {
        var withHook = ClaudeHookInstaller.EnsureInstalled(Path.Combine(_root, "a"), forWindows, PlaceRuleHook(forWindows))!;

        var sessionStart = Hooks(withHook)["SessionStart"]!.AsArray();
        Assert.Equal(["startup", "resume", "clear", "compact"], sessionStart.Select(e => (string?)e!["matcher"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureInstalled_RuleHookAbsent_RegistersNoPreToolUseAndStillTheSessionStartHooks(bool forWindows)
    {
        var missing = Path.Combine(_root, "nowhere", ClaudeHookInstaller.RuleHookExecutableName(forWindows));

        var settings = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows, missing)!;

        var hooks = Hooks(settings);
        Assert.False(hooks.ContainsKey("PreToolUse"));
        Assert.Equal(4, hooks["SessionStart"]!.AsArray().Count);
    }

    [Fact]
    public void RuleHookExecutableName_PerPlatform_IsCcRuleHookWithTheWindowsExtensionOnWindowsOnly()
    {
        Assert.Equal("cc-rule-hook.exe", ClaudeHookInstaller.RuleHookExecutableName(forWindows: true));
        Assert.Equal("cc-rule-hook", ClaudeHookInstaller.RuleHookExecutableName(forWindows: false));
    }

    [Fact]
    public void RuleHookPathBesideDirector_Always_IsTheExecutableInTheDirectorsOwnFolder()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "cc-rule-hook.exe"),
            ClaudeHookInstaller.RuleHookPathBesideDirector(forWindows: true));
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "cc-rule-hook"),
            ClaudeHookInstaller.RuleHookPathBesideDirector(forWindows: false));
    }

    [Fact]
    public void WriteSessionSettings_RuleHookPresent_CarriesTheSamePreToolUseAsTheSharedFile()
    {
        var ruleHook = PlaceRuleHook(forWindows: true);
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true, ruleHook)!;
        var kit = Path.Combine(_root, "kit");
        Directory.CreateDirectory(kit);

        var own = ClaudeHookInstaller.WriteSessionSettings(kit, HooksDirectory, forWindows: true, new JsonObject { ["deny"] = new JsonArray("Bash(git push:*)") }, ruleHook);

        Assert.True(JsonNode.DeepEquals(Hooks(shared)["PreToolUse"], Hooks(own)["PreToolUse"]));
        Assert.NotNull(Hooks(own)["PreToolUse"]);
    }
}
