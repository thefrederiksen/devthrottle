using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Triggers;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.Factory;

/// <summary>
/// Factory Definition, phase 5: the Director's <c>factory-machine-question</c> verb. The question runs against a fake
/// cc-secrets, never a real process; the end-to-end case through the real dispatcher asks only about repositories, so
/// the production question starts no process either.
/// </summary>
public sealed class FactoryMachineQuestionExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Listed = """{"entries": [{"name": "smtp-password", "uses": ["run"]}, {"name": "portal-login", "uses": ["login"]}]}""";

    private static DirectorCommand Command(string payload) => new()
    {
        CommandId = "c1",
        Verb = FactoryMachineQuestionContract.Verb,
        SessionId = "",
        PayloadJson = payload,
    };

    private static FactoryMachineQuestionExecutor Executor(string stdout = Listed, FakeProcesses? processes = null, bool isWindows = true)
        => new(new FactoryMachineQuestion(_ => Task.FromResult(new ProcessRunner.Result(0, stdout, "", Started: true)),
            TimeSpan.FromSeconds(10)), "SOREN_SOUTH", processes ?? new FakeProcesses(), isWindows);

    /// <summary>The check runner's seam with programs "on PATH" by name. It starts nothing: a run is a test defect.</summary>
    private sealed class FakeProcesses : ICheckProcesses
    {
        public Dictionary<string, string> OnPath { get; } = new(StringComparer.Ordinal);
        public List<string> LookedFor { get; } = new();

        public string? FindOnPath(string name)
        {
            LookedFor.Add(name);
            return OnPath.TryGetValue(name, out var path) ? path : null;
        }

        public string? FindCcSecrets() => throw new InvalidOperationException("the machine question finds cc-secrets its own way");

        public Task<CheckProcessResult> RunAsync(CheckProcessStart start, CancellationToken ct)
            => throw new InvalidOperationException("the machine question never runs Python or uv, it only looks for them");
    }

    private static FactoryMachineAnswer Answer(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<FactoryMachineAnswer>(result.BodyJson!, Json)!;
    }

    [Fact]
    public void AreaFor_TheMachineQuestionVerb_IsOwnedByTheMachineQuestionArea()
    {
        Assert.IsType<FactoryMachineQuestionExecutor>(SessionCommandExecutor.AreaFor("factory-machine-question"));
    }

    [Fact]
    public async Task AnswerAsync_SecretsAndRepositories_AnswersEachInTheOrderAsked()
    {
        var registered = new[] { new RepositoryConfig { Name = "devthrottle", Path = @"D:\repos\devthrottle" } };
        var payload = """{"secrets": ["portal-login", "smtp-password", "imap-password"], "repositories": ["devthrottle", "website"]}""";

        var answer = Answer(await Executor().AnswerAsync(Command(payload), registered, CancellationToken.None));

        Assert.Equal("SOREN_SOUTH", answer.Machine);
        Assert.Null(answer.SecretsUnavailable);
        Assert.Equal(new[] { "portal-login", "smtp-password", "imap-password" }, answer.Secrets.Select(s => s.Name));
        Assert.Equal(new[] { true, true, false }, answer.Secrets.Select(s => s.Held));
        Assert.Equal(new[] { false, true, false }, answer.Secrets.Select(s => s.AllowedForRun));
        Assert.Equal("Secret 'portal-login' is not allowed for 'run'. It may be used for: login.", answer.Secrets[0].NotAllowedReason);
        Assert.Equal(new[] { true, false }, answer.Repositories.Select(r => r.Registered));
        Assert.Equal(@"D:\repos\devthrottle", answer.Repositories[0].Path);
    }

    [Fact]
    public async Task AnswerAsync_PythonAndUvNotAsked_AnswersNeitherAndLooksForNothing()
    {
        var processes = new FakeProcesses();

        var answer = Answer(await Executor(processes: processes).AnswerAsync(
            Command("""{"secrets": [], "repositories": ["devthrottle"]}"""), Array.Empty<RepositoryConfig>(), CancellationToken.None));

        Assert.Null(answer.Python);
        Assert.Null(answer.Uv);
        Assert.Empty(processes.LookedFor);
    }

    [Fact]
    public async Task AnswerAsync_PythonAndUvAskedAndHeld_AnswersWhereTheRunnerWouldFindThem()
    {
        var processes = new FakeProcesses();
        processes.OnPath["python.exe"] = @"C:\Python313\python.exe";
        processes.OnPath["uv.exe"] = @"C:\tools\uv.exe";

        var answer = Answer(await Executor(processes: processes).AnswerAsync(
            Command("""{"secrets": [], "repositories": [], "askPython": true, "askUv": true}"""), Array.Empty<RepositoryConfig>(), CancellationToken.None));

        Assert.Equal("python.exe", answer.Python!.LookedFor);
        Assert.True(answer.Python.Found);
        Assert.Equal(@"C:\Python313\python.exe", answer.Python.Path);
        Assert.Equal("uv.exe", answer.Uv!.LookedFor);
        Assert.True(answer.Uv.Found);
        Assert.Equal(@"C:\tools\uv.exe", answer.Uv.Path);
    }

    [Fact]
    public async Task AnswerAsync_PythonAndUvAskedAndMissingOffWindows_SaysTheRunnersOwnNamesWereNotFound()
    {
        var processes = new FakeProcesses();
        // The Windows names on PATH do not count off Windows: the runner looks for python3 and uv there.
        processes.OnPath["python.exe"] = "/wrong/python.exe";
        processes.OnPath["uv.exe"] = "/wrong/uv.exe";

        var answer = Answer(await Executor(processes: processes, isWindows: false).AnswerAsync(
            Command("""{"secrets": [], "repositories": [], "askPython": true, "askUv": true}"""), Array.Empty<RepositoryConfig>(), CancellationToken.None));

        Assert.Equal("python3", answer.Python!.LookedFor);
        Assert.False(answer.Python.Found);
        Assert.Null(answer.Python.Path);
        Assert.Equal("uv", answer.Uv!.LookedFor);
        Assert.False(answer.Uv.Found);
        Assert.Equal(new[] { "python3", "uv" }, processes.LookedFor);
    }

    [Fact]
    public async Task AnswerAsync_CcSecretsAnswersInAnotherShape_TheVerbStillAnswersAndSaysSo()
    {
        var answer = Answer(await Executor(stdout: "oops").AnswerAsync(Command("""{"secrets": ["smtp-password"], "repositories": []}"""),
            Array.Empty<RepositoryConfig>(), CancellationToken.None));

        Assert.Equal("cc-secrets on SOREN_SOUTH answered in a shape the Director does not read; update cc-secrets there.", answer.SecretsUnavailable);
        Assert.Empty(answer.Secrets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"secrets\": null, \"repositories\": []}")]
    [InlineData("{\"secrets\": [\"\"], \"repositories\": []}")]
    [InlineData("{\"secrets\": [], \"repositories\": [\" \"]}")]
    public async Task AnswerAsync_AnUnreadablePayload_IsBadRequest(string payload)
    {
        var result = await Executor().AnswerAsync(Command(payload), Array.Empty<RepositoryConfig>(), CancellationToken.None);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("the payload must be a factory machine question: secrets and repositories, each a list of names", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_TheVerb_GoesThroughTheRealCommandExecutorToTheAnswer()
    {
        // The registry is never loaded or written: nothing on disk is touched, and nothing is registered in it.
        var registry = new RepositoryRegistry(Path.Combine(Path.GetTempPath(), "never-written-" + Guid.NewGuid().ToString("N") + ".json"));
        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("""{"secrets": [], "repositories": ["devthrottle"]}"""),
                new SessionCommandServices { Repositories = registry });

            var answer = Answer(result);
            Assert.Equal("c1", result.CommandId);
            Assert.Equal(Environment.MachineName, answer.Machine);
            var repository = Assert.Single(answer.Repositories);
            Assert.Equal("devthrottle", repository.Name);
            Assert.False(repository.Registered);
            Assert.Empty(answer.Secrets);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoRepositoryRegistryWired_ThrowsRatherThanAnsweringNotRegistered()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteAsync(default, Command("""{"secrets": [], "repositories": ["devthrottle"]}"""), CancellationToken.None));
    }
}
