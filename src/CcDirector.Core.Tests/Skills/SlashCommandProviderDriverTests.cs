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
    public void GetCommands_RawCustomSession_ReturnsNoBuiltInCommands()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.RawCli, _repoPath);

        Assert.Empty(commands);
    }

    [Theory]
    [InlineData(AgentKind.Codex, "model")]
    [InlineData(AgentKind.Gemini, "theme")]
    [InlineData(AgentKind.OpenCode, "models")]
    [InlineData(AgentKind.Cursor, "model")]
    public void GetCommands_NonClaudeDriverSession_ReturnsOwnCommandCatalog(AgentKind agentKind, string expectedCommand)
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(agentKind, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("help", names);
        Assert.Contains(expectedCommand, names);
        Assert.DoesNotContain("output-style", names);
        Assert.DoesNotContain("scoped-models", names);
    }

    [Fact]
    public void GetCommands_GrokSession_ReturnsInteractiveSlashCommands_NotCliSubcommands()
    {
        // The Grok catalog must list Grok's INTERACTIVE slash commands (the menu shown when you
        // type "/" at its prompt), not the shell subcommands of `grok --help` (login/sessions/...).
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.Grok, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Real interactive commands captured live from Grok Build Beta v0.2.56.
        Assert.Contains("new", names);
        Assert.Contains("compact", names);
        Assert.Contains("fork", names);
        Assert.Contains("quit", names);
        // The wrong (CLI-subcommand) surface must not leak in.
        Assert.DoesNotContain("login", names);
        Assert.DoesNotContain("sessions", names);
        // And it must never inherit Claude's commands.
        Assert.DoesNotContain("output-style", names);
        Assert.DoesNotContain("permissions", names);
    }

    [Fact]
    public void GetCommands_ClaudeSession_IncludesInteractiveCommandsInComposerList()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.ClaudeCode, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("clear", names);
        Assert.Contains("compact", names);
        Assert.Contains("permissions", names);
        Assert.Contains("model", names);
        Assert.Contains("theme", names);
        Assert.Contains("resume", names);
    }

    [Fact]
    public void GetCommands_PiSession_IncludesInteractiveCommandsInComposerList()
    {
        var provider = new SlashCommandProvider();

        var commands = provider.GetCommands(AgentKind.Pi, _repoPath);
        var names = commands.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("new", names);
        Assert.Contains("export", names);
        Assert.Contains("settings", names);
        Assert.Contains("model", names);
        Assert.Contains("scoped-models", names);
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
