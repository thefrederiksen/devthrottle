using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the roster fold needs in order to put the Wingman's verdict on a row (the Wingman-on-every-turn mission,
/// slice D): the account's colour switch, every session's latest stored verdict in ONE read, and whether a verdict
/// is being formed for a session right now. A seam so the fold's read count can be counted in a test.
/// </summary>
public interface ITurnVerdictRowSource
{
    /// <summary>Whether this account's judged verdicts may reach a screen.</summary>
    bool ColourEnabled(TenantId tenant);

    /// <summary>Every session's latest stored verdict in this account, keyed by session id - one query.</summary>
    IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant);

    /// <summary>True while a verdict about this session's stop is being formed.</summary>
    bool IsReading(TenantId tenant, string sessionId);
}

/// <summary>The production row source: the account's settings, the verdict store, and the seat's reading state.</summary>
public sealed class TurnVerdictRowSource : ITurnVerdictRowSource
{
    private readonly Func<TenantId, TurnVerdictSettings> _settings;
    private readonly TurnVerdictStore _store;
    private readonly Func<TurnVerdictService?> _service;

    /// <param name="service">The seat, which is built lazily; null until it exists, and nothing is reading then.</param>
    public TurnVerdictRowSource(
        Func<TenantId, TurnVerdictSettings> settings,
        TurnVerdictStore store,
        Func<TurnVerdictService?> service)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public bool ColourEnabled(TenantId tenant) => _settings(tenant).ColourEnabled;

    public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _store.SnapshotLatest(tenant);

    public bool IsReading(TenantId tenant, string sessionId) => _service()?.IsReading(tenant, sessionId) == true;
}

/// <summary>
/// Stamps <see cref="SessionDto.TurnVerdict"/>, <see cref="SessionDto.VerdictState"/> and
/// <see cref="SessionDto.VerdictLabel"/> onto a roster, for the fold in <c>GatewayEndpoints.StampFleetRolesAndFold</c>.
///
/// ONE SNAPSHOT PER FOLD, AND ONLY WHEN THE COLOUR SWITCH IS ON. The fold runs over the whole account on the hot
/// path - every roster poll, every display sweep, every accepted Director push - so the verdicts are read once,
/// set-based, before the loop, never per session. An account whose colour switch is off is not read at all, and
/// every one of its rows is stamped "none" with no verdict, however many verdicts are stored: a shadow verdict
/// means nothing on the wire, so the fold needs no switch fact of its own.
///
/// EVERY FIELD IS ASSIGNED ON EVERY ROW, in both directions. The roster re-serves rows this fold stamped before,
/// so a stamp that was only ever set would outlive the verdict it came from.
/// </summary>
public static class TurnVerdictRowStamp
{
    /// <param name="rows">The rows to stamp.</param>
    /// <param name="source">Where the verdicts come from. Null (a diagnostics page, an older test) stamps "none".</param>
    /// <param name="tenant">The account the rows belong to. Null or invalid stamps "none" - the verdicts are
    /// partitioned by account and there is no read without one.</param>
    /// <returns>Whether this account's verdicts reached these rows - false when there is no source, no account,
    /// or the colour switch is off. Slice F's stamp runs on the same footing and asks this rather than reading
    /// the switch a second time, so the two cannot come to different answers about one account.</returns>
    public static bool Stamp(IReadOnlyList<SessionDto> rows, ITurnVerdictRowSource? source, TenantId? tenant)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (source is null || tenant is not { IsValid: true } account || !source.ColourEnabled(account))
        {
            foreach (var s in rows) None(s);
            return false;
        }

        var latest = source.SnapshotLatest(account);
        foreach (var s in rows)
        {
            if (string.IsNullOrEmpty(s.SessionId))
            {
                None(s);
                continue;
            }

            // Reading wins over a stored verdict: a read in flight means the stored one is about to be replaced,
            // and what the row shows is that the stop is being read.
            if (source.IsReading(account, s.SessionId))
            {
                Reading(s);
                continue;
            }

            if (!latest.TryGetValue(s.SessionId, out var verdict))
            {
                None(s);
                continue;
            }

            s.TurnVerdict = verdict;
            // THE WINGMAN ERROR, from this same record and nothing else. Not while the session works: its last
            // reading then describes a screen that is gone, and a Working edge is about to invalidate it.
            s.WingmanError = WingmanErrorFold.For(verdict, IsWorking(s));
            if (verdict.Failed)
            {
                s.VerdictState = VerdictStates.Failed;
                s.VerdictLabel = null;
            }
            else
            {
                s.VerdictState = VerdictStates.Judged;
                s.VerdictLabel = string.IsNullOrWhiteSpace(verdict.Label) ? null : verdict.Label;
            }
        }

        return true;
    }

    /// <summary>
    /// THE ROW SAYS THE STOP IS BEING READ. One place, because slice F writes it too: a snooze expiry that asks
    /// the judge does so AFTER this stamp has already run over the row, so the row it is holding still carries
    /// the verdict that is about to be replaced - and would go out red. It re-stamps through here rather than
    /// assigning the three fields itself, so there is one rule for what "reading" looks like on a row and not two.
    /// </summary>
    public static void Reading(SessionDto s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.VerdictState = VerdictStates.Reading;
        s.TurnVerdict = null;
        s.VerdictLabel = null;
        // Being read NOW - the first reading or a retry - so there is no error to report until it ends.
        s.WingmanError = null;
    }

    private static bool IsWorking(SessionDto s)
        => string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
           || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);

    private static void None(SessionDto s)
    {
        s.VerdictState = VerdictStates.None;
        s.TurnVerdict = null;
        s.VerdictLabel = null;
        s.WingmanError = null;
    }
}
