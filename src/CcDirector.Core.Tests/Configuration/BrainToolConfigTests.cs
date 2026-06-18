using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="BrainToolConfig"/> (issue #393), the Gateway brain's tool choice.
/// The contract: missing file/key = the default (Claude Code), a set value is parsed
/// case-insensitively, and a present-but-invalid value (unknown name, blank, or non-string)
/// THROWS (no-fallback - the brain must never silently run as a tool nobody chose). As of
/// issue #510 the value may be ANY recognised <see cref="AgentKind"/> name, not just the
/// brain-hostable list, because the wingman agent is chosen from the machine's registered
/// agents (the driver-level hostability work landed in issue #509).
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class BrainToolConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public BrainToolConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-braintool-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Get_MissingConfig_ReturnsDefault()
    {
        Assert.Equal(BrainToolConfig.Default, BrainToolConfig.Get());
    }

    [Fact]
    public void Get_KeyAbsent_ReturnsDefault()
    {
        SeedConfig("""{ "brain_model": "opus" }""");
        Assert.Equal(BrainToolConfig.Default, BrainToolConfig.Get());
    }

    [Fact]
    public void Get_KeySet_ReturnsParsedTool()
    {
        SeedConfig("""{ "brain_tool": "ClaudeCode" }""");
        Assert.Equal(AgentKind.ClaudeCode, BrainToolConfig.Get());
    }

    [Fact]
    public void Get_KeySet_IsCaseInsensitive()
    {
        SeedConfig("""{ "brain_tool": "  claudecode  " }""");
        Assert.Equal(AgentKind.ClaudeCode, BrainToolConfig.Get());
    }

    [Fact]
    public void Get_UnknownToolName_Throws()
    {
        SeedConfig("""{ "brain_tool": "NotATool" }""");
        var ex = Assert.Throws<InvalidOperationException>(() => BrainToolConfig.Get());
        Assert.Contains("brain_tool", ex.Message);
    }

    [Fact]
    public void Get_AnyRegisteredAgentKind_IsAccepted()
    {
        // Issue #510: the wingman agent is chosen from the machine's registered agents, so any
        // recognised AgentKind name is now a valid brain_tool - not just the brain-hostable list.
        // Pi (a known kind that was previously rejected) now parses straight through.
        SeedConfig("""{ "brain_tool": "Pi" }""");
        Assert.Equal(AgentKind.Pi, BrainToolConfig.Get());
    }

    [Fact]
    public void Get_BlankValue_Throws()
    {
        SeedConfig("""{ "brain_tool": "   " }""");
        var ex = Assert.Throws<InvalidOperationException>(() => BrainToolConfig.Get());
        Assert.Contains("brain_tool", ex.Message);
    }

    [Fact]
    public void Get_NonStringValue_Throws()
    {
        SeedConfig("""{ "brain_tool": 42 }""");
        var ex = Assert.Throws<InvalidOperationException>(() => BrainToolConfig.Get());
        Assert.Contains("brain_tool", ex.Message);
    }

    [Fact]
    public void BrainHostableTools_ContainsDefault()
    {
        Assert.Contains(BrainToolConfig.Default, BrainToolConfig.BrainHostableTools);
    }

    [Fact]
    public void IsHostable_ClaudeCode_True()
    {
        Assert.True(BrainToolConfig.IsHostable(AgentKind.ClaudeCode));
    }

    [Fact]
    public void IsHostable_Pi_False()
    {
        Assert.False(BrainToolConfig.IsHostable(AgentKind.Pi));
    }
}
