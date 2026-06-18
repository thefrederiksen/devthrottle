using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

public sealed class SlashCommandProviderDriverTests : IDisposable
{
    private readonly string _repoPath;

    public SlashCommandProviderDriverTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "cc-director-slash-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoPath);
    }

    [Fact]
    public void GetCommands_PiSession_ReturnsPiCommandsWithoutClaudeCommands()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.Pi, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("settings", names);
        Assert.Contains("scoped-models", names);
        Assert.Contains("new", names);
        Assert.DoesNotContain("permissions", names);
        Assert.DoesNotContain("output-style", names);
    }

    [Fact]
    public void GetCommands_ClaudeSession_PreservesClaudeCommands()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.ClaudeCode, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("permissions", names);
        Assert.Contains("output-style", names);
        Assert.Contains("clear", names);
        Assert.DoesNotContain("scoped-models", names);
    }

    [Fact]
    public void GetCommands_GenericSession_ReturnsNoBuiltInCommands()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.Codex, _repoPath);

        Assert.Empty(commands);
    }

    [Fact]
    public void GetCommands_PiSession_DiscoversProjectPromptsAndSkills()
    {
        var promptDir = Path.Combine(_repoPath, ".pi", "prompts");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "review.md"), "---\ndescription: Review the current changes\n---\nReview this repository.");

        var skillDir = Path.Combine(_repoPath, ".pi", "skills", "browser");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: browser\ndescription: Use browser automation\n---\n# Browser");

        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.Pi, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("review", names);
        Assert.Contains("skill:browser", names);
    }

    [Fact]
    public void InvalidateCache_PiPromptAddedAfterFirstRead_RefreshesCommands()
    {
        var promptDir = Path.Combine(_repoPath, ".pi", "prompts");
        Directory.CreateDirectory(promptDir);
        var provider = new SlashCommandProvider();

        var before = provider.GetCommands(AgentKind.Pi, _repoPath);
        File.WriteAllText(Path.Combine(promptDir, "late.md"), "---\ndescription: Added after first read\n---\nRun this later.");
        provider.InvalidateCache(AgentKind.Pi, _repoPath);

        var after = provider.GetCommands(AgentKind.Pi, _repoPath);

        Assert.DoesNotContain(before, command => string.Equals(command.Name, "late", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(after, command => string.Equals(command.Name, "late", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
            Directory.Delete(_repoPath, recursive: true);
    }
}
