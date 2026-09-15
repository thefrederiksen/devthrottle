using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The answer route's rules (the Wingman-on-every-turn mission, slice E): the one server-owned write path for a
/// verdict's options. Run over the REAL verdict store on a throwaway SQLite file, with a fake channel that RECORDS
/// EVERY WRITE - so every accepted case below compares the exact bytes written, and a sender that wrote a constant
/// would fail it.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
///
/// Every screen and option below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class TurnVerdictAnswerServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-answer-a");
    private static readonly TenantId OtherTenant = new("acct-answer-b");
    private const string Sid = "sid-answer-1";
    private const string OtherSid = "sid-answer-2";
    private const string Dir = "dir-1";
    private static readonly DateTime JudgedAt = new(2026, 9, 15, 11, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Rows =
    {
        "Which parts should the migration touch?",
        "  [ ] 1. the schema",
        "  [ ] 2. the data",
        "  [ ] 3. the seed rows",
    };
    private static readonly string[] ChangedRows =
    {
        "Which parts should the migration touch?",
        "  [x] 1. the schema",
        "  [ ] 2. the data",
        "  [ ] 3. the seed rows",
    };
    private static readonly string RowsHash = WingmanScreenVerdictCache.HashRows(Rows);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TurnVerdictStore _store;
    private readonly List<TurnVerdictRecord> _records = new();
    private int _judged;

    public TurnVerdictAnswerServiceTests()
    {
        _store = new TurnVerdictStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    private TurnVerdictAnswerService Service()
        => new(new TurnVerdictAnswerRecords(_store, r => { lock (_records) _records.Add(r); }));

    // ================================================================= builders

    private static TurnVerdictOptionDto Option(string key, string send) => new()
    {
        Key = key, Send = send, Recommended = false, Note = "A consequence written for the test.",
    };

    private static TurnVerdictDto Base(string verdictId, string answerVia, TurnVerdictMenuDto? menu, params TurnVerdictOptionDto[] options) => new()
    {
        VerdictId = verdictId,
        TurnEndObservedAtUtc = JudgedAt.AddSeconds(-12),
        ScreenHash = RowsHash,
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = Core.Wingman.TurnVerdictVocabulary.NeededYou,
        Confidence = "high",
        Evidence = "Which parts should the migration touch?",
        Label = "Choose what the migration touches",
        Summary = "The migration session is asking which parts to change.",
        AnswerVia = answerVia,
        Menu = menu,
        Options = options.ToList(),
        Risk = "none",
        Spoken = "The migration session wants to know which parts to change.",
    };

    private static TurnVerdictMenuDto Menu(string mode, string submit) => new()
    {
        Question = "Which parts should the migration touch?", SelectionMode = mode, Submit = submit,
    };

    /// <summary>Store a verdict as the session's latest, a second after anything stored before it.</summary>
    private TurnVerdictDto Stored(string sessionId, TurnVerdictDto verdict, TenantId? tenant = null)
    {
        verdict.JudgedAtUtc = JudgedAt.AddSeconds(++_judged);
        _store.Store(tenant ?? Tenant, sessionId, verdict);
        return verdict;
    }

    private static TurnVerdictAnswerRequest Request(string verdictId, params int[] indexes)
        => new() { VerdictId = verdictId, OptionIndexes = indexes.ToList() };

    private sealed class FakeChannel : ITurnVerdictAnswerChannel
    {
        public IReadOnlyList<string>? Rows;
        public IReadOnlyList<string>? RowsAfterWrite;
        public bool MenuOwns;
        public TurnVerdictAnswerWriteKind WriteKind = TurnVerdictAnswerWriteKind.Accepted;
        public TimeSpan ReadDelay = TimeSpan.Zero;
        public readonly List<(string Text, bool AppendEnter)> Writes = new();
        public int Reads;
        public int MenuChecks;

        public async Task<ScreenGridResponse?> ReadScreenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            // Taken BEFORE the delay: a racing reader that is not held back by a lock reads the same screen.
            var rows = Rows;
            if (ReadDelay > TimeSpan.Zero) await Task.Delay(ReadDelay, ct);
            return rows is null ? null : new ScreenGridResponse { Rows = rows.ToList(), HasGrid = true };
        }

        public Task<bool> MenuOwnsScreenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref MenuChecks);
            return Task.FromResult(MenuOwns);
        }

        public Task<TurnVerdictAnswerWrite> WriteAsync(string text, bool appendEnter, CancellationToken ct)
        {
            lock (Writes) Writes.Add((text, appendEnter));
            if (RowsAfterWrite is not null) Rows = RowsAfterWrite;
            return Task.FromResult(new TurnVerdictAnswerWrite(WriteKind, "the transport's own words"));
        }
    }

    private static FakeChannel Matching() => new() { Rows = Rows };

    private TurnVerdictRecord OnlyRecord()
    {
        lock (_records)
        {
            Assert.Single(_records);
            return _records[0];
        }
    }

    private void AssertRefusedAndNothingWritten(TurnVerdictAnswerOutcome outcome, FakeChannel channel, string cause)
    {
        Assert.False(outcome.Accepted);
        Assert.Equal(cause, outcome.Code);
        Assert.Empty(channel.Writes);
        var record = OnlyRecord();
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, record.EventType);
        Assert.Equal(cause, record.Cause);
    }

    // ================================================================= the bytes

    [Fact]
    public async Task Keys_SingleSelect_OnTheJudgedScreen_WritesTheChosenOptionThenTheSubmit_AsOneRawWrite()
    {
        var v = Stored(Sid, Base("tv-single", "keys", Menu("single", "\r"),
            Option("the schema", "1"), Option("the data", "2"), Option("the seed rows", "3")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 1), channel, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal(ActivityCauses.OwnerAnswered, outcome.Code);
        Assert.Equal(TurnVerdictAnswerService.SentReason, outcome.Reason);
        Assert.Equal(new[] { ("2\r", false) }, channel.Writes);
        var record = OnlyRecord();
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswered, record.EventType);
        Assert.Equal(ActivityCauses.OwnerAnswered, record.Cause);
        Assert.Equal(Sid, record.SessionId);
        // Control flow only: the ledger names the verdict and never the bytes.
        Assert.Contains("verdict=tv-single", record.Detail);
        Assert.DoesNotContain("2\r", record.Detail);
    }

    [Fact]
    public async Task Keys_OptionWithAnEmptySubmit_WritesExactlyItsSend_AndNoEnterBeyondTheSubmit()
    {
        var v = Stored(Sid, Base("tv-nosubmit", "keys", Menu("single", ""),
            Option("Yes", "y"), Option("No", "n")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 1), channel, CancellationToken.None);

        Assert.True(outcome.Accepted);
        // Not "n\r" and not an Enter appended by the send: the raw write is the option and the submit, and the
        // submit here is empty.
        Assert.Equal(new[] { ("n", false) }, channel.Writes);
        Assert.Equal(0, channel.MenuChecks);
    }

    [Fact]
    public async Task ReplyOption_WritesItsSend_WithTheOneEnterThePromptSendAppends_AfterTheMenuGuard()
    {
        var v = Stored(Sid, Base("tv-reply", "reply", null,
            Option("Merge it", "yes, merge it"), Option("Hold off", "no, wait for the review")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 1), channel, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal(new[] { ("no, wait for the review", true) }, channel.Writes);
        Assert.Equal(1, channel.MenuChecks);
    }

    [Fact]
    public async Task ReplyOption_WhenTheMenuGuardSaysAMenuOwnsTheScreen_IsRefused_AndNothingIsWritten()
    {
        var v = Stored(Sid, Base("tv-reply-menu", "reply", null, Option("Merge it", "yes"), Option("Hold off", "no")));
        var channel = Matching();
        channel.MenuOwns = true;

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.MenuOwnsScreen);
    }

    [Fact]
    public async Task MultipleSelect_SeveralIndexes_WritesEveryToggleInTheOrderGivenThenEnter_InOneWriteAfterOneScreenRead()
    {
        var v = Stored(Sid, Base("tv-multi", "keys", Menu("multiple", "\r"),
            Option("the schema", "1"), Option("the data", "2"), Option("the seed rows", "3")));
        // The first toggle repaints the picker. A route that compared the screen per toggle would refuse its own
        // second toggle; this one compares once and writes once.
        var channel = Matching();
        channel.RowsAfterWrite = ChangedRows;

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 2, 0), channel, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal(new[] { ("31\r", false) }, channel.Writes);
        Assert.Equal(1, channel.Reads);
    }

    [Fact]
    public async Task ParkedReply_WithAnEmptyList_WritesTheSubmitAlone()
    {
        var parked = Base("tv-parked", "keys", new TurnVerdictMenuDto
        {
            Question = "Send the reply already typed: run the tests first", SelectionMode = "single", Submit = "\r",
        });
        var v = Stored(Sid, parked);
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId), channel, CancellationToken.None);

        Assert.True(outcome.Accepted);
        Assert.Equal(new[] { ("\r", false) }, channel.Writes);
    }

    [Fact]
    public async Task AnEmptyList_InEveryShapeButTheParkedReply_IsRefused_AndNothingIsWritten()
    {
        var shapes = new (string Sid, TurnVerdictDto Verdict)[]
        {
            ("sid-empty-1", Base("tv-empty-single-options", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2"))),
            ("sid-empty-2", Base("tv-empty-single-nosubmit", "keys", Menu("single", ""))),
            ("sid-empty-3", Base("tv-empty-multiple", "keys", Menu("multiple", "\r"))),
            ("sid-empty-4", Base("tv-empty-multiple-options", "keys", Menu("multiple", "\r"), Option("a", "1"), Option("b", "2"))),
            ("sid-empty-5", Base("tv-empty-reply", "reply", null, Option("a", "yes"), Option("b", "no"))),
        };
        foreach (var (sid, verdict) in shapes)
        {
            Stored(sid, verdict);
            lock (_records) _records.Clear();
            var channel = Matching();

            var outcome = await Service().AnswerAsync(Tenant, Dir, sid, Request(verdict.VerdictId), channel, CancellationToken.None);

            AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerSelectionRefused);
            Assert.Equal(0, channel.Reads);
        }
    }

    [Theory]
    [InlineData("single", new[] { 3 })]      // out of range
    [InlineData("single", new[] { -1 })]     // out of range
    [InlineData("single", new[] { 0, 1 })]   // single takes exactly one
    [InlineData("multiple", new[] { 1, 1 })] // the same index twice
    [InlineData("multiple", new[] { 0, 7 })] // one good, one out of range: the whole selection is refused
    public async Task ASelectionTheVerdictDoesNotAllow_IsRefused_AndNothingIsWritten(string mode, int[] indexes)
    {
        var v = Stored(Sid, Base("tv-bad-" + mode + "-" + string.Join("_", indexes), "keys", Menu(mode, "\r"),
            Option("the schema", "1"), Option("the data", "2"), Option("the seed rows", "3")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, indexes), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerSelectionRefused);
    }

    [Fact]
    public async Task AReplyWithTwoIndexes_IsRefused_AndNothingIsWritten()
    {
        var v = Stored(Sid, Base("tv-reply-two", "reply", null, Option("a", "yes"), Option("b", "no")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0, 1), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerSelectionRefused);
    }

    // ================================================================= the screen lock

    [Fact]
    public async Task AScreenThatIsNotTheJudgedScreen_IsRefusedWithAReasonTheClientShows_AndNothingIsWritten()
    {
        var v = Stored(Sid, Base("tv-changed", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        var channel = new FakeChannel { Rows = ChangedRows };

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerScreenChanged);
        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(TurnVerdictAnswerService.ScreenChangedReason, outcome.Reason);
    }

    [Fact]
    public async Task AnUnreadableScreen_IsRefused_AndNothingIsWritten()
    {
        var v = Stored(Sid, Base("tv-unreadable", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        var channel = new FakeChannel { Rows = null };

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerScreenUnreadable);
    }

    [Fact]
    public async Task TwoAnswersRacingOnOneSession_OnlyTheFirstIsWritten_AndTheSecondIsRefusedOnTheScreenTheFirstChanged()
    {
        var v = Stored(Sid, Base("tv-race", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        var channel = Matching();
        channel.RowsAfterWrite = ChangedRows;
        channel.ReadDelay = TimeSpan.FromMilliseconds(150);
        var service = Service();

        var outcomes = await Task.WhenAll(
            Task.Run(() => service.AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), channel, CancellationToken.None)),
            Task.Run(() => service.AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 1), channel, CancellationToken.None)));

        Assert.Single(channel.Writes);
        Assert.Equal(1, outcomes.Count(o => o.Accepted));
        Assert.Equal(1, outcomes.Count(o => o.Code == ActivityCauses.AnswerScreenChanged));
        Assert.Equal(2, _records.Count);
    }

    // ================================================================= the joins

    [Fact]
    public async Task AVerdictFromAnotherSessionInTheSameAccount_IsRefused_AndNothingIsWritten()
    {
        // The other session's verdict was formed on the SAME screen, so only the join can refuse it.
        var foreign = Stored(OtherSid, Base("tv-other-session", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        Stored(Sid, Base("tv-this-session", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(foreign.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerVerdictNotFound);
        Assert.Equal(404, outcome.StatusCode);

        // POSITIVE CONTROL: the same verdict, answered on its own session, is written - so the refusal above came
        // from the join and not from a verdict that could never be answered.
        lock (_records) _records.Clear();
        var own = Matching();
        var control = await Service().AnswerAsync(Tenant, Dir, OtherSid, Request(foreign.VerdictId, 0), own, CancellationToken.None);
        Assert.True(control.Accepted);
        Assert.Equal(new[] { ("1\r", false) }, own.Writes);
    }

    [Fact]
    public async Task AVerdictFromAnotherAccount_IsNeverFound_EvenUnderTheSameSessionId()
    {
        var foreign = Stored(Sid, Base("tv-other-account", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")), OtherTenant);
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(foreign.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerVerdictNotFound);
        Assert.Null(_store.FindById(Tenant, foreign.VerdictId));
        Assert.Equal(Sid, _store.FindById(OtherTenant, foreign.VerdictId)!.SessionId);
    }

    [Fact]
    public async Task AVerdictANewerOneHasReplaced_IsRefused_AndNothingIsWritten()
    {
        var older = Stored(Sid, Base("tv-older", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        Stored(Sid, Base("tv-newer", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(older.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerVerdictSuperseded);
    }

    [Fact]
    public async Task AFailedVerdict_IsRefused_AndNothingIsWritten()
    {
        var failed = Base("tv-failed", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2"));
        failed.Failed = true;
        failed.FailureReason = "evidence not found";
        var v = Stored(Sid, failed);
        var channel = Matching();

        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), channel, CancellationToken.None);

        AssertRefusedAndNothingWritten(outcome, channel, ActivityCauses.AnswerVerdictFailed);
    }

    // ================================================================= malformed requests and the send

    [Fact]
    public async Task ARequestWithNoOptionList_OrNoVerdict_IsRefused_AndNothingIsRead()
    {
        var v = Stored(Sid, Base("tv-malformed", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));

        var noList = Matching();
        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, new TurnVerdictAnswerRequest { VerdictId = v.VerdictId }, noList, CancellationToken.None);
        AssertRefusedAndNothingWritten(outcome, noList, ActivityCauses.AnswerMalformed);
        Assert.Equal(0, noList.Reads);

        lock (_records) _records.Clear();
        var noVerdict = Matching();
        outcome = await Service().AnswerAsync(Tenant, Dir, Sid, new TurnVerdictAnswerRequest { OptionIndexes = new List<int> { 0 } }, noVerdict, CancellationToken.None);
        AssertRefusedAndNothingWritten(outcome, noVerdict, ActivityCauses.AnswerMalformed);
    }

    [Fact]
    public async Task ADirectorThatIsNotConnected_IsARefusal_AndAnUnconfirmedWrite_IsNot()
    {
        var v = Stored(Sid, Base("tv-send", "keys", Menu("single", "\r"), Option("a", "1"), Option("b", "2")));

        var offline = Matching();
        offline.WriteKind = TurnVerdictAnswerWriteKind.NeverLeftTheGateway;
        var outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), offline, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.Equal(ActivityCauses.AnswerNeverSent, outcome.Code);
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, OnlyRecord().EventType);

        lock (_records) _records.Clear();
        var unanswered = Matching();
        unanswered.WriteKind = TurnVerdictAnswerWriteKind.Unanswered;
        outcome = await Service().AnswerAsync(Tenant, Dir, Sid, Request(v.VerdictId, 0), unanswered, CancellationToken.None);
        Assert.False(outcome.Accepted);
        Assert.Equal(502, outcome.StatusCode);
        Assert.Equal(ActivityCauses.AnswerUnanswered, outcome.Code);
        // Not recorded as a refusal: a refusal promises nothing was sent, and here the bytes went out.
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerUnconfirmed, OnlyRecord().EventType);
    }

    [Fact]
    public void EveryAnswerWord_IsALegalLedgerWord()
    {
        foreach (var type in new[] { ActivityEventTypes.TurnVerdictAnswered, ActivityEventTypes.TurnVerdictAnswerRefused, ActivityEventTypes.TurnVerdictAnswerUnconfirmed })
            Assert.Contains(type, ActivityEventTypes.All);
        foreach (var cause in new[]
                 {
                     ActivityCauses.OwnerAnswered, ActivityCauses.AnswerMalformed, ActivityCauses.AnswerSessionNotFound,
                     ActivityCauses.AnswerShadowRecord, ActivityCauses.AnswerVerdictNotFound, ActivityCauses.AnswerVerdictFailed,
                     ActivityCauses.AnswerVerdictSuperseded, ActivityCauses.AnswerSelectionRefused, ActivityCauses.AnswerScreenUnreadable,
                     ActivityCauses.AnswerScreenChanged, ActivityCauses.AnswerNeverSent, ActivityCauses.AnswerUnanswered,
                     ActivityCauses.MenuOwnsScreen,
                 })
            Assert.Contains(cause, ActivityCauses.All);
    }
}
