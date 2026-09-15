using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.TurnLog;
using Xunit;

namespace CcDirector.Gateway.Tests.TurnLog;

/// <summary>
/// The recorder: what a record contains, what happens when a part cannot be collected, and - the property
/// the whole instrument rests on - that with capture switched off it touches nothing at all.
/// </summary>
public sealed class TurnLogRecorderTests
{
    private static readonly TenantId Tenant = new("acct-a");

    /// <summary>The observed moment the watcher would have stamped. A FIXED value rather than
    /// DateTime.UtcNow, so a test can prove the recorder COPIES it onto the record instead of stamping a
    /// clock of its own - with "now" on both sides the assertion passes whichever one the recorder used.</summary>
    private static readonly DateTime ObservedAt = new(2026, 9, 14, 8, 30, 15, DateTimeKind.Utc);

    private static TurnEndSignal Signal(bool isNewTurn = true, string? previous = "Working")
        => new("sid-1", "director-1", Tenant, ObservedAt, isNewTurn, previous);

    [Fact]
    public void OnTurnEnd_CaptureSwitchedOff_ReadsNothingAtAll()
    {
        // The instrument must not change what it observes. Off means no screen read, no scrollback read, no
        // conversation read and no file - not "a capture that is thrown away", which would still cost the
        // Director a tunnel round trip on every turn end on the fleet.
        var env = new FakeEnvironment { Enabled = false };
        using var recorder = new TurnLogRecorder(env);

        recorder.OnTurnEnd(Signal());

        Assert.Equal(0, env.ScreenReads);
        Assert.Equal(0, env.ScrollbackReads);
        Assert.Equal(0, env.ConversationReads);
        Assert.Empty(env.Written);
    }

    [Fact]
    public async Task CaptureAsync_AGoodTurn_WritesOneRecordWithTheScreenAndTheConversation()
    {
        var env = new FakeEnvironment();
        env.Grid = new ScreenGridResponse
        {
            SessionId = "sid-1",
            HasGrid = true,
            Rows = { "> waiting for you", "" },
            CursorRow = 1,
            CursorCol = 2,
            CursorVisible = true,
        };
        env.Scrollback = new BufferResponse { Text = "ran the build\nbuild succeeded\n" };
        env.Conversation = new StoredConversationSnapshot(true, "transcript-1", new[]
        {
            Message("User", "do the thing"),
            Message("Assistant", "done"),
        });
        var recorder = new TurnLogRecorder(env);

        var path = await recorder.CaptureAsync(Signal(), CancellationToken.None);

        Assert.NotNull(path);
        var record = Assert.Single(env.Written);
        Assert.True(record.Terminal.HasGrid);
        Assert.Equal(2, record.Terminal.RowCount);
        Assert.Equal("> waiting for you", record.Terminal.Rows[0]);
        Assert.True(record.Terminal.CursorVisible);
        Assert.Contains("build succeeded", record.Terminal.Scrollback);
        Assert.Equal(2, record.Conversation.Messages.Count);
        Assert.Equal("transcript-1", record.Conversation.Generation);
        Assert.True(record.Moment.IsNewTurn);
        Assert.Equal("Working", record.Moment.ActivityStateBefore);
        // THE JOIN KEY IS THE SIGNAL'S MOMENT, EXACTLY. Presence is not enough: a recorder that stamped its
        // own clock, or a constant, would still write a value - and would pair this record with no verdict.
        Assert.Equal(ObservedAt, record.Moment.TurnEndObservedAtUtc);
        Assert.Empty(record.Gaps);
        // Unlabelled, and it must stay that way until a person says otherwise.
        Assert.Null(record.Verdict);
    }

    [Fact]
    public async Task CaptureAsync_TheScreenCannotBeRead_StillWritesARecordAndNamesTheGap()
    {
        // Unreadable is not an empty screen, and it is not a turn that did not happen. Both confusions
        // would quietly bias the corpus away from exactly the sessions worth looking at - the ones whose
        // machine had dropped off.
        var env = new FakeEnvironment { Grid = null };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.False(record.Terminal.HasGrid);
        Assert.Empty(record.Terminal.Rows);
        Assert.Contains(record.Gaps, g => g.Part == "terminal");
    }

    [Fact]
    public async Task CaptureAsync_AReadThatThrows_IsRecordedAsAGapRatherThanLosingTheTurn()
    {
        var env = new FakeEnvironment { ScreenThrows = new InvalidOperationException("the tunnel closed") };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Contains(record.Gaps, g => g.Part == "terminal" && g.Reason.Contains("the tunnel closed"));
    }

    [Fact]
    public async Task CaptureAsync_TheSessionHasGone_StillWritesARecordAndNamesTheGap()
    {
        var env = new FakeEnvironment { Session = null };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Null(record.Session);
        Assert.Contains(record.Gaps, g => g.Part == "session");
    }

    [Fact]
    public async Task CaptureAsync_KeepsTheWholeSessionSnapshotRatherThanAChosenFew()
    {
        var env = new FakeEnvironment();
        env.Session!.Name = "Turn Log Harness - Architect";
        env.Session.MachineName = "SOREN-NORTH";
        env.Session.Agent = "Claude";
        env.Session.RepoPath = "D:/ReposFred/devthrottle";
        env.Session.StateLabel = "Waiting for you";
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Equal("Turn Log Harness - Architect", record.Session!.Name);
        Assert.Equal("SOREN-NORTH", record.Glance.Computer);
        Assert.Equal("Claude", record.Glance.Agent);
        Assert.Equal("D:/ReposFred/devthrottle", record.Glance.Repository);
        Assert.Equal("Waiting for you", record.Observed.StateLabel);
    }

    [Fact]
    public void BuildConversation_MoreThanTenTurns_KeepsTheLastTenWholeAndSaysItCut()
    {
        // Twelve full turns in; the cut must land ON a user message so no agent reply arrives in the corpus
        // without the prompt that caused it.
        var messages = new List<HistoryMessageDto>();
        for (var turn = 1; turn <= 12; turn++)
        {
            messages.Add(Message("User", $"ask {turn}"));
            messages.Add(Message("Assistant", $"answer {turn}"));
        }

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.True(built.Truncated);
        Assert.Equal(24, built.TotalMessageCount);
        Assert.Equal(20, built.Messages.Count);
        Assert.Equal("User", built.Messages[0].Role);
        Assert.Equal("ask 3", built.Messages[0].Parts[0].Text);
        Assert.Equal("answer 12", built.Messages[^1].Parts[0].Text);
    }

    [Fact]
    public void BuildConversation_FewerThanTenTurns_KeepsEverythingAndSaysItDidNotCut()
    {
        var messages = new List<HistoryMessageDto>
        {
            Message("User", "ask 1"),
            Message("Assistant", "answer 1"),
        };

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.False(built.Truncated);
        Assert.Equal(2, built.Messages.Count);
    }

    [Fact]
    public void BuildConversation_NothingStored_IsAnEmptyConversationNotACrash()
    {
        var built = TurnLogRecorder.BuildConversation(null);
        Assert.Empty(built.Messages);
        Assert.False(built.IsSupported);
    }

    [Fact]
    public void BuildConversation_ToolResultsAreNotHumanTurns()
    {
        // Codex records every tool result as a USER message carrying one ToolResult part. Counting bare
        // roles would let the tool calls inside ONE human turn consume the whole window, and the stored
        // conversation would begin mid-turn with the prompt that caused it cut off.
        var messages = new List<HistoryMessageDto>();
        messages.Add(Message("User", "the prompt that started everything"));
        for (var i = 0; i < 30; i++)
        {
            messages.Add(ToolCall($"call-{i}"));
            messages.Add(ToolResult($"call-{i}"));
        }
        messages.Add(Message("Assistant", "done"));

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        // One human turn, so nothing is cut and the prompt survives at the front.
        Assert.False(built.Truncated);
        Assert.Equal("the prompt that started everything", built.Messages[0].Parts[0].Text);
    }

    [Fact]
    public void BuildConversation_AUserMessageWithNoPartsIsNotATurn()
    {
        var messages = new List<HistoryMessageDto> { new() { Role = "User", Parts = new List<HistoryPartDto>() } };

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.False(built.Truncated);
        Assert.Single(built.Messages);
    }

    [Fact]
    public async Task CaptureAsync_AConversationPushedBeforeTheTurnEnded_IsNamedAsAGap()
    {
        // The bad case: the last push predates the session's last activity, so the stored conversation stops
        // short of the turn the screen beside it is showing.
        var env = new FakeEnvironment
        {
            Conversation = new StoredConversationSnapshot(
                true, "transcript-1", Array.Empty<HistoryMessageDto>(),
                LastPushedUtc: new DateTime(2026, 9, 4, 11, 0, 0, DateTimeKind.Utc)),
        };
        env.Session!.LastActivityAt = new DateTime(2026, 9, 4, 11, 30, 0, DateTimeKind.Utc);
        var recorder = new TurnLogRecorder(env, nowUtc: () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Contains(record.Gaps, g => g.Part == "conversation" && g.Reason.Contains("one turn short"));
    }

    [Fact]
    public async Task CaptureAsync_AConversationPushedAFTERTheTurnEnded_IsNotAGap()
    {
        // THE HEALTHY CASE, and the one the first version got backwards. A push that landed after the turn
        // ended is a conversation that CONTAINS the turn - exactly what we want. Flagging it fired on every
        // record in the first minutes of live capture, and a gap that marks good records as suspect teaches
        // everyone to ignore the field.
        var env = new FakeEnvironment
        {
            Conversation = new StoredConversationSnapshot(
                true, "transcript-1", Array.Empty<HistoryMessageDto>(),
                LastPushedUtc: new DateTime(2026, 9, 4, 11, 45, 0, DateTimeKind.Utc)),
        };
        env.Session!.LastActivityAt = new DateTime(2026, 9, 4, 11, 30, 0, DateTimeKind.Utc);
        var recorder = new TurnLogRecorder(env, nowUtc: () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.DoesNotContain(record.Gaps, g => g.Part == "conversation");
    }

    [Fact]
    public async Task CaptureAsync_AnObservedStateReadThatThrows_IsNamedAsAGapRatherThanASilentNull()
    {
        // A null with no gap reads later as "this session had no supervisor", when what actually happened is
        // that we failed to ask.
        var env = new FakeEnvironment { SupervisorEnabledThrows = new InvalidOperationException("settings unavailable") };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Null(record.Observed.SupervisorEnabled);
        Assert.Contains(record.Gaps, g => g.Part == "supervisor-enabled" && g.Reason.Contains("settings unavailable"));
    }

    [Fact]
    public async Task CaptureAsync_EntersTheOwningAccountsScopeForEveryRead()
    {
        // Without the scope every read is denied on the hosted Gateway, and because a denied read is written
        // down as a gap rather than thrown, capture would look switched on and record nothing but holes.
        var entered = new List<TenantId>();
        var env = new FakeEnvironment();
        using var recorder = new TurnLogRecorder(env, enterTenantScope: t => { entered.Add(t); return new NoScope(); });

        recorder.OnTurnEnd(Signal());
        for (var i = 0; i < 100 && env.Written.Count == 0; i++) await Task.Delay(20);

        Assert.Single(env.Written);
        Assert.Equal(Tenant, Assert.Single(entered));
    }

    private sealed class NoScope : IDisposable { public void Dispose() { } }

    private static HistoryMessageDto ToolCall(string id) => new()
    {
        Role = "Assistant",
        Parts = new List<HistoryPartDto> { new() { Kind = "ToolUse", Text = "{}", ToolName = "Bash", ToolId = id } },
    };

    private static HistoryMessageDto ToolResult(string id) => new()
    {
        Role = "User",
        Parts = new List<HistoryPartDto> { new() { Kind = "ToolResult", Text = "output", ToolId = id } },
    };

    [Fact]
    public void BuildConversation_AConversationOverTheCeiling_DropsTheOLDESTTurnsAndSaysHowMany()
    {
        // Three fat turns, each well over the ceiling on its own tool output. The turns nearest the screen
        // are the ones a judgement about THIS turn end would read, so they are the ones that must survive.
        var big = new string('x', 90_000);
        var messages = new List<HistoryMessageDto>();
        foreach (var n in new[] { 1, 2, 3 })
        {
            messages.Add(Message("User", $"prompt {n}"));
            messages.Add(ToolResultWith($"call-{n}", big));
            messages.Add(Message("Assistant", $"answer {n}"));
        }

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.True(built.Truncated);
        Assert.True(built.TurnsDroppedForSize > 0);
        Assert.Equal(TurnLogRecorder.MaxConversationBytes, built.SizeCeilingBytes);
        // The FRONT went, not the back: the newest prompt and its answer are still here.
        Assert.Equal("User", built.Messages[0].Role);
        Assert.Equal("prompt 3", built.Messages[0].Parts[0].Text);
        Assert.Equal("answer 3", built.Messages[^1].Parts[0].Text);
    }

    [Fact]
    public void BuildConversation_ATurnsKeptWholeEvenWhenOneTurnAloneExceedsTheCeiling()
    {
        // Half a turn would teach the corpus something that never happened - an agent reply with no prompt.
        // One oversized turn is kept whole and the record is simply large.
        var messages = new List<HistoryMessageDto>
        {
            Message("User", "the only prompt"),
            ToolResultWith("call-1", new string('y', 400_000)),
            Message("Assistant", "the only answer"),
        };

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.Equal(3, built.Messages.Count);
        Assert.Equal("the only prompt", built.Messages[0].Parts[0].Text);
        Assert.Equal(0, built.TurnsDroppedForSize);
    }

    [Fact]
    public void BuildConversation_AnOrdinaryConversation_IsNotTrimmedForSizeAtAll()
    {
        var messages = new List<HistoryMessageDto> { Message("User", "hello"), Message("Assistant", "hi") };

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.Equal(0, built.TurnsDroppedForSize);
        Assert.False(built.Truncated);
        Assert.Equal(2, built.Messages.Count);
    }

    [Fact]
    public async Task CaptureAsync_WhenTheCeilingTrimsTurns_TheRecordSaysSoAsAGap()
    {
        var big = new string('x', 90_000);
        var messages = new List<HistoryMessageDto>();
        foreach (var n in new[] { 1, 2, 3 })
        {
            messages.Add(Message("User", $"prompt {n}"));
            messages.Add(ToolResultWith($"call-{n}", big));
            messages.Add(Message("Assistant", $"answer {n}"));
        }
        var env = new FakeEnvironment
        {
            Conversation = new StoredConversationSnapshot(true, "transcript-1", messages),
        };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Contains(record.Gaps, g => g.Part == "conversation" && g.Reason.Contains("dropped to stay under"));
    }

    private static HistoryMessageDto ToolResultWith(string id, string text) => new()
    {
        Role = "User",
        Parts = new List<HistoryPartDto> { new() { Kind = "ToolResult", Text = text, ToolId = id } },
    };

    [Fact]
    public async Task CaptureAsync_TheRawPushHasNoMachineName_ItIsFilledFromTheDirectorRegistration()
    {
        // A Director pushes its sessions with an empty machine name; the Gateway fills it in when it SERVES
        // the session list. A capture reads the pushed snapshot, one layer earlier, so it sees the blank -
        // and every record on the fleet said its computer was "unknown" while the Gateway knew all along.
        var env = new FakeEnvironment();
        env.Session!.MachineName = "";
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Equal("SOREN-NORTH", record.Glance.Computer);
        Assert.Equal("SOREN-NORTH", record.Session!.MachineName);
    }

    [Fact]
    public async Task CaptureAsync_TheSessionAlreadyCarriesAMachineName_TheRegistrationIsNotConsulted()
    {
        // The Director's own answer wins, exactly as it does in the session listing. Asking the
        // registration anyway would let a re-registered Director rename a machine under a session that had
        // already told us what it was.
        var env = new FakeEnvironment();
        env.Session!.MachineName = "SOREN-SOUTH";
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Equal("SOREN-SOUTH", record.Glance.Computer);
        Assert.Equal(0, env.MachineNameLookups);
    }

    [Fact]
    public async Task CaptureAsync_NeitherTheSessionNorTheRegistrationKnowsTheMachine_TheRecordSaysNothingRatherThanGuessing()
    {
        var env = new FakeEnvironment { MachineNameFromRegistration = null };
        env.Session!.MachineName = "";
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Null(record.Glance.Computer);
    }

    [Fact]
    public void EnforceRecordCeiling_AnEnormousScrollback_IsTrimmedAndTheScreenIsUntouched()
    {
        // Terminal content is UNTRUSTED input - it is whatever a program somebody ran decided to print - so
        // a record's size must not be settable by that program. The scrollback is the shock absorber; the
        // live screen is what every judgement reads and is never trimmed.
        var record = new TurnLogRecord
        {
            CapturedAtUtc = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Glance = new TurnLogGlance { SessionId = "sid-1", Account = "acct-a", Computer = "PC" },
            Terminal = new TurnLogTerminal
            {
                HasGrid = true,
                Rows = { "the screen line a judgement reads" },
                RowCount = 1,
                Scrollback = Enumerable.Range(0, 4000).Select(i => new string('x', 600)).ToList(),
                ScrollbackLineCount = 4000,
            },
        };

        var trimmed = TurnLogRecorder.EnforceRecordCeiling(record);

        Assert.True(trimmed.Terminal.Scrollback.Count < 4000);
        Assert.Equal(trimmed.Terminal.Scrollback.Count, trimmed.Terminal.ScrollbackLineCount);
        Assert.Equal("the screen line a judgement reads", Assert.Single(trimmed.Terminal.Rows));
        Assert.Contains(trimmed.Gaps, g => g.Part == "scrollback" && g.Reason.Contains("dropped to keep the record under"));
    }

    [Fact]
    public void EnforceRecordCeiling_AnOrdinaryRecord_IsReturnedUntouchedAndUnmarked()
    {
        var record = new TurnLogRecord
        {
            CapturedAtUtc = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            Glance = new TurnLogGlance { SessionId = "sid-1", Account = "acct-a", Computer = "PC" },
            Terminal = new TurnLogTerminal { HasGrid = true, Rows = { "small" }, RowCount = 1 },
        };

        var result = TurnLogRecorder.EnforceRecordCeiling(record);

        Assert.Empty(result.Gaps);
        Assert.Same(record, result);
    }

    [Fact]
    public void OnTurnEnd_MoreTurnsThanCapturesAllowedAtOnce_DropsRatherThanQueueing()
    {
        // Every turn end used to start an unbounded task, each holding a screen, thousands of scrollback
        // lines and a conversation in memory. Dropping loses a turn and says so; queueing would hold them
        // all and trade a memory problem for a latency one.
        using var gate = new ManualResetEventSlim(false);
        var env = new FakeEnvironment { BlockScreenReadOn = gate };
        using var recorder = new TurnLogRecorder(env);

        // A FIXED number, deliberately NOT derived from MaxConcurrentCaptures. Scaling the input to the
        // constant under test makes the test vacuous: raise the bound and the input rises with it, so the
        // bound still bites and the test still passes while proving nothing. Caught by mutation - the first
        // version of this test went green with the bound raised to a hundred thousand.
        const int turnsFiredAtOnce = 64;
        for (var i = 0; i < turnsFiredAtOnce; i++)
            recorder.OnTurnEnd(Signal());

        // Let the blocked captures finish before the test tears the fake down.
        var dropped = recorder.DroppedForConcurrency;
        gate.Set();

        Assert.True(dropped > 0, $"expected some captures to be dropped, but {dropped} were");
    }

    private static HistoryMessageDto Message(string role, string text) => new()
    {
        Role = role,
        Parts = new List<HistoryPartDto> { new() { Kind = "Text", Text = text } },
    };

    /// <summary>A Gateway that is not there: every read is a field a test sets, and every write is kept.</summary>
    private sealed class FakeEnvironment : ITurnLogEnvironment
    {
        public bool Enabled { get; set; } = true;
        public SessionDto? Session { get; set; } = new() { SessionId = "sid-1", ActivityState = "WaitingForInput" };
        public ScreenGridResponse? Grid { get; set; } = new() { SessionId = "sid-1", HasGrid = true };
        public BufferResponse? Scrollback { get; set; } = new() { Text = "" };
        public StoredConversationSnapshot? Conversation { get; set; }
            = new(true, "transcript-1", Array.Empty<HistoryMessageDto>());
        public Exception? ScreenThrows { get; set; }
        public Exception? SupervisorEnabledThrows { get; set; }
        public ManualResetEventSlim? BlockScreenReadOn { get; set; }
        public string? MachineNameFromRegistration { get; set; } = "SOREN-NORTH";
        public int MachineNameLookups { get; private set; }

        public int ScreenReads { get; private set; }
        public int ScrollbackReads { get; private set; }
        public int ConversationReads { get; private set; }
        public List<TurnLogRecord> Written { get; } = new();

        public bool IsEnabled(string account, string machine) => Enabled;

        public SessionDto? LocateSession(TenantId tenant, string sessionId) => Session;

        public Task<ScreenGridResponse?> ReadScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            BlockScreenReadOn?.Wait(TimeSpan.FromSeconds(10));
            ScreenReads++;
            if (ScreenThrows is not null) throw ScreenThrows;
            return Task.FromResult(Grid);
        }

        public Task<BufferResponse?> ReadScrollbackAsync(TenantId tenant, string directorId, string sessionId, int lines, CancellationToken ct)
        {
            ScrollbackReads++;
            return Task.FromResult(Scrollback);
        }

        public StoredConversationSnapshot? ReadConversation(TenantId tenant, string sessionId)
        {
            ConversationReads++;
            return Conversation;
        }

        public bool? SupervisorEnabled(TenantId tenant)
        {
            if (SupervisorEnabledThrows is not null) throw SupervisorEnabledThrows;
            return true;
        }

        public bool? IsVoiceSession(TenantId tenant, string sessionId) => false;

        public string? ResolveMachineName(TenantId tenant, string directorId)
        {
            MachineNameLookups++;
            return MachineNameFromRegistration;
        }

        public string? Write(TurnLogRecord record)
        {
            Written.Add(record);
            return "in-memory";
        }
    }
}
