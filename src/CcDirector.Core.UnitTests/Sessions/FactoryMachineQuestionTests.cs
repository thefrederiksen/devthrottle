using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE MACHINE QUESTION, answered for this machine (Factory Definition, phase 5, design section 6). Every test runs
/// against a faked <c>cc-secrets list --json</c> and a hand-built repository list - never a real process and never a
/// real secret store. The faked output has the shape the installed cc-secrets prints (names, uses, and a setting's
/// value), and a planted marker in the setting's value proves no value ever reaches the answer.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryMachineQuestionTests
{
    private const string Machine = "SOREN_SOUTH";
    private const string ValueMarker = "VALUE-MARKER-5e2a";

    // The shape `cc-secrets list --json` prints on this machine: one object with an entries array.
    private static readonly string Listed = $$"""
        {"entries": [
          {"name": "smtp-password", "username": "", "allowedDomains": [], "uses": ["run"], "notes": "", "envName": "SMTP_PASSWORD", "kind": "secret", "updatedUtc": "2026-10-01T00:00:00+00:00"},
          {"name": "portal-login", "username": "me", "allowedDomains": ["https://portal.example"], "uses": ["login"], "notes": "", "envName": "PORTAL_LOGIN", "kind": "secret", "updatedUtc": "2026-10-01T00:00:00+00:00"},
          {"name": "no-uses", "username": "", "allowedDomains": [], "uses": [], "notes": "", "envName": "NO_USES", "kind": "secret", "updatedUtc": "2026-10-01T00:00:00+00:00"},
          {"name": "smtp-host", "username": "", "allowedDomains": [], "uses": ["run"], "notes": "", "envName": "SMTP_HOST", "kind": "setting", "value": "{{ValueMarker}}", "updatedUtc": "2026-10-01T00:00:00+00:00"}
        ]}
        """;

    private static readonly IReadOnlyList<RepositoryConfig> Registered = new[]
    {
        new RepositoryConfig { Name = "DevThrottle", Path = @"D:\repos\devthrottle" },
        new RepositoryConfig { Name = "website", Path = @"D:\repos\website" },
    };

    private sealed class FakeList
    {
        private readonly Func<CancellationToken, Task<ProcessRunner.Result>> _answer;
        public int Runs { get; private set; }

        public FakeList(Func<CancellationToken, Task<ProcessRunner.Result>> answer) => _answer = answer;

        public static FakeList Printing(string stdout, int exitCode = 0)
            => new(_ => Task.FromResult(new ProcessRunner.Result(exitCode, stdout, "", Started: true)));

        public Task<ProcessRunner.Result> RunAsync(CancellationToken ct)
        {
            Runs++;
            return _answer(ct);
        }
    }

    private static Task<Gateway.Contracts.FactoryMachineAnswer> Ask(FakeList list, string[] secrets, string[]? repositories = null,
        TimeSpan? timeout = null)
        => new FactoryMachineQuestion(list.RunAsync, timeout ?? TimeSpan.FromSeconds(10))
            .AnswerAsync(Machine, secrets, repositories ?? Array.Empty<string>(), Registered, CancellationToken.None);

    [Fact]
    public async Task AnswerAsync_AHeldSecretAllowedForRun_IsHeldAndAllowed()
    {
        var answer = await Ask(FakeList.Printing(Listed), new[] { "smtp-password" });

        var secret = Assert.Single(answer.Secrets);
        Assert.Equal("smtp-password", secret.Name);
        Assert.True(secret.Held);
        Assert.True(secret.AllowedForRun);
        Assert.Null(secret.NotAllowedReason);
        Assert.Null(answer.SecretsUnavailable);
        Assert.Equal(Machine, answer.Machine);
    }

    [Fact]
    public async Task AnswerAsync_ASecretNotListed_IsNotHeld()
    {
        var answer = await Ask(FakeList.Printing(Listed), new[] { "imap-password" });

        var secret = Assert.Single(answer.Secrets);
        Assert.False(secret.Held);
        Assert.False(secret.AllowedForRun);
        Assert.Null(secret.NotAllowedReason);
    }

    [Fact]
    public async Task AnswerAsync_AHeldSecretNotAllowedForRun_SaysSoInTheToolsOwnWords()
    {
        var answer = await Ask(FakeList.Printing(Listed), new[] { "portal-login", "no-uses" });

        Assert.Collection(answer.Secrets,
            s =>
            {
                Assert.True(s.Held);
                Assert.False(s.AllowedForRun);
                Assert.Equal("Secret 'portal-login' is not allowed for 'run'. It may be used for: login.", s.NotAllowedReason);
            },
            s => Assert.Equal("Secret 'no-uses' is not allowed for 'run'. It may be used for: .", s.NotAllowedReason));
    }

    [Fact]
    public async Task AnswerAsync_ASettingWithItsValueInTheList_NoValueReachesTheAnswer()
    {
        var answer = await Ask(FakeList.Printing(Listed), new[] { "smtp-host" });

        Assert.True(Assert.Single(answer.Secrets).AllowedForRun);
        var json = System.Text.Json.JsonSerializer.Serialize(answer);
        Assert.DoesNotContain(ValueMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerAsync_CcSecretsNotInstalled_SaysSoAndNeverNotHeld()
    {
        var list = new FakeList(_ => Task.FromResult(new ProcessRunner.Result(-1, "", "", Started: false)));

        var answer = await Ask(list, new[] { "smtp-password" });

        Assert.Equal("cc-secrets is not installed on SOREN_SOUTH, or could not be started there.", answer.SecretsUnavailable);
        Assert.Empty(answer.Secrets);
    }

    [Fact]
    public async Task AnswerAsync_CcSecretsFails_SaysWhichExitCode()
    {
        var answer = await Ask(FakeList.Printing("Traceback (most recent call last):", exitCode: 1), new[] { "smtp-password" });

        Assert.Equal("cc-secrets list failed on SOREN_SOUTH (exit 1).", answer.SecretsUnavailable);
        Assert.Empty(answer.Secrets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"entries\": {}}")]
    [InlineData("{\"entries\": [{\"name\": \"smtp-password\"}]}")]
    [InlineData("{\"entries\": [{\"name\": \"smtp-password\", \"uses\": [1]}]}")]
    public async Task AnswerAsync_AnAnswerInAnotherShape_SaysItCannotBeRead(string stdout)
    {
        var answer = await Ask(FakeList.Printing(stdout), new[] { "smtp-password" });

        Assert.Equal("cc-secrets on SOREN_SOUTH answered in a shape the Director does not read; update cc-secrets there.", answer.SecretsUnavailable);
        Assert.Empty(answer.Secrets);
    }

    [Fact]
    public async Task AnswerAsync_CcSecretsDoesNotFinishInTime_SaysSo()
    {
        // The fake never answers on its own: it ends only when the question's limit cancels it.
        var list = new FakeList(ct =>
        {
            var never = new TaskCompletionSource<ProcessRunner.Result>();
            ct.Register(() => never.TrySetCanceled(ct));
            return never.Task;
        });

        var answer = await Ask(list, new[] { "smtp-password" }, timeout: TimeSpan.Zero);

        Assert.Equal("cc-secrets on SOREN_SOUTH did not answer within 0 seconds.", answer.SecretsUnavailable);
    }

    [Fact]
    public async Task AnswerAsync_NoSecretAsked_RunsNoCcSecrets()
    {
        var list = FakeList.Printing(Listed);

        var answer = await Ask(list, Array.Empty<string>(), new[] { "website" });

        Assert.Equal(0, list.Runs);
        Assert.Empty(answer.Secrets);
        Assert.Null(answer.SecretsUnavailable);
    }

    [Fact]
    public async Task AnswerAsync_Repositories_RegisteredWithTheirPathAndAnyCase_OthersNot()
    {
        var answer = await Ask(FakeList.Printing(Listed), Array.Empty<string>(), new[] { "devthrottle", "not-here" });

        Assert.Collection(answer.Repositories,
            r =>
            {
                Assert.Equal("devthrottle", r.Name);
                Assert.True(r.Registered);
                Assert.Equal(@"D:\repos\devthrottle", r.Path);
            },
            r =>
            {
                Assert.Equal("not-here", r.Name);
                Assert.False(r.Registered);
                Assert.Null(r.Path);
            });
    }

    [Fact]
    public async Task AnswerAsync_CcSecretsUnavailable_StillAnswersTheRepositories()
    {
        var list = new FakeList(_ => Task.FromResult(new ProcessRunner.Result(-1, "", "", Started: false)));

        var answer = await Ask(list, new[] { "smtp-password" }, new[] { "website" });

        Assert.NotNull(answer.SecretsUnavailable);
        Assert.True(Assert.Single(answer.Repositories).Registered);
    }
}
