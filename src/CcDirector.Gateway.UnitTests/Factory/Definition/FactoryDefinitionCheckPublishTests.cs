using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// WHAT THE WHOLE-DEFINITION CHECK ADDS IN PHASE 4 (Factory Definition; design sections 3 and 7): the boss seat's name
/// must be Boss, on every check; and, at a publish or a restore only, a start with a check is an error on its check
/// line and the secret question is worded as the publish asks it. A save keeps the phase 1 behaviour for both.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckPublishTests
{
    private static FactoryDefinitionCheckResult CheckFor(IReadOnlyDictionary<string, string> content, DefinitionCheckPurpose purpose)
        => FactoryDefinitionCheck.Check(content, DefinitionFixture.FactoryId, DefinitionFixture.FleetSkills, DefinitionFixture.Machines, purpose);

    [Fact]
    public void Check_ABossSeatNamedOtherThanBoss_IsAnErrorOnItsNameLine_AtASave()
    {
        var result = DefinitionFixture.Check(DefinitionFixture.Edit("seats/boss.yaml", "name: Boss", "name: Chief"));

        DefinitionFixture.AssertOneError(result, "seats/boss.yaml", 2,
            "The boss seat 'boss' is named 'Chief'. The boss has no name of its own: set its name to \"Boss\".");
    }

    [Fact]
    public void Check_ANonBossSeatMayHaveAnyName()
    {
        var result = DefinitionFixture.Check(DefinitionFixture.Edit("seats/writer.yaml", "name: Writer", "name: Chief"));

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Check_AStartWithACheck_IsAnErrorOnItsCheckLine_AtAPublish()
    {
        var result = CheckFor(DefinitionFixture.Valid(), DefinitionCheckPurpose.Publish);

        var error = DefinitionFixture.AssertOneError(result, "seats/outreach.yaml", 13, "This start has a check.");
        Assert.Equal("This start has a check. Checks from a definition run from phase 5b of the factory definition work, so this version cannot be published yet; publish it without the start, or wait.",
            error.Message);
    }

    [Fact]
    public void Check_AStartWithACheck_IsNoErrorAtASave_ADraftMayHoldIt()
    {
        var result = CheckFor(DefinitionFixture.Valid(), DefinitionCheckPurpose.Save);

        Assert.False(result.HasErrors);
        Assert.NotNull(result.Definition);
    }

    [Fact]
    public void Check_TheSecretQuestion_AtAPublish_SaysTheMachineCannotAnswerYet()
    {
        var result = CheckFor(DefinitionFixture.Publishable(), DefinitionCheckPurpose.Publish);

        DefinitionFixture.AssertNotice(result, "seats/outreach.yaml", 18,
            "SOREN_SOUTH cannot answer yet whether it holds 'outreach-smtp-password': the question needs a Director that can be asked it, which comes with phase 5. The seat's start will check it.");
        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("is not checked yet", StringComparison.Ordinal) && f.Message.Contains("holds", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_TheSecretQuestion_AtASave_KeepsThePhase1Wording()
    {
        var result = CheckFor(DefinitionFixture.Publishable(), DefinitionCheckPurpose.Save);

        DefinitionFixture.AssertNotice(result, "seats/outreach.yaml", 18, "Whether SOREN_SOUTH holds 'outreach-smtp-password' is not checked yet.");
    }
}
