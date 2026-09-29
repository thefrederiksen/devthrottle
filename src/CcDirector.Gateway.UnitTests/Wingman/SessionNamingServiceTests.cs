using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE WINGMAN NAMES A SESSION THE USER DID NOT NAME, FROM THE USER'S FIRST PROMPT (issue #3488): on the Working edge
/// that follows the user pressing Enter, an unnamed session gets a short name from its first prompt; a name a person
/// gave is never touched, and a session is asked about at most once.
/// </summary>
public sealed class SessionNamingServiceTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Sid = "5d138b9b-3287-464a-8205-ee8c39dee86c";
    private const string Director = "dir-1";

    [Fact]
    public async Task AnUnnamedSession_IsNamedFromItsFirstPrompt_OnTheWorkingEdge()
    {
        var env = new FakeNamingEnvironment { Reply = "Wingman Session Naming" };
        var sut = new SessionNamingService(env);

        var name = await sut.NameIfUnnamedAsync(Tenant, Sid, Director);

        Assert.Equal("Wingman Session Naming", name);
        Assert.Equal(new[] { (Director, Sid, "Wingman Session Naming") }, env.Renames);
        Assert.Contains("add a github issue and implement naming", env.Prompts.Single());
    }

    [Fact]
    public async Task OnlyTheFirstPrompt_IsUsed_NeverTheAgentsWords()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name" };
        env.Turns = Turns(("Text", "agent preamble"), ("UserMessage", "first ask"), ("Text", "reply"), ("UserMessage", "second ask"));
        var sut = new SessionNamingService(env);

        await sut.NameIfUnnamedAsync(Tenant, Sid, Director);

        var prompt = env.Prompts.Single();
        Assert.Contains("first ask", prompt);
        Assert.DoesNotContain("second ask", prompt);
        Assert.DoesNotContain("agent preamble", prompt);
    }

    [Fact]
    public async Task ASessionStillMarkedAutoNamed_IsNamed()
    {
        var env = new FakeNamingEnvironment { Reply = "Fix The Login Page" };
        env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "devthrottle / 1fb5", IsAutoNamed = true };
        var sut = new SessionNamingService(env);

        Assert.Equal("Fix The Login Page", await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
    }

    [Fact]
    public async Task ANameThePersonGave_IsNeverTouched_AndNeverAskedAbout()
    {
        var env = new FakeNamingEnvironment { Reply = "Something Else" };
        env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "My Own Name", IsAutoNamed = false };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Empty(env.Prompts);
        Assert.Empty(env.Renames);
        Assert.Equal(0, env.TurnReads);
    }

    [Fact]
    public async Task AHeldSession_IsNotNamed()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name", Held = true };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Empty(env.Prompts);
    }

    [Fact]
    public async Task AnAccountWithTheJudgeSwitchOff_PaysForNoNamingCall()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name", Judge = false };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Empty(env.Prompts);
    }

    [Fact]
    public async Task APromptThatLandsAMomentAfterTheEdge_IsWaitedFor()
    {
        // The Working edge can come before the prompt reaches the transcript.
        var env = new FakeNamingEnvironment { Reply = "Late Name" };
        var full = env.Turns;
        env.Turns = Turns();
        env.OnDelay = n => { if (n == 2) env.Turns = full; };
        var sut = new SessionNamingService(env);

        Assert.Equal("Late Name", await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Equal(3, env.TurnReads);
    }

    [Fact]
    public async Task AFailedRead_IsNotTakenAsNoPrompt_AndIsReadAgain()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name" };
        var full = env.Turns;
        env.Turns = new TurnsResponse { Status = "no_jsonl", Widgets = full!.Widgets };
        env.OnDelay = _ => env.Turns = full;
        var sut = new SessionNamingService(env);

        Assert.Equal("A Name", await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Equal(2, env.TurnReads);
    }

    [Fact]
    public async Task AnEdgeWithNoPromptBehindIt_NamesNothing_AndLeavesTheSessionForTheNextEdge()
    {
        // The agent starting up goes Working with no user prompt in the conversation.
        var env = new FakeNamingEnvironment { Reply = "Next Edge Name" };
        var full = env.Turns;
        env.Turns = Turns(("Text", "agent starting"));
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Equal(SessionNamingService.PromptWaitAttempts, env.TurnReads);
        Assert.Empty(env.Prompts);

        env.Turns = full;
        Assert.Equal("Next Edge Name", await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
    }

    [Fact]
    public async Task ASessionIsAskedAboutAtMostOnce()
    {
        var env = new FakeNamingEnvironment { Reply = "   " }; // an unusable reply
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        env.Reply = "A Good Name";
        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));

        Assert.Single(env.Prompts);
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task TwoEdgesAtOnce_AskOnce()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name" };
        var gate = new TaskCompletionSource();
        env.AskGate = gate.Task;
        var sut = new SessionNamingService(env);

        var first = sut.NameIfUnnamedAsync(Tenant, Sid, Director);
        var second = await sut.NameIfUnnamedAsync(Tenant, Sid, Director);
        gate.SetResult();

        Assert.Null(second);
        Assert.Equal("A Name", await first);
        Assert.Single(env.Prompts);
    }

    [Fact]
    public async Task ARenameWhileTheModelAnswers_Wins()
    {
        var env = new FakeNamingEnvironment { Reply = "Model Name" };
        env.OnAsk = () => env.Facts = new SessionDto { SessionId = Sid, DirectorId = Director, Name = "Typed By Hand" };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task AModelFailure_NamesNothing_AndDoesNotThrow()
    {
        var env = new FakeNamingEnvironment { Throw = new TimeoutException("no answer") };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Empty(env.Renames);
    }

    [Fact]
    public async Task ARenameTheDirectorRefuses_IsReportedAsNoName()
    {
        var env = new FakeNamingEnvironment { Reply = "A Name", RenameError = "session not found" };
        var sut = new SessionNamingService(env);

        Assert.Null(await sut.NameIfUnnamedAsync(Tenant, Sid, Director));
        Assert.Single(env.Renames);
    }

    [Fact]
    public void FirstUserPrompt_SkipsMachineTextOnTheUsersSide()
    {
        var widgets = Turns(
            ("UserMessage", "<system-reminder>\nhook context\n</system-reminder>"),
            ("UserMessage", "Caveat: The messages below were generated by the user while running local commands."),
            ("UserMessage", "<command-name>/clear</command-name>\n<command-message>clear</command-message>"),
            ("UserMessage", "Base directory for this skill: C:\\skills\\x\n\n# A skill body"),
            ("UserMessage", "This session is being continued from a previous conversation that ran out of context."),
            ("UserMessage", "<task-notification>\n<task-id>b1</task-id>\n</task-notification>"),
            ("Text", "agent words"),
            ("UserMessage", "<system-reminder>x</system-reminder>\nplease fix the build")).Widgets;

        Assert.Equal("please fix the build", SessionNamingService.FirstUserPrompt(widgets));
    }

    [Fact]
    public void FirstUserPrompt_IsCapped()
    {
        var widgets = Turns(("UserMessage", new string('a', SessionNamingService.MaxPromptChars + 50))).Widgets;
        Assert.Equal(SessionNamingService.MaxPromptChars, SessionNamingService.FirstUserPrompt(widgets)!.Length);
    }

    [Theory]
    [InlineData("Fix The Login Page", "Fix The Login Page")]
    [InlineData("\"Fix The Login Page\"", "Fix The Login Page")]
    [InlineData("Name: Fix The Login Page.", "Fix The Login Page")]
    [InlineData("\n\n  **Fix   the  build**  \nbecause the user asked", "Fix the build")]
    [InlineData("Caf\u00e9 Menu Redesign", "Caf Menu Redesign")]
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

    private static TurnsResponse Turns(params (string Kind, string Content)[] widgets)
        => new()
        {
            SessionId = Sid,
            Status = "ok",
            Widgets = widgets.Select(w => new TurnWidgetDto { Kind = w.Kind, Content = w.Content }).ToList(),
        };

    private sealed class FakeNamingEnvironment : ISessionNamingEnvironment
    {
        public SessionDto? Facts = new() { SessionId = Sid, DirectorId = Director, Name = null };
        public TurnsResponse? Turns = SessionNamingServiceTests.Turns(("UserMessage", "add a github issue and implement naming"));
        public bool Held;
        public bool Judge = true;
        public string? Reply;
        public Exception? Throw;
        public string? RenameError;
        public Action? OnAsk;
        public Task? AskGate;
        public Action<int>? OnDelay;
        public int TurnReads;
        private int _delays;
        public readonly List<string> Prompts = new();
        public readonly List<(string Director, string Sid, string Name)> Renames = new();

        public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId) => new(Facts, Held);

        public bool JudgeEnabled(TenantId tenant) => Judge;

        public Task<TurnsResponse?> ReadTurnsAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            TurnReads++;
            return Task.FromResult(Turns);
        }

        public async Task<string> AskNamerAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
        {
            Prompts.Add(prompt);
            if (AskGate is not null) await AskGate;
            OnAsk?.Invoke();
            if (Throw is not null) throw Throw;
            return Reply ?? "";
        }

        public Task<string?> RenameSessionAsync(TenantId tenant, string directorId, string sessionId, string name, CancellationToken ct)
        {
            Renames.Add((directorId, sessionId, name));
            return Task.FromResult(RenameError);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            OnDelay?.Invoke(++_delays);
            return Task.CompletedTask;
        }
    }
}
