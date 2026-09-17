using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <see cref="TurnVerdictTraceRowStamp"/>: the colour is folded with THIS judgement's verdict on the row, whatever the
/// live store says by the time the writer gets to it.
///
/// WHY THIS EXISTS BESIDE THE WRITE-PATH TESTS. Those run the seat, which stores a verdict before it hands in the trace,
/// so when the writer folds, the live store already holds the same verdict - and a stamp that ignored the trace's verdict
/// and folded the live row passed all of them (revert proof, phase 2). The difference only shows when the live store has
/// moved on first: the writer is a queue, and a busy one lags. So the live source here has ALREADY moved on, and is
/// reading the session again, and the stamp must still record the stop it was handed. The colour comes from the real
/// roster fold; only the trace and the live source are built here.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictTraceRowStampTests
{
    private static readonly TenantId Account = TenantId.Local;
    private const string Sid = "22222222-2222-2222-2222-222222222222";

    private sealed class MovedOn : ITurnVerdictRowSource
    {
        public bool Reading;
        public TurnVerdictDto? Latest;
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
            => Latest is null
                ? new Dictionary<string, TurnVerdictDto>()
                : new Dictionary<string, TurnVerdictDto> { [Sid] = Latest };
        public bool IsReading(TenantId tenant, string sessionId) => Reading && sessionId == Sid;
    }

    private static TurnVerdictDto Verdict(string id, string word, string label) => new()
    {
        VerdictId = id,
        JudgedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow,
        Verdict = word,
        Confidence = "high",
        Label = label,
        Summary = "s",
        AnswerVia = "reply",
        Risk = "none",
    };

    private static TurnVerdictTraceRowStamp Stamp(MovedOn live)
    {
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, "d", "c");
        Assert.True(pushed.ApplySnapshot(Account, "d", "c", 1, new List<SessionDto>
        {
            new() { SessionId = Sid, Name = Sid, ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow },
        }));
        return new TurnVerdictTraceRowStamp(t => pushed.SnapshotConnected(t), live,
            (t, sessions, rows) => GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, tenant: t, turnVerdictRows: rows));
    }

    private static TurnVerdictTrace Trace(TurnVerdictDto? verdict) => new()
    {
        TraceId = "t1",
        SessionId = Sid,
        RecordedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow,
        Trigger = "clock",
        Outcome = verdict is null ? TurnVerdictTraceOutcomes.Skipped : TurnVerdictTraceOutcomes.Expired,
        VerdictId = verdict?.VerdictId,
        Verdict = verdict,
        ColourEnabled = true,
    };

    [Fact]
    public void The_stop_is_folded_with_its_own_verdict_after_the_live_store_has_moved_on_to_a_calm_one()
    {
        var live = new MovedOn { Latest = Verdict("later", TurnVerdictVocabulary.Finished, "Pushed the tag") };

        var stamped = Stamp(live).Stamp(Account, Trace(Verdict("expired", TurnVerdictVocabulary.NeededYou, TurnVerdictWatchdog.ExpiredLabel)));

        Assert.Equal("red", stamped.RowColour);
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, stamped.RowLabel);
    }

    [Fact]
    public void The_stop_is_folded_as_judged_even_while_the_session_is_being_read_again()
    {
        var live = new MovedOn { Reading = true };

        var stamped = Stamp(live).Stamp(Account, Trace(Verdict("v", TurnVerdictVocabulary.Finished, "Pushed the tag")));

        Assert.Equal("cyan", stamped.RowColour);
    }

    [Fact]
    public void A_stop_with_no_verdict_of_its_own_records_the_row_as_the_live_source_has_it()
    {
        // The control for the two above: the override is for the trace's own verdict and nothing else.
        var live = new MovedOn { Latest = Verdict("later", TurnVerdictVocabulary.Finished, "Pushed the tag") };

        var stamped = Stamp(live).Stamp(Account, Trace(null));

        Assert.Equal("cyan", stamped.RowColour);
    }
}
