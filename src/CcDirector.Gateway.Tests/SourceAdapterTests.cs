using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the per-source adapter contract (issue #300): registry dispatch across
/// github/devops/jira, the per-source seed prompts, and the sentinel correlation keys.
/// </summary>
public sealed class SourceAdapterTests
{
    private static WorkListItemRef Ref(string source, string id) => new() { Source = source, Id = id };

    [Theory]
    [InlineData("github")]
    [InlineData("GitHub")]
    [InlineData("devops")]
    [InlineData("DevOps")]
    public void TryGet_RunnableSources_ReturnsAdapter_CaseInsensitive(string source)
    {
        var adapter = SourceAdapters.TryGet(source);

        Assert.NotNull(adapter);
        Assert.Equal(source.ToLowerInvariant(), adapter.Source);
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("JIRA")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-else")]
    public void TryGet_NonRunnableSources_ReturnsNull(string? source)
    {
        Assert.Null(SourceAdapters.TryGet(source));
    }

    [Fact]
    public void Github_BuildSeedPrompt_IsOriginalV1Form()
    {
        var adapter = SourceAdapters.TryGet("github");

        Assert.NotNull(adapter);
        // The pre-#300 runner seeded exactly this - the github path is unchanged (regression).
        Assert.Equal("/implementation-loop 262", adapter.BuildSeedPrompt(Ref("github", "262")));
    }

    [Fact]
    public void Devops_BuildSeedPrompt_UsesDevopsMode()
    {
        var adapter = SourceAdapters.TryGet("devops");

        Assert.NotNull(adapter);
        Assert.Equal("/implementation-loop --source devops 1203", adapter.BuildSeedPrompt(Ref("devops", "1203")));
    }

    [Theory]
    [InlineData("github", "262", 262)]
    [InlineData("devops", "1203", 1203)]
    public void TryGetCorrelationKey_NumericIds_Parse(string source, string id, int expected)
    {
        var adapter = SourceAdapters.TryGet(source);

        Assert.NotNull(adapter);
        Assert.True(adapter.TryGetCorrelationKey(Ref(source, id), out var key));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("github", "abc")]
    [InlineData("devops", "WI-99")]
    public void TryGetCorrelationKey_NonNumericIds_False(string source, string id)
    {
        var adapter = SourceAdapters.TryGet(source);

        Assert.NotNull(adapter);
        Assert.False(adapter.TryGetCorrelationKey(Ref(source, id), out _));
    }
}
