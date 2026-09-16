using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE COLOUR A STOP PRODUCED, recorded on its trace when the trace is written (the Wingman inspector, phase 2,
/// ruling 1).
///
/// WHY IT IS RECORDED AND NOT RECOMPUTED. The colour and label a row shows come from <see cref="SessionOrdering"/> over
/// the WHOLE row: working, snooze, dictation, supervision and voice all outrank a verdict. The trace keeps none of
/// those, so a colour worked out later from the stored verdict would be a guess about a row nobody kept - and a row
/// recorded red that later went cyan would read cyan. So the colour is folded once, here, and stored.
///
/// ONE COLOUR RULE, NOT TWO. This runs the fold the display push runs - the caller passes it in, and production
/// passes the push's own - over the account's roster as it stands, with ONE difference: this session's verdict is
/// the trace's verdict. Nothing here decides a colour.
///
/// WHAT IT LEAVES OUT, stated. The fold's two clocks and the snooze-expiry memory are not passed, because each of them
/// CHANGES state when it is folded (a needs-you clock starts, an expiry edge is spent) and a record must not move the
/// product. So a row whose colour depended on a snooze expiry being re-judged at that moment records the row without
/// that yellow.
///
/// RUNS ON THE WRITER'S THREAD, never the verdict path: it snapshots the roster and reads the stored verdicts, which is
/// the work <see cref="TurnVerdictTraceWriter"/> exists to keep off a judgement.
/// </summary>
public sealed class TurnVerdictTraceRowStamp
{
    private readonly Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> _roster;
    private readonly ITurnVerdictRowSource _live;
    private readonly Action<TenantId, List<SessionDto>, ITurnVerdictRowSource> _fold;

    /// <param name="roster">The account's roster as deep copies. Production passes the connected snapshot the display
    /// push folds.</param>
    /// <param name="live">The verdict source the display push reads.</param>
    /// <param name="fold">The display push's fold: stamps the colour and label onto every row, reading verdicts from the
    /// source it is handed.</param>
    public TurnVerdictTraceRowStamp(
        Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> roster,
        ITurnVerdictRowSource live,
        Action<TenantId, List<SessionDto>, ITurnVerdictRowSource> fold)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _fold = fold ?? throw new ArgumentNullException(nameof(fold));
    }

    /// <summary>
    /// The trace with the row's colour and label as they stood with this judgement on it. A session that is not on the
    /// roster at this moment has no row to fold, and the trace says so by carrying no colour - it is never given one.
    /// </summary>
    public TurnVerdictTrace Stamp(TenantId tenant, TurnVerdictTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var sessions = _roster(tenant).Select(r => r.Session).Where(s => s is not null).ToList();
        if (!sessions.Any(s => string.Equals(s.SessionId, trace.SessionId, StringComparison.Ordinal)))
        {
            FileLog.Write($"[TurnVerdictTraceRowStamp] Stamp: sid={trace.SessionId} outcome={trace.Outcome} is not on the roster; no colour recorded");
            return trace;
        }

        _fold(tenant, sessions, new AtThisStop(_live, trace));
        var row = sessions.First(s => string.Equals(s.SessionId, trace.SessionId, StringComparison.Ordinal));
        return trace with { RowColour = row.EffectiveColor, RowLabel = row.StateLabel };
    }

    /// <summary>
    /// The live verdict source, except for the one session the trace is about: its verdict is the trace's own, and it is
    /// not being read, because this judgement is the read. A trace with no verdict of its own (a skip, a cancellation, a
    /// joined stop) leaves the row as the live source has it. The colour switch is the one the judgement ran under, so
    /// a shadow verdict records the row that was actually shown.
    /// </summary>
    private sealed class AtThisStop : ITurnVerdictRowSource
    {
        private readonly ITurnVerdictRowSource _live;
        private readonly TurnVerdictTrace _trace;

        public AtThisStop(ITurnVerdictRowSource live, TurnVerdictTrace trace)
        {
            _live = live;
            _trace = trace;
        }

        public bool ColourEnabled(TenantId tenant) => _trace.ColourEnabled;

        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
        {
            var latest = _live.SnapshotLatest(tenant);
            if (_trace.Verdict is null) return latest;
            var copy = new Dictionary<string, TurnVerdictDto>(latest, StringComparer.Ordinal)
            {
                [_trace.SessionId] = _trace.Verdict,
            };
            return copy;
        }

        public bool IsReading(TenantId tenant, string sessionId)
            => (_trace.Verdict is null || !string.Equals(sessionId, _trace.SessionId, StringComparison.Ordinal))
               && _live.IsReading(tenant, sessionId);
    }
}
