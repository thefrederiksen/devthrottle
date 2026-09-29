using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE WINGMAN NAMES A SESSION THE USER DID NOT NAME (issue #3488): after an accepted turn-end reading, an unnamed
/// session gets a short name from its first prompt; a name a person gave is never touched, and a session is named at
/// most once.
/// </summary>
public sealed class SessionNamingServiceTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Sid = "5d138b9b-3287-464a-8205-ee8c39dee86c";
    private const string Director = "dir-1";

    [Fact]
    public async Task AnUnnamedSession_IsNamedFromItsFirstPrompt()
    {
        var env = new FakeNamingEnvironment { Reply = "Wingman Session Naming" };
        var sut = new SessionNamingService(env);

        var name = await sut.NameIfUnnamedAsync(TurnEnd());

        Assert.Equal("Wingman Session Naming", name);
        Assert.Equal(new[] { (Director, Sid, "Wingman Session Naming") }, env.Renames);
        Assert.Contains("add a github issue and implement naming", env.Prompts.Single());
    }

    [Fact]
    public async Task ASessionStillMarkedAutoNamed_IsNamed()
    {
        var env = new FakeNamingEnvironment { Reply = "Fix The Login Page" };
        env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "devthrottle / 1fb5", IsAutoNamed = true };
        var sut = new SessionNamingService(env);

        Assert.Equal("Fix The Login Page", await sut.NameIfUnnamedAsync(TurnEnd()));
    }

    [Fact]
    public async Task ANameThePersonGave_IsNeverTouched_AndNeverAskedAbout()
    {
        var env = new FakeNamingEnvironment { Reply = "Something Else" };
        env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "My Own Name", IsAutoNamed = false };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Prompts);
        Assert.Empty(env.Renames);
    }

    [Theory]
    [InlineData(TurnVerdictTrigger.Voice)]
    [InlineData(TurnVerdictTrigger.Sweep)]
    [InlineData(TurnVerdictTrigger.OnDemand)]
    [InlineData(TurnVerdictTrigger.SnoozeExpiry)]
    [InlineData(TurnVerdictTrigger.Retry)]
    public async Task OnlyATurnEndNamesASession(TurnVerdictTrigger trigger)
    {
        var env = new FakeNamingEnvironment { Reply = "A Name" };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd(trigger: trigger)));
        Assert.Empty(env.Prompts);
    }

    [Theory]
    [InlineData(TurnVerdictOutcomeKind.Skipped, false)]
    [InlineData(TurnVerdictOutcomeKind.Failed, true)]
    [InlineData(TurnVerdictOutcomeKind.Cancelled, false)]
    [InlineData(TurnVerdictOutcomeKind.Judged, true)]
    public async Task AReadingThatWasNotAccepted_NamesNothing(TurnVerdictOutcomeKind kind, bool failedVerdict)
    {
        // Judged with a FAILED verdict is not accepted either: the Wingman did not answer for this account.
        var env = new FakeNamingEnvironment { Reply = "A Name" };
        var sut = new SessionNamingService(env);
        var outcome = new TurnVerdictOutcome
        {
            Kind = kind,
            Verdict = kind is TurnVerdictOutcomeKind.Skipped or TurnVerdictOutcomeKind.Cancelled
                ? null
                : new TurnVerdictDto { Failed = failedVerdict },
        };

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd(outcome: outcome)));
        Assert.Empty(env.Prompts);
    }

    [Fact]
    public async Task AHeldSession_IsNotNamed()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name", Held = true };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Prompts);
    }

    [Fact]
    public async Task AnAccountWithTheJudgeSwitchOff_PaysForNoNamingCall()
    {
        // A voice session is read with the switch off; naming must not add a call the account turned off.
        var env = new FakeNamingEnvironment { Reply = "A Name", Judge = false };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Prompts);
    }

    [Fact]
    public async Task ASessionIsAskedAboutAtMostOnce()
    {
        var env = new FakeNamingEnvironment { Reply = "   " }; // an unusable reply
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        env.Reply = "A Good Name";
        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));

        Assert.Single(env.Prompts);
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task NoStoredPromptYet_AsksAgainAtTheNextTurnEnd()
    {
        var env = new FakeNamingEnvironment { Reply = "Late Name", Conversation = Conversation() };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Prompts);

        env.Conversation = Conversation(("UserMessage", "now do the thing"));
        Assert.Equal("Late Name", await sut.NameIfUnnamedAsync(TurnEnd()));
    }

    [Fact]
    public async Task ARenameWhileTheModelAnswers_Wins()
    {
        var env = new FakeNamingEnvironment { Reply = "Model Name" };
        env.OnAsk = () => env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "Typed By Hand" };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task AModelFailure_NamesNothing_AndDoesNotThrow()
    {
        var env = new FakeNamingEnvironment { Throw = new TimeoutException("no answer") };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task ARenameTheDirectorRefuses_IsReportedAsNoName()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name", RenameError = "session not found" };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(TurnEnd()));
        Assert.Single(env.Renames);
    }

    [Fact]
    public void FirstUserPrompt_SkipsMachineTextOnTheUsersSide()
    {
        var c = Conversation(
            ("UserMessage", "<system-reminder>\nhook context\n</system-reminder>"),
            ("UserMessage", "Caveat: The messages below were generated by the user while running local commands."),
            ("UserMessage", "<command-name>/clear</command-name>\n<command-message>clear</command-message>"),
            ("UserMessage", "Base directory for this skill: C:\\skills\\x\n\n# A skill body"),
            ("UserMessage", "This session is being continued from a previous conversation that ran out of context."),
            ("UserMessage", "<task-notification>\n<task-id>b1</task-id>\n</task-notification>"),
            ("Text", "agent words"),
            ("UserMessage", "<system-reminder>x</system-reminder>\nplease fix the build"));

        Assert.Equal("please fix the build", SessionNamingService.FirstUserPrompt(c));
    }

    [Fact]
    public void FirstUserPrompt_IsCapped()
    {
        var c = Conversation(("UserMessage", new string('a', SessionNamingService.MaxPromptChars + 50)));
        Assert.Equal(SessionNamingService.MaxPromptChars, SessionNamingService.FirstUserPrompt(c)!.Length);
    }

    [Theory]
    [InlineData("Fix The Login Page", "Fix The Login Page")]
    [InlineData("\"Fix The Login Page\"", "Fix The Login Page")]
    [InlineData("Name: Fix The Login Page.", "Fix The Login Page")]
    [InlineData("\n\n  **Fix   the  build**  \nbecause the user asked", "Fix the build")]
    [InlineData("Café Menu Redesign", "Caf Menu Redesign")]
    [InlineData("   ", null)]
    [InlineData("\"...\"", null)]
    [InlineData(null, null)]
    public void CleanName_MakesAPlainOneLineName(string? reply, string? expected)
        => Assert.Equal(expected, SessionNamingService.CleanName(reply));

    [Fact]
    public void CleanName_CapsALongReplyOnAWordBoundary()
    {
        var reply = string.Join(' ', Enumerable.Repeat("Word", 30));
        var name = SessionNamingService.CleanName(reply)!;
        Assert.True(name.Length <= SessionNamingService.MaxNameLength);
        Assert.EndsWith("Word", name);
    }

    private static TurnVerdictReadingCompleted TurnEnd(
        TurnVerdictTrigger trigger = TurnVerdictTrigger.TurnEnd, TurnVerdictOutcome? outcome = null)
        => new(Tenant, Sid, Director, trigger, DateTime.UtcNow,
            outcome ?? new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Judged, Verdict = new TurnVerdictDto() });

    private static StoredConversation Conversation(params (string Kind, string Content)[] widgets)
        => new(true, widgets.Select(w => new TurnWidgetDto { Kind = w.Kind, Content = w.Content }).ToList());

    private sealed class FakeNamingEnvironment : ISessionNamingEnvironment
    {
        public SessionDto? Facts = new() { SessionId = Sid, DirectorId = Director, Name = null };
        public StoredConversation? Conversation =
            SessionNamingServiceTests.Conversation(("UserMessage", "add a github issue and implement naming"));
        public string? Reply;
        public Exception? Throw;
        public string? RenameError;
        public Action? OnAsk;
        public readonly List<string> Prompts = new();
        public readonly List<(string Director, string Sid, string Name)> Renames = new();

        public bool Held;
        public bool Judge = true;

        public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId) => new(Facts, Held);

        public bool JudgeEnabled(TenantId tenant) => Judge;

        public StoredConversation? ReadConversation(TenantId tenant, string sessionId) => Conversation;

        public Task<string> AskNamerAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
        {
            Prompts.Add(prompt);
            OnAsk?.Invoke();
            if (Throw is not null) throw Throw;
            return Task.FromResult(Reply ?? "");
        }

        public Task<string?> RenameSessionAsync(TenantId tenant, string directorId, string sessionId, string name, CancellationToken ct)
        {
            Renames.Add((directorId, sessionId, name));
            return Task.FromResult(RenameError);
        }
    }
}
