using CcDirector.Core.Agents;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The Loud When Stranded mission, step 6: after a VERIFIED license switch, one plain "continue" goes to each session
/// whose last turn ended on a usage-limit reply - once, and to no other session. Scripted sessions over real
/// transcript files in a temporary folder; nothing is typed anywhere.
/// </summary>
public sealed class LimitStoppedSessionResumerTests : IDisposable
{
    private const string SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
    private const string SecondSwitchId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new() { Now = new DateTime(2026, 10, 26, 15, 0, 0, DateTimeKind.Utc) };

    public void Dispose() => _folder.Dispose();

    private string StoreFolder => Path.Combine(_folder.Path, "limit-resumes");

    private LimitStoppedSessionResumer NewResumer() => new(new LimitResumeStore(StoreFolder, _clock.UtcNow), _clock.UtcNow);

    private static LicenseSwitchResult Switch(string switchId = SwitchId, string outcome = LicenseSwitchOutcomes.Switched,
        bool verified = true, bool replayed = false) => new()
    {
        SwitchId = switchId,
        Outcome = outcome,
        Message = "",
        PreviousActiveAccountEmail = "account-a@example.com",
        ActiveAccountEmailAfter = "account-b@example.com",
        VerifiedByUsageRead = verified,
        Replayed = replayed,
    };

    private ScriptedSession Limited(string name, string? uuid = null) =>
        new(LimitTranscripts.Write(_folder.Path, name, [LimitTranscripts.UserPrompt("go on", LimitTranscripts.LimitAtUtc.AddSeconds(-1)),
            LimitTranscripts.LimitReply(uuid: uuid ?? Guid.NewGuid().ToString())]));

    private ScriptedSession Normal(string name) => new(LimitTranscripts.Write(_folder.Path, name, LimitTranscripts.EndedNormally()));

    [Fact]
    public async Task ResumeAfterSwitch_VerifiedSwitch_SendsOneContinueToEachLimitedSessionOnly()
    {
        var first = Limited("first");
        var second = Limited("second");
        var normal = Normal("normal");

        var outcomes = await NewResumer().ResumeAfterSwitchAsync(Switch(), [first, normal, second]);

        Assert.Equal(1, first.Sends);
        Assert.Equal(1, second.Sends);
        Assert.Equal(0, normal.Sends);
        Assert.Equal(2, outcomes.Count(o => o.Resumed));
        Assert.Contains("did not end on a usage-limit reply", outcomes.Single(o => o.SessionId == normal.Id).Detail);
    }

    [Fact]
    public async Task ResumeAfterSwitch_NotVerifiedByUsageRead_SendsNothing()
    {
        var limited = Limited("limited");

        var outcomes = await NewResumer().ResumeAfterSwitchAsync(Switch(verified: false), [limited]);

        Assert.Empty(outcomes);
        Assert.Equal(0, limited.Sends);
    }

    [Theory]
    [InlineData(LicenseSwitchOutcomes.Failed)]
    [InlineData(LicenseSwitchOutcomes.AlreadyActive)]
    public async Task ResumeAfterSwitch_SwitchThatDidNotSwitch_SendsNothing(string outcome)
    {
        var limited = Limited("limited");

        await NewResumer().ResumeAfterSwitchAsync(Switch(outcome: outcome, verified: false), [limited]);

        Assert.Equal(0, limited.Sends);
    }

    [Fact]
    public async Task ResumeAfterSwitch_ReplayedAnswer_SendsNothing()
    {
        var limited = Limited("limited");

        await NewResumer().ResumeAfterSwitchAsync(Switch(replayed: true), [limited]);

        Assert.Equal(0, limited.Sends);
    }

    [Fact]
    public async Task ResumeAfterSwitch_SecondVerifiedSwitch_SendsNothingMore()
    {
        var limited = Limited("limited");
        var resumer = NewResumer();

        await resumer.ResumeAfterSwitchAsync(Switch(), [limited]);
        var again = await resumer.ResumeAfterSwitchAsync(Switch(SecondSwitchId), [limited]);

        Assert.Equal(1, limited.Sends);
        Assert.Contains("already sent", again.Single().Detail);
    }

    [Fact]
    public async Task ResumeAfterSwitch_AfterADirectorRestart_SendsNothingMore()
    {
        var limited = Limited("limited");
        await NewResumer().ResumeAfterSwitchAsync(Switch(), [limited]);

        // A new process: a new store and resumer over the same folder.
        await NewResumer().ResumeAfterSwitchAsync(Switch(SecondSwitchId), [limited]);

        Assert.Equal(1, limited.Sends);
    }

    [Fact]
    public async Task ResumeAfterSwitch_InputAfterTheLimitReply_SendsNothing()
    {
        var limited = Limited("limited");
        limited.LastInputAtUtc = LimitTranscripts.LimitAtUtc.AddMinutes(5);

        var outcomes = await NewResumer().ResumeAfterSwitchAsync(Switch(), [limited]);

        Assert.Equal(0, limited.Sends);
        Assert.Contains("given input", outcomes.Single().Detail);
    }

    [Fact]
    public async Task ResumeAfterSwitch_InputOnlyBeforeTheLimitReply_SendsContinue()
    {
        var limited = Limited("limited");
        limited.LastInputAtUtc = LimitTranscripts.LimitAtUtc.AddSeconds(-1);

        await NewResumer().ResumeAfterSwitchAsync(Switch(), [limited]);

        Assert.Equal(1, limited.Sends);
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Gemini)]
    public async Task ResumeAfterSwitch_NotClaudeCode_SendsNothing(AgentKind agent)
    {
        var limited = Limited("limited");
        limited.Agent = agent;

        await NewResumer().ResumeAfterSwitchAsync(Switch(), [limited]);

        Assert.Equal(0, limited.Sends);
    }

    [Fact]
    public async Task ResumeAfterSwitch_UnreadableTranscript_SkipsItAndResumesTheOthers()
    {
        var unreadable = new ScriptedSession(LimitTranscripts.Write(_folder.Path, "broken", [LimitTranscripts.LimitReply(), "{not json", LimitTranscripts.TurnDuration(LimitTranscripts.LimitAtUtc)]));
        var limited = Limited("limited");

        var outcomes = await NewResumer().ResumeAfterSwitchAsync(Switch(), [unreadable, limited]);

        Assert.Equal(0, unreadable.Sends);
        Assert.Equal(1, limited.Sends);
        Assert.Contains("unreadable", outcomes.First().Detail);
    }

    [Fact]
    public async Task ResumeAfterSwitch_TranscriptNotWrittenYet_IsAnOrdinarySkip()
    {
        var fresh = new ScriptedSession(Path.Combine(_folder.Path, "absent.jsonl"));

        var outcomes = await NewResumer().ResumeAfterSwitchAsync(Switch(), [fresh]);

        Assert.Equal(0, fresh.Sends);
        Assert.Contains("no conversation yet", outcomes.Single().Detail);
    }

    [Fact]
    public async Task ResumeAfterSwitch_NoTranscriptNamed_SendsNothing()
    {
        var unnamed = new ScriptedSession(null);

        await NewResumer().ResumeAfterSwitchAsync(Switch(), [unnamed]);

        Assert.Equal(0, unnamed.Sends);
    }

    [Fact]
    public async Task ResumeAfterSwitch_SendRefused_IsNotRecordedSoALaterSwitchTriesAgain()
    {
        var limited = Limited("limited");
        limited.Refusal = "the session is Working, not waiting for a prompt; nothing was typed";
        var resumer = NewResumer();

        var first = await resumer.ResumeAfterSwitchAsync(Switch(), [limited]);
        limited.Refusal = null;
        await resumer.ResumeAfterSwitchAsync(Switch(SecondSwitchId), [limited]);

        Assert.False(first.Single().Resumed);
        Assert.Equal(1, limited.Sends);
    }

    [Fact]
    public void LimitResumeStore_Prune_RemovesOnlyRecordsOlderThanTheRetention()
    {
        var store = new LimitResumeStore(StoreFolder, _clock.UtcNow);
        var old = Guid.NewGuid().ToString();
        var fresh = Guid.NewGuid().ToString();
        store.Record(new LimitResume(old, Guid.NewGuid(), SwitchId, _clock.Now - LimitResumeStore.Retention - TimeSpan.FromHours(1)));
        store.Record(new LimitResume(fresh, Guid.NewGuid(), SwitchId, _clock.Now));
        File.WriteAllText(Path.Combine(StoreFolder, "not-a-record.json"), "{}");

        Assert.Equal(1, store.Prune());
        Assert.False(store.WasResumed(old));
        Assert.True(store.WasResumed(fresh));
        Assert.True(File.Exists(Path.Combine(StoreFolder, "not-a-record.json")));
    }

    private sealed class ScriptedSession(string? transcriptPath) : ILimitResumeTarget
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AgentKind Agent { get; set; } = AgentKind.ClaudeCode;
        public string? TranscriptPath { get; } = transcriptPath;
        public DateTime? LastInputAtUtc { get; set; }
        public string? Refusal { get; set; }
        public int Sends { get; private set; }

        public Task<LimitResumeSend> SendContinueAsync()
        {
            if (Refusal is not null) return Task.FromResult(LimitResumeSend.Refused(Refusal));
            Sends++;
            return Task.FromResult(LimitResumeSend.Ok);
        }
    }
}
