using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE PUBLISH ASKS THE MACHINES (Factory Definition, phase 5, design section 6): one question per machine, sent only
/// to a connected Director there that said on Hello it answers it, and every way it can go unanswered said in words.
/// The tunnel is a fake delegate; no Director, port or clock is involved.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryMachineQuestionAskerTests
{
    private static readonly TenantId A = new("acct-a");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly MachineQuestion South = new("SOREN_SOUTH", new[] { "smtp-password" }, new[] { "devthrottle" });

    private static DirectorDto Director(string id, string machine, string version = "2.22.0")
        => new() { DirectorId = id, MachineName = machine, Version = version };

    private sealed class Tunnel
    {
        public List<(string DirectorId, DirectorCommand Command)> Sent { get; } = new();
        public Func<DirectorCommand, DirectorCommandResult?> Answer { get; set; } = _ => DirectorCommandResult.Success(
            JsonSerializer.Serialize(new FactoryMachineAnswer
            {
                Machine = "SOREN_SOUTH",
                Secrets = new() { new() { Name = "smtp-password", Held = true, AllowedForRun = true } },
                Repositories = new() { new() { Name = "devthrottle", Registered = true, Path = @"D:\repos\devthrottle" } },
            }, Json));

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            Sent.Add((directorId, command));
            return Task.FromResult(Answer(command));
        }
    }

    private static async Task<MachineReply> AskSouth(Tunnel tunnel, IReadOnlyList<DirectorDto> connected, ISet<string> answering,
        ISet<string>? answeringChecks = null, MachineQuestion? question = null)
    {
        var asker = new FactoryMachineQuestionAsker(_ => connected, (_, id) => answering.Contains(id), (_, id) => answeringChecks?.Contains(id) == true, tunnel.SendAsync);
        var replies = await asker.AskAsync(A, new[] { question ?? South }, CancellationToken.None);
        return Assert.Single(replies).Value;
    }

    [Fact]
    public async Task AskAsync_ADirectorThatAnswers_IsSentTheQuestionAndItsAnswerComesBack()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("north-1", "SOREN_NORTH"), Director("south-1", "soren_south") },
            new HashSet<string> { "north-1", "south-1" });

        var (directorId, command) = Assert.Single(tunnel.Sent);
        Assert.Equal("south-1", directorId);
        Assert.Equal("factory-machine-question", command.Verb);
        var request = JsonSerializer.Deserialize<FactoryMachineQuestionRequest>(command.PayloadJson, Json)!;
        Assert.Equal(new[] { "smtp-password" }, request.Secrets);
        Assert.Equal(new[] { "devthrottle" }, request.Repositories);
        Assert.Null(reply.NotAsked);
        Assert.True(reply.Answer!.Secrets.Single().AllowedForRun);
        Assert.True(reply.Answer.Repositories.Single().Registered);
    }

    [Fact]
    public async Task AskAsync_OfTwoDirectorsOnTheMachine_OnlyTheOneThatAnswersIsAsked()
    {
        var tunnel = new Tunnel();

        await AskSouth(tunnel, new[] { Director("south-old", "SOREN_SOUTH", "2.21.0"), Director("south-new", "SOREN_SOUTH") },
            new HashSet<string> { "south-new" });

        Assert.Equal("south-new", Assert.Single(tunnel.Sent).DirectorId);
    }

    [Fact]
    public async Task AskAsync_NoDirectorConnectedOnTheMachine_SaysSoAndSendsNothing()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("north-1", "SOREN_NORTH") }, new HashSet<string> { "north-1" });

        Assert.Equal("no Director on SOREN_SOUTH is connected now.", reply.NotAsked);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public async Task AskAsync_ADirectorTooOldToBeAsked_SaysWhichVersionAndSendsNothing()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("south-old", "SOREN_SOUTH", "2.21.0") }, new HashSet<string>());

        Assert.Equal("the Director on SOREN_SOUTH is version 2.21.0, which is too old to be asked. Update it.", reply.NotAsked);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public async Task AskAsync_TheDirectorDoesNotAnswerInTime_SaysSo()
    {
        var tunnel = new Tunnel { Answer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer") };

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" });

        Assert.Equal("the Director on SOREN_SOUTH did not answer within 15 seconds.", reply.NotAsked);
    }

    [Fact]
    public async Task AskAsync_TheTunnelIsGoneWhenSending_SaysNoDirectorIsConnected()
    {
        var tunnel = new Tunnel { Answer = _ => null };

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" });

        Assert.Equal("no Director on SOREN_SOUTH is connected now.", reply.NotAsked);
    }

    [Fact]
    public async Task AskAsync_TheDirectorFails_SaysSoWithItsWords()
    {
        var tunnel = new Tunnel { Answer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Error, "the repository registry is not wired") };

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" });

        Assert.Equal("the Director on SOREN_SOUTH did not answer the question (the repository registry is not wired).", reply.NotAsked);
    }

    [Fact]
    public async Task AskAsync_AnAnswerInAnotherShape_SaysSo()
    {
        var tunnel = new Tunnel { Answer = _ => DirectorCommandResult.Success("[1, 2]") };

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" });

        Assert.Equal("the Director on SOREN_SOUTH answered in a shape the Gateway does not read.", reply.NotAsked);
    }

    [Fact]
    public async Task AskAsync_NoQuestions_AsksNobodyAndReadsNoDirectors()
    {
        var tunnel = new Tunnel();
        var read = false;
        var asker = new FactoryMachineQuestionAsker(_ => { read = true; return Array.Empty<DirectorDto>(); }, (_, _) => true, (_, _) => true, tunnel.SendAsync);

        var replies = await asker.AskAsync(A, Array.Empty<MachineQuestion>(), CancellationToken.None);

        Assert.Empty(replies);
        Assert.False(read);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public void QuestionsFor_GroupsBySeatMachine_EachNameOnce_StartSecretsIncluded_AMachineWithNothingNotAsked()
    {
        // The valid definition: the outreach seat on SOREN_SOUTH with a seat secret and a start secret; the boss and the
        // writer on the default SOREN_NORTH with neither a secret nor a repository.
        var questions = FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition, DefinitionFixture.Valid());

        var question = Assert.Single(questions);
        Assert.Equal("SOREN_SOUTH", question.Machine);
        Assert.Equal(new[] { "outreach-smtp-password", "outreach-imap-password" }, question.Secrets);
        Assert.Equal(new[] { "devthrottle" }, question.Repositories);
    }

    // ---------- phase 5b: Python and uv ----------

    private static readonly MachineQuestion SouthWithACheck = South with { AskPython = true };

    private static FactoryMachineQuestionRequest Request(Tunnel tunnel)
        => JsonSerializer.Deserialize<FactoryMachineQuestionRequest>(Assert.Single(tunnel.Sent).Command.PayloadJson, Json)!;

    private static Tunnel AnsweringPython(bool found) => new()
    {
        Answer = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(new FactoryMachineAnswer
        {
            Machine = "SOREN_SOUTH",
            Secrets = new() { new() { Name = "smtp-password", Held = true, AllowedForRun = true } },
            Repositories = new() { new() { Name = "devthrottle", Registered = true } },
            Python = new() { LookedFor = "python.exe", Found = found, Path = found ? @"C:\Python313\python.exe" : null },
        }, Json)),
    };

    [Fact]
    public async Task AskAsync_ACheckToAnswerForAndADirectorThatAnswersChecks_AsksForPythonAndCarriesItsAnswer()
    {
        var tunnel = AnsweringPython(found: false);

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" },
            answeringChecks: new HashSet<string> { "south-1" }, question: SouthWithACheck);

        var request = Request(tunnel);
        Assert.True(request.AskPython);
        Assert.False(request.AskUv);
        Assert.Null(reply.ChecksNotAnswered);
        Assert.False(reply.Answer!.Python!.Found);
        Assert.Equal("python.exe", reply.Answer.Python.LookedFor);
    }

    [Fact]
    public async Task AskAsync_ACheckToAnswerForAndAPhase5Director_AsksTheRestAndSaysItCannotAnswerAboutPythonOrUvYet()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("south-5", "SOREN_SOUTH", "2.22.0") }, new HashSet<string> { "south-5" },
            question: SouthWithACheck with { AskUv = true });

        var request = Request(tunnel);
        Assert.False(request.AskPython);
        Assert.False(request.AskUv);
        Assert.Equal(new[] { "smtp-password" }, request.Secrets);
        Assert.Null(reply.NotAsked);
        Assert.True(reply.Answer!.Secrets.Single().AllowedForRun);
        Assert.Equal("the Director on SOREN_SOUTH is version 2.22.0, which cannot answer about Python or uv yet. Update it.", reply.ChecksNotAnswered);
    }

    [Fact]
    public async Task AskAsync_ACheckToAnswerFor_PrefersTheDirectorOnTheMachineThatAnswersChecks()
    {
        var tunnel = AnsweringPython(found: true);

        await AskSouth(tunnel, new[] { Director("south-5", "SOREN_SOUTH"), Director("south-5b", "SOREN_SOUTH") },
            new HashSet<string> { "south-5", "south-5b" }, answeringChecks: new HashSet<string> { "south-5b" }, question: SouthWithACheck);

        Assert.Equal("south-5b", Assert.Single(tunnel.Sent).DirectorId);
    }

    [Fact]
    public async Task AskAsync_ADirectorThatSaysItAnswersChecksButLeavesPythonOut_AnsweredInAShapeNotRead()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" },
            answeringChecks: new HashSet<string> { "south-1" }, question: SouthWithACheck);

        Assert.Equal("the Director on SOREN_SOUTH answered in a shape the Gateway does not read.", reply.NotAsked);
    }

    [Fact]
    public async Task AskAsync_NoCheckToAnswerFor_ADirectorThatAnswersChecksIsAskedExactlyWhatPhase5Asked()
    {
        var tunnel = new Tunnel();

        var reply = await AskSouth(tunnel, new[] { Director("south-1", "SOREN_SOUTH") }, new HashSet<string> { "south-1" },
            answeringChecks: new HashSet<string> { "south-1" });

        var request = Request(tunnel);
        Assert.False(request.AskPython);
        Assert.False(request.AskUv);
        Assert.Null(reply.ChecksNotAnswered);
        Assert.Null(reply.Answer!.Python);
        Assert.Null(reply.Answer.Uv);
    }

    [Fact]
    public void QuestionsFor_ACheckWithNoPackages_AsksForPythonOnTheSeatsMachine()
    {
        var question = Assert.Single(FactoryMachineQuestionAsker.QuestionsFor(
            FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition, DefinitionFixture.Valid()));

        Assert.True(question.AskPython);
        Assert.False(question.AskUv);
    }

    [Fact]
    public void QuestionsFor_ACheckThatDeclaresPackages_AsksForUvAndNotPython()
    {
        var content = DefinitionFixture.With("checks/new_mail.py", WithPackages);

        var question = Assert.Single(FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content));

        Assert.True(question.AskUv);
        Assert.False(question.AskPython);
    }

    [Fact]
    public void QuestionsFor_ACheckWhoseHeaderNeverCloses_AsksForNeither()
    {
        var content = DefinitionFixture.With("checks/new_mail.py", "# /// script\n# dependencies = [\"requests\"]\nprint('{}')\n");

        var question = Assert.Single(FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content));

        Assert.False(question.AskPython);
        Assert.False(question.AskUv);
    }

    [Fact]
    public void QuestionsFor_AStartWhoseCheckNamesAFileOutsideChecks_AsksForNeither()
    {
        // files/outreach_send.py is in the definition, but not as a check: the definition check refuses the start, and
        // no run will ever look for a program for it.
        var content = DefinitionFixture.Edit("seats/outreach.yaml", "check: checks/new_mail.py", "check: files/outreach_send.py");

        var question = Assert.Single(FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content));

        Assert.False(question.AskPython);
        Assert.False(question.AskUv);
    }

    [Fact]
    public void QuestionsFor_AFactoryWithNoChecks_AsksForNeither()
    {
        var content = DefinitionFixture.Publishable();

        var question = Assert.Single(FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content));

        Assert.False(question.AskPython);
        Assert.False(question.AskUv);
    }

    /// <summary>A check whose PEP 723 header declares one package.</summary>
    internal const string WithPackages = "# /// script\n# dependencies = [\"requests\"]\n# ///\nprint('{\"count\": 0}')\n";

    [Fact]
    public void QuestionsFor_ADefinitionThatDoesNotParse_RaisesNone()
    {
        Assert.Empty(FactoryMachineQuestionAsker.QuestionsFor(null, new Dictionary<string, string>()));
    }
}
