using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE PUBLISH NOTICES FOR A START'S CHECK (Factory Definition, phase 5b, design section 4). For a start with a check,
/// the publish asked the seat's machine for the start's own secrets (held, and allowed for run) and for the program
/// the check runs under - Python, or uv when the check's header declares packages - and the check words each answer
/// as a NOTICE on the start's own line, never an error, because the check's run is where it binds.
///
/// The fixture's outreach seat runs on SOREN_SOUTH; its interval start names the check on line 13 and the start's
/// secret 'outreach-imap-password' on line 14; the seat's own secret is on line 18 and its repository on line 8.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckCheckMachineQuestionTests
{
    private const string Seat = "seats/outreach.yaml";
    private const int RepositoryLine = 8;
    private const int CheckLine = 13;
    private const int StartSecretLine = 14;
    private const int SeatSecretLine = 18;
    private const string StartSecret = "outreach-imap-password";
    private const string SeatSecret = "outreach-smtp-password";

    private static FactoryDefinitionCheckResult Publish(MachineReply southReply, Dictionary<string, string>? content = null)
        => FactoryDefinitionCheck.Check(content ?? DefinitionFixture.Valid(), DefinitionFixture.FactoryId, DefinitionFixture.FleetSkills,
            DefinitionFixture.Machines, DefinitionCheckPurpose.Publish,
            new Dictionary<string, MachineReply>(StringComparer.OrdinalIgnoreCase) { ["SOREN_SOUTH"] = southReply });

    private static FactorySecretAnswer Allowed(string name) => new() { Name = name, Held = true, AllowedForRun = true };

    private static MachineReply Answered(FactorySecretAnswer? startSecret = null, FactoryProgramAnswer? python = null,
        FactoryProgramAnswer? uv = null)
        => MachineReply.Answered(new FactoryMachineAnswer
        {
            Machine = "SOREN_SOUTH",
            Secrets = new List<FactorySecretAnswer> { Allowed(SeatSecret), startSecret ?? Allowed(StartSecret) },
            Repositories = new List<FactoryRepositoryAnswer> { new() { Name = "devthrottle", Registered = true, Path = @"D:\repos\devthrottle" } },
            Python = python,
            Uv = uv,
        });

    private static FactoryProgramAnswer Found(string name) => new() { LookedFor = name, Found = true, Path = @"C:\tools\" + name };

    private static FactoryProgramAnswer Missing(string name) => new() { LookedFor = name, Found = false };

    private static IEnumerable<DefinitionFinding> AtLine(FactoryDefinitionCheckResult result, int line)
        => result.Findings.Where(f => f.File == Seat && f.Line == line);

    private static void AssertOnlyNotice(FactoryDefinitionCheckResult result, int line, string message)
    {
        var finding = Assert.Single(AtLine(result, line));
        Assert.False(finding.IsError);
        Assert.Equal(message, finding.Message);
    }

    [Fact]
    public void Check_AtAPublish_AMachineThatHoldsEverything_IsNoNoticeOnAnyLineTheQuestionCovers()
    {
        var result = Publish(Answered(python: Found("python.exe")));

        Assert.False(result.HasErrors);
        Assert.Empty(AtLine(result, CheckLine));
        Assert.Empty(AtLine(result, StartSecretLine));
        Assert.Empty(AtLine(result, SeatSecretLine));
        Assert.Empty(AtLine(result, RepositoryLine));
    }

    [Fact]
    public void Check_AtAPublish_NoPythonForACheckWithNoPackages_IsANoticeOnTheCheckLine()
    {
        var result = Publish(Answered(python: Missing("python.exe")));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, CheckLine,
            "SOREN_SOUTH has no Python (looked for python.exe on PATH), and the check 'checks/new_mail.py' runs under it. Install Python there; the check's run will fail without it.");
    }

    [Fact]
    public void Check_AtAPublish_NoUvForACheckThatDeclaresPackages_IsANoticeOnTheCheckLine()
    {
        var content = DefinitionFixture.With("checks/new_mail.py", FactoryMachineQuestionAskerTests.WithPackages);

        var result = Publish(Answered(uv: Missing("uv")), content);

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, CheckLine,
            "SOREN_SOUTH has no uv (looked for uv on PATH), and the check 'checks/new_mail.py' declares packages, so it runs under uv. Install uv there; the check's run will fail without it.");
    }

    [Fact]
    public void Check_AtAPublish_UvFoundForACheckThatDeclaresPackages_IsNoNoticeAndPythonIsNotNeeded()
    {
        var content = DefinitionFixture.With("checks/new_mail.py", FactoryMachineQuestionAskerTests.WithPackages);

        // No Python in the answer at all: a check that declares packages runs under uv, so Python was not asked.
        var result = Publish(Answered(uv: Found("uv.exe")), content);

        Assert.False(result.HasErrors);
        Assert.Empty(AtLine(result, CheckLine));
    }

    [Fact]
    public void Check_AtAPublish_AStartSecretTheMachineDoesNotHold_IsANoticeOnTheStartsSecretsLine()
    {
        var result = Publish(Answered(new FactorySecretAnswer { Name = StartSecret, Held = false, AllowedForRun = false }, python: Found("python.exe")));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, StartSecretLine,
            "SOREN_SOUTH does not hold the secret 'outreach-imap-password'. Add it there with cc-secrets; the check's run will refuse without it.");
        Assert.Empty(AtLine(result, SeatSecretLine));
    }

    [Fact]
    public void Check_AtAPublish_AStartSecretAllowedForLoginOnly_IsANoticeInTheToolsOwnWords()
    {
        var result = Publish(Answered(new FactorySecretAnswer
        {
            Name = StartSecret, Held = true, AllowedForRun = false,
            NotAllowedReason = "Secret 'outreach-imap-password' is not allowed for 'run'. It may be used for: login.",
        }, python: Found("python.exe")));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, StartSecretLine,
            "SOREN_SOUTH holds 'outreach-imap-password', but cc-secrets will not let the check use it: Secret 'outreach-imap-password' is not allowed for 'run'. It may be used for: login. The check's run will refuse it.");
    }

    [Fact]
    public void Check_AtAPublish_APhase5DirectorThatCannotAnswerAboutPython_IsANoticeOnTheCheckLine_AndItsOtherAnswersStand()
    {
        const string tooOld = "the Director on SOREN_SOUTH is version 2.22.0, which cannot answer about Python or uv yet. Update it.";

        var result = Publish(Answered() with { ChecksNotAnswered = tooOld });

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, CheckLine,
            $"Whether SOREN_SOUTH has the Python the check 'checks/new_mail.py' runs under could not be asked: {tooOld} The check's run will check it.");
        Assert.Empty(AtLine(result, StartSecretLine));
        Assert.Empty(AtLine(result, SeatSecretLine));
    }

    [Fact]
    public void Check_AtAPublish_AMachineThatCouldNotBeAsked_SaysWhyOnTheCheckLineAndTheStartsSecretsLine()
    {
        const string why = "no Director on SOREN_SOUTH is connected now.";

        var result = Publish(MachineReply.CouldNotAsk(why));

        Assert.False(result.HasErrors);
        AssertOnlyNotice(result, CheckLine,
            $"Whether SOREN_SOUTH has the Python the check 'checks/new_mail.py' runs under could not be asked: {why} The check's run will check it.");
        AssertOnlyNotice(result, StartSecretLine,
            $"Whether SOREN_SOUTH holds 'outreach-imap-password' could not be asked: {why} The check's run will check it.");
        AssertOnlyNotice(result, SeatSecretLine,
            $"Whether SOREN_SOUTH holds 'outreach-smtp-password' could not be asked: {why} The seat's start will check it.");
    }

    [Fact]
    public void Check_AtAPublish_ACheckWhoseHeaderNeverCloses_IsNoProgramNotice()
    {
        // The run refuses such a check on its header, whatever programs the machine has, so no program was asked.
        var content = DefinitionFixture.With("checks/new_mail.py", "# /// script\n# dependencies = [\"requests\"]\nprint('{}')\n");

        var result = Publish(Answered(), content);

        Assert.Empty(AtLine(result, CheckLine));
    }

    [Fact]
    public void Check_AtASave_SaysNothingAboutPythonOrUv()
    {
        var result = FactoryDefinitionCheck.Check(DefinitionFixture.Valid(), DefinitionFixture.FactoryId,
            DefinitionFixture.FleetSkills, DefinitionFixture.Machines, DefinitionCheckPurpose.Save);

        Assert.Empty(AtLine(result, CheckLine));
        AssertOnlyNotice(result, StartSecretLine, "Whether SOREN_SOUTH holds 'outreach-imap-password' is not checked yet.");
    }

    [Fact]
    public void Check_AtAPublish_AFactoryWithNoChecks_GivesExactlyThePhase5Answers()
    {
        // Goal 14: a definition with no checked start raises no Python or uv question and no notice about either. The
        // reply carries no Python and no uv, as a phase 5 Director's would.
        var content = DefinitionFixture.Publishable();
        var reply = MachineReply.Answered(new FactoryMachineAnswer
        {
            Machine = "SOREN_SOUTH",
            Secrets = new List<FactorySecretAnswer> { new() { Name = SeatSecret, Held = false } },
            Repositories = new List<FactoryRepositoryAnswer> { new() { Name = "devthrottle", Registered = true } },
        });

        var result = Publish(reply, content);

        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("Python", StringComparison.Ordinal) || f.Message.Contains(" uv", StringComparison.Ordinal));
        AssertOnlyNotice(result, SeatSecretLine,
            "SOREN_SOUTH does not hold the secret 'outreach-smtp-password'. Add it there with cc-secrets; the seat's start will refuse without it.");
    }
}
