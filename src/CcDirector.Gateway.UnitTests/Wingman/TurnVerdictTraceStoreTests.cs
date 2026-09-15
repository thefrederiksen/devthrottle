using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The Wingman inspector's record (devthrottle_internal#2029): every judgement kept whole, appended only, read
/// back newest first, partitioned by account, cut at the raw-reply ceiling, purged at seven days - and above all
/// NOT cleared when the verdict store invalidates a session, which is the one property the record exists for.
/// Runs over the real EF store on a throwaway SQLite file.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
///
/// Every screen, reply and prompt below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class TurnVerdictTraceStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");
    private static readonly DateTime RecordedAt = new(2026, 9, 15, 15, 38, 16, DateTimeKind.Utc);

    private static TurnVerdictTrace Trace(
        string traceId = "trace-1",
        string sessionId = "sid-1",
        DateTime? recordedAt = null,
        string? rawReply = "{\"verdict\":\"finished\"}") => new()
    {
        TraceId = traceId,
        SessionId = sessionId,
        DirectorId = "dir-1",
        RecordedAtUtc = recordedAt ?? RecordedAt,
        // Deliberately a different moment from the recorded one: they are two facts.
        TurnEndObservedAtUtc = (recordedAt ?? RecordedAt).AddSeconds(-4),
        Trigger = "turn-end",
        Outcome = TurnVerdictTraceOutcomes.Refused,
        VerdictId = "verdict-" + traceId,
        ReplySeconds = 3.8,
        ColourEnabled = true,
        Package = new TurnVerdictPackage
        {
            ScreenRows = new[] { "The nightly build failed on the migration step.", "> " },
            CursorRow = 1,
            ScreenHash = "hash-1",
            Kind = TurnVerdictPackageKind.TerminalFailure,
            FailureText = "The nightly build failed on the migration step.",
            RecentTurns = "USER: run the nightly build",
            SessionTitle = "devthrottle - nightly build",
            NextScheduledWakeUtc = RecordedAt.AddMinutes(20),
            OwnedSessions = new OwnedSessionCounts(2, 1, 0),
            ConversationAvailable = true,
        },
        Prompt = "You judge ONE stop of a coding agent's session.",
        RawReply = rawReply,
        Verdict = new TurnVerdictDto
        {
            VerdictId = "verdict-" + traceId,
            JudgedAtUtc = recordedAt ?? RecordedAt,
            TurnEndObservedAtUtc = (recordedAt ?? RecordedAt).AddSeconds(-4),
            Failed = true,
            FailureReason = "the verdict 'finished' is calm, and this stop has no reply at all",
            ContractVersion = TurnVerdictContract.Version,
        },
    };

    [Fact]
    public void Append_ThenHistory_ReadsBackEveryField_IncludingThePackageThePromptAndTheRawReply()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        var written = Trace();

        store.Append(TenantA, written);
        var read = Assert.Single(store.History(TenantA, "sid-1"));

        Assert.Equal("trace-1", read.TraceId);
        Assert.Equal("dir-1", read.DirectorId);
        Assert.Equal(RecordedAt, read.RecordedAtUtc);
        Assert.Equal(RecordedAt.AddSeconds(-4), read.TurnEndObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, read.RecordedAtUtc.Kind);
        Assert.Equal("turn-end", read.Trigger);
        Assert.Equal(TurnVerdictTraceOutcomes.Refused, read.Outcome);
        Assert.Equal("verdict-trace-1", read.VerdictId);
        Assert.Equal(3.8, read.ReplySeconds);
        Assert.True(read.ColourEnabled);
        Assert.Equal(written.Prompt, read.Prompt);
        Assert.Equal(written.RawReply, read.RawReply);
        Assert.False(read.RawReplyTruncated);

        var package = read.Package!;
        Assert.Equal(written.Package!.ScreenRows, package.ScreenRows);
        Assert.Equal(TurnVerdictPackageKind.TerminalFailure, package.Kind);
        Assert.Equal("The nightly build failed on the migration step.", package.FailureText);
        Assert.Equal("devthrottle - nightly build", package.SessionTitle);
        Assert.Equal(RecordedAt.AddMinutes(20), package.NextScheduledWakeUtc);
        Assert.Equal(new OwnedSessionCounts(2, 1, 0), package.OwnedSessions);

        Assert.True(read.Verdict.Failed);
        Assert.Equal(written.Verdict.FailureReason, read.Verdict.FailureReason);
    }

    [Fact]
    public void AVerdictStoreInvalidation_LeavesEveryTraceOfThatSessionInPlace()
    {
        // THE PROPERTY THE RECORD EXISTS FOR. The verdict store deletes a session's verdicts every time it starts
        // working again; a record that went with them would never hold more than one stop.
        var db = _harness.Open();
        var verdicts = new TurnVerdictStore(db);
        var traces = new TurnVerdictTraceStore(db);
        var trace = Trace();
        verdicts.Store(TenantA, "sid-1", trace.Verdict);
        traces.Append(TenantA, trace);
        traces.Append(TenantA, Trace("trace-2", recordedAt: RecordedAt.AddMinutes(1)));

        Assert.Equal(1, verdicts.Invalidate(TenantA, "sid-1"));

        Assert.Null(verdicts.Latest(TenantA, "sid-1"));
        Assert.Equal(2, traces.History(TenantA, "sid-1").Count);
    }

    [Fact]
    public void History_IsNewestFirst_OneSessionOnly_AndNeverMoreThanTheCeiling()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        store.Append(TenantA, Trace("old", recordedAt: RecordedAt));
        store.Append(TenantA, Trace("new", recordedAt: RecordedAt.AddMinutes(5)));
        store.Append(TenantA, Trace("other-session", sessionId: "sid-2", recordedAt: RecordedAt.AddMinutes(9)));

        Assert.Equal(new[] { "new", "old" }, store.History(TenantA, "sid-1").Select(t => t.TraceId));
        Assert.Equal(new[] { "new" }, store.History(TenantA, "sid-1", count: 1).Select(t => t.TraceId));

        for (var i = 0; i < TurnVerdictTraceStore.MaxHistoryCount + 5; i++)
            store.Append(TenantA, Trace($"bulk-{i}", sessionId: "sid-3", recordedAt: RecordedAt.AddSeconds(i)));
        Assert.Equal(TurnVerdictTraceStore.MaxHistoryCount, store.History(TenantA, "sid-3", count: 10_000).Count);
    }

    [Fact]
    public void OneAccount_NeverReadsAnothersTraces_EvenForTheSameSessionId()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        store.Append(TenantA, Trace("a-trace"));
        store.Append(TenantB, Trace("b-trace"));

        Assert.Equal(new[] { "a-trace" }, store.History(TenantA, "sid-1").Select(t => t.TraceId));
        Assert.Equal(new[] { "b-trace" }, store.History(TenantB, "sid-1").Select(t => t.TraceId));
    }

    [Fact]
    public void ARawReplyOverTheCeiling_IsCut_AndTheCutIsRecorded()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        var huge = new string('x', TurnVerdictTraceStore.MaxRawReplyChars + 10);

        store.Append(TenantA, Trace(rawReply: huge));
        var read = Assert.Single(store.History(TenantA, "sid-1"));

        Assert.Equal(TurnVerdictTraceStore.MaxRawReplyChars, read.RawReply!.Length);
        Assert.True(read.RawReplyTruncated);
    }

    [Fact]
    public void ATraceWithNoAnswer_KeepsNoRawReply_AndIsNotMarkedCut()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        store.Append(TenantA, Trace(rawReply: null) with { ReplySeconds = null, Package = null, Prompt = null });

        var read = Assert.Single(store.History(TenantA, "sid-1"));
        Assert.Null(read.RawReply);
        Assert.Null(read.ReplySeconds);
        Assert.Null(read.Package);
        Assert.Null(read.Prompt);
        Assert.False(read.RawReplyTruncated);
    }

    [Fact]
    public void ThePurge_RemovesOnlyWhatIsOlderThanTheCutoff_AndOnlyInThatAccount()
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        store.Append(TenantA, Trace("a-old", recordedAt: RecordedAt.AddDays(-8)));
        store.Append(TenantA, Trace("a-new", recordedAt: RecordedAt));
        store.Append(TenantB, Trace("b-old", recordedAt: RecordedAt.AddDays(-8)));

        Assert.Equal(1, store.PurgeOlderThan(TenantA, RecordedAt - TurnVerdictTraceStore.RetentionPeriod));

        Assert.Equal(new[] { "a-new" }, store.History(TenantA, "sid-1").Select(t => t.TraceId));
        Assert.Equal(new[] { "b-old" }, store.History(TenantB, "sid-1").Select(t => t.TraceId));
    }

    [Fact]
    public void TheRetentionPeriod_IsTheVerdictsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), TurnVerdictTraceStore.RetentionPeriod);
        Assert.Equal(TurnVerdictStore.RetentionPeriod, TurnVerdictTraceStore.RetentionPeriod);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("session")]
    [InlineData("verdict")]
    [InlineData("recorded")]
    [InlineData("observed")]
    public void ATraceMissingAnIdentifyingField_IsRefused_RatherThanStoredMatchingNothing(string missing)
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        var trace = missing switch
        {
            "id" => Trace() with { TraceId = "" },
            "session" => Trace() with { SessionId = "" },
            "verdict" => Trace() with { VerdictId = "" },
            "recorded" => Trace() with { RecordedAtUtc = default },
            _ => Trace() with { TurnEndObservedAtUtc = default },
        };

        Assert.Throws<ArgumentException>(() => store.Append(TenantA, trace));
        Assert.Empty(store.History(TenantA, "sid-1"));
    }
}
