using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// Where the API-mode verdict's facts are read from (mission #4070, phase 2): the stored receipts of every API-mode
/// session on a roster in ONE read for the account (<see cref="ApiModeVerdictReceipts"/>), and the recent events the feed
/// holds for each, read by tenant from memory. So stamping a roster costs one database query however many API-mode rows
/// it has, takes no receipt-store lock, and one account's session id can never fold another account's facts.
/// </summary>
public sealed class ApiModeDisplayFacts
{
    /// <summary>For an account and the API-mode session ids on its roster: each one's receipts, oldest prompt
    /// first.</summary>
    internal delegate IReadOnlyDictionary<string, IReadOnlyList<ApiModeVerdictReceipt>> ReceiptsReader(TenantId tenant, IReadOnlyCollection<string> sessionIds);

    private readonly IApiModeEventFeed _feed;
    private readonly ReceiptsReader _receipts;

    public ApiModeDisplayFacts(IApiModeEventFeed feed, ApiModeVerdictReceipts receipts)
        : this(feed, (receipts ?? throw new ArgumentNullException(nameof(receipts))).Read)
    {
    }

    /// <summary>The seam a test counts the reads through.</summary>
    internal ApiModeDisplayFacts(IApiModeEventFeed feed, ReceiptsReader receipts)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
    }

    /// <summary>Every asked-for session's stored receipts (oldest prompt first) in <paramref name="tenant"/>, in one
    /// read.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ApiModeVerdictReceipt>> ReadReceipts(TenantId tenant, IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return _receipts(tenant, sessionIds);
    }

    /// <summary>What the feed holds for the session in <paramref name="tenant"/> (memory only, no database).</summary>
    public ApiModeEventReplay Held(TenantId tenant, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _feed.Read(tenant, sessionId);
    }
}

/// <summary>
/// THE ONE PLACE A ROW IS STAMPED WITH ITS API-MODE VERDICT. It decides nothing: every word is
/// <see cref="ApiModeSessionFold"/>'s. It ASSIGNS on every row in both directions, so a row that left API mode, or a
/// re-served row, never keeps a verdict it no longer has; and a row that is not in API mode reads nothing at all (it
/// costs no read, and its <see cref="SessionDto.ApiMode"/> stays null and off the wire).
/// </summary>
internal static class ApiModeRowStamp
{
    /// <param name="rows">The rows to stamp.</param>
    /// <param name="facts">Where the facts are read; null from a caller that was not given them, and then no row
    /// carries a verdict - a verdict folded from no facts would be a confident wrong one.</param>
    /// <param name="tenant">The account the rows belong to; null stamps no verdict, for the same reason.</param>
    internal static void Stamp(IReadOnlyList<SessionDto> rows, ApiModeDisplayFacts? facts, TenantId? tenant)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var apiRows = new List<SessionDto>();
        foreach (var s in rows)
        {
            s.ApiMode = null;
            if (facts is not null && tenant is { IsValid: true }
                && string.Equals(s.ConnectionMode, ConnectionModes.Api, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(s.SessionId))
                apiRows.Add(s);
        }
        if (apiRows.Count == 0) return;

        // ONE receipt read for every API-mode row on the roster (review finding 1): never one per row.
        var t = tenant!.Value;
        var receipts = facts!.ReadReceipts(t, apiRows.Select(s => s.SessionId).ToList());
        foreach (var s in apiRows)
            s.ApiMode = ApiModeSessionFold.Fold(s.ConnectionMode, s.WheelHolder, GatewayDictationEndpoint.IsExited(s), receipts[s.SessionId], facts.Held(t, s.SessionId));
    }
}
