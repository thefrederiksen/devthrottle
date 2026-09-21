using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

public sealed class TriggerDefinitionTests
{
    internal static TriggerDefinitionRequest Valid() => new()
    {
        Name = "website-new-mail",
        Factory = "website-factory",
        FactoryAgent = "Front Desk",
        Machine = "SOREN_NORTH",
        RepoPath = @"D:\ReposFred\cc-consult",
        CheckCommand = "cc-website-factory mail-waiting --json",
        IntervalSeconds = 300,
        Prompt = "New mail: {count} threads. Handle them.",
    };

    [Fact]
    public void Validate_ACompleteDefinition_IsAccepted() => Assert.Null(TriggerDefinition.Validate(Valid(), isCreate: true));

    [Fact]
    public void Validate_AnIntervalUnderAMinute_IsRefused()
    {
        var req = Valid();
        req.IntervalSeconds = 59;
        Assert.Equal("intervalSeconds must be at least 60 (one minute)", TriggerDefinition.Validate(req, isCreate: true));
    }

    [Fact]
    public void Validate_OneMinute_IsAccepted()
    {
        var req = Valid();
        req.IntervalSeconds = 60;
        Assert.Null(TriggerDefinition.Validate(req, isCreate: true));
    }

    [Theory]
    [InlineData(nameof(TriggerDefinitionRequest.Name), "name is required")]
    [InlineData(nameof(TriggerDefinitionRequest.CheckCommand), "checkCommand is required")]
    [InlineData(nameof(TriggerDefinitionRequest.Machine), "machine is required")]
    [InlineData(nameof(TriggerDefinitionRequest.Prompt), "prompt is required")]
    public void Validate_AMissingFieldOnCreate_IsRefused_ButKeptOnAnUpdate(string field, string error)
    {
        var req = Valid();
        typeof(TriggerDefinitionRequest).GetProperty(field)!.SetValue(req, null);
        Assert.Equal(error, TriggerDefinition.Validate(req, isCreate: true));
        Assert.Null(TriggerDefinition.Validate(req, isCreate: false));
    }

    [Theory]
    [InlineData("new/mail")]
    [InlineData("-leading")]
    [InlineData("7c7f2e1e-0000-4000-8000-000000000001")]
    public void Validate_ANameThatCannotTravelInAPathOrLooksLikeAnId_IsRefused(string name)
    {
        var req = Valid();
        req.Name = name;
        Assert.NotNull(TriggerDefinition.Validate(req, isCreate: true));
    }

    [Fact]
    public void PromptFor_ReplacesEveryCount()
        => Assert.Equal("2 threads, 2 replies", TriggerDefinition.PromptFor("{count} threads, {count} replies", 2));

    [Theory]
    [InlineData(60, 30)]
    [InlineData(300, 150)]
    [InlineData(3600, 300)]
    public void TimeoutSecondsFor_IsHalfTheIntervalAtMostFiveMinutes(int interval, int timeout)
        => Assert.Equal(timeout, TriggerDefinition.TimeoutSecondsFor(interval));
}
