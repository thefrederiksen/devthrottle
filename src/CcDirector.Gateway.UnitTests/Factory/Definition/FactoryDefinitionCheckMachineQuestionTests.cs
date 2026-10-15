using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE PUBLISH NOTICES FROM THE MACHINE QUESTION (Factory Definition, phase 5, design section 6). The check is handed
/// what each seat's machine answered and words the secret and repository notices from it: held and allowed for run,
/// or registered, is no notice; anything else is a notice naming the seat file, the line, the machine and the name -
/// never an error, because the seat's start is the check that binds. A save asks nothing and keeps its wording.
///
/// The fixture's outreach seat runs on SOREN_SOUTH, works in the repository 'devthrottle' (line 8) and lists the
/// secret 'outreach-smtp-password' (line 18).
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckMachineQuestionTests
{
    private const string Seat = "seats/outreach.yaml";
    private const int RepositoryLine = 8;
    private const int SecretLine = 18;
    private const string Secret = "outreach-smtp-password";

    private static FactoryDefinitionCheckResult Publish(MachineReply southReply)
        => FactoryDefinitionCheck.Check(DefinitionFixture.Publishable(), DefinitionFixture.FactoryId, DefinitionFixture.FleetSkills,
            DefinitionFixture.Machines, DefinitionCheckPurpose.Publish,
            new Dictionary<string, MachineReply>(StringComparer.OrdinalIgnoreCase) { ["SOREN_SOUTH"] = southReply });

    private static MachineReply Answered(FactorySecretAnswer secret, bool registered = true, string? secretsUnavailable = null)
        => MachineReply.Answered(new FactoryMachineAnswer
        {
            Machine = "SOREN_SOUTH",
            SecretsUnavailable = secretsUnavailable,
            Secrets = secretsUnavailable is null ? new List<FactorySecretAnswer> { secret } : new List<FactorySecretAnswer>(),
            Repositories = new List<FactoryRepositoryAnswer>
            {
                new() { Name = "devthrottle", Registered = registered, Path = registered ? @"D:\repos\devthrottle" : null },
            },
        });

    private static FactorySecretAnswer HeldAndAllowed => new() { Name = Secret, Held = true, AllowedForRun = true };

    private static IEnumerable<DefinitionFinding> AtLine(FactoryDefinitionCheckResult result, int line)
        => result.Findings.Where(f => f.File == Seat && f.Line == line);

    private static void AssertOnlyNotice(FactoryDefinitionCheckResult result, int line, string message)
    {
        var finding = Assert.Single(AtLine(result, line));
        Assert.False(finding.IsError);
        Assert.Equal(message, finding.Message);
    }

    [Fact]
    public void Check_TheMachineQuestion_TheQuestionsAPublishableDefinitionRaises()
    {
        var questions = FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(DefinitionFixture.Publishable()).Definition, DefinitionFixture.Publishable());

        var question = Assert.Single(questions);
        Assert.Equal("SOREN_SOUTH", question.Machine);
        Assert.Equal(new[] { Secret }, question.Secrets);
        Assert.Equal(new[] { "devthrottle" }, question.Repositories);
    }

    [Fact]
    public void Check_AtAPublish_AHeldAllowedSecretAndARegisteredRepository_AreNoNotice()
    {
        var result = Publish(Answered(HeldAndAllowed));

        Assert.False(result.HasErrors);
        Assert.Empty(AtLine(result, SecretLine));
        Assert.Empty(AtLine(result, RepositoryLine));
    }

    [Fact]
    public void Check_AtAPublish_ASecretTheMachineDoesNotHold_IsANoticeNamingItAndTheMachine()
    {
        var result = Publish(Answered(new FactorySecretAnswer { Name = Secret, Held = false, AllowedForRun = false }));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, SecretLine,
            "SOREN_SOUTH does not hold the secret 'outreach-smtp-password'. Add it there with cc-secrets; the seat's start will refuse without it.");
    }

    [Fact]
    public void Check_AtAPublish_ASecretNotAllowedForRun_IsANoticeInTheToolsOwnWords()
    {
        var result = Publish(Answered(new FactorySecretAnswer
        {
            Name = Secret, Held = true, AllowedForRun = false,
            NotAllowedReason = "Secret 'outreach-smtp-password' is not allowed for 'run'. It may be used for: login.",
        }));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, SecretLine,
            "SOREN_SOUTH holds 'outreach-smtp-password', but cc-secrets will not let a seat use it: Secret 'outreach-smtp-password' is not allowed for 'run'. It may be used for: login. The seat's start will refuse it.");
    }

    [Fact]
    public void Check_AtAPublish_ARepositoryNotRegistered_IsANoticeNamingItAndTheMachine()
    {
        var result = Publish(Answered(HeldAndAllowed, registered: false));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, RepositoryLine,
            "SOREN_SOUTH does not have the repository 'devthrottle' registered. Register it there; the seat's start will refuse without it.");
    }

    [Fact]
    public void Check_AtAPublish_CcSecretsUnavailable_SaysSoForTheSecret_AndStillAnswersTheRepository()
    {
        var result = Publish(Answered(HeldAndAllowed, secretsUnavailable: "cc-secrets is not installed on SOREN_SOUTH, or could not be started there."));

        AssertOnlyNotice(result, SecretLine,
            "Whether SOREN_SOUTH holds 'outreach-smtp-password' could not be answered: cc-secrets is not installed on SOREN_SOUTH, or could not be started there. The seat's start will check it.");
        Assert.Empty(AtLine(result, RepositoryLine));
    }

    [Theory]
    [InlineData("no Director on SOREN_SOUTH is connected now.")]
    [InlineData("the Director on SOREN_SOUTH is version 2.21.0, which is too old to be asked. Update it.")]
    [InlineData("the Director on SOREN_SOUTH did not answer within 15 seconds.")]
    public void Check_AtAPublish_AMachineThatCouldNotBeAsked_SaysWhyForTheSecretAndTheRepository(string why)
    {
        var result = Publish(MachineReply.CouldNotAsk(why));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, SecretLine,
            $"Whether SOREN_SOUTH holds 'outreach-smtp-password' could not be asked: {why} The seat's start will check it.");
        AssertOnlyNotice(result, RepositoryLine,
            $"Whether SOREN_SOUTH has the repository 'devthrottle' registered could not be asked: {why} The seat's start will check it.");
    }

    [Fact]
    public void Check_AtAPublish_TheOldWordingsAreGone()
    {
        var result = Publish(MachineReply.CouldNotAsk("no Director on SOREN_SOUTH is connected now."));

        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("cannot answer yet", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("is not checked yet", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_AtASave_KeepsThePhase1WordingAndAsksNothing()
    {
        var result = FactoryDefinitionCheck.Check(DefinitionFixture.Publishable(), DefinitionFixture.FactoryId,
            DefinitionFixture.FleetSkills, DefinitionFixture.Machines, DefinitionCheckPurpose.Save);

        AssertOnlyNotice(result, SecretLine, "Whether SOREN_SOUTH holds 'outreach-smtp-password' is not checked yet.");
        AssertOnlyNotice(result, RepositoryLine, "Whether SOREN_SOUTH has the repository 'devthrottle' registered is not checked yet.");
    }

    [Fact]
    public void Check_AtAPublish_WithoutTheMachinesAnswers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FactoryDefinitionCheck.Check(DefinitionFixture.Publishable(), DefinitionFixture.FactoryId,
            DefinitionFixture.FleetSkills, DefinitionFixture.Machines, DefinitionCheckPurpose.Publish));
    }

    [Fact]
    public void Check_AtAPublish_AMachineThatWasNotAsked_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => FactoryDefinitionCheck.Check(DefinitionFixture.Publishable(), DefinitionFixture.FactoryId,
            DefinitionFixture.FleetSkills, DefinitionFixture.Machines, DefinitionCheckPurpose.Publish,
            new Dictionary<string, MachineReply>()));
    }
}
