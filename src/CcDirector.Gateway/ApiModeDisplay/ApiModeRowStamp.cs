using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// Where the API-mode verdict's facts are read from, for one account (mission #4070, phase 2): the session's stored
/// receipts, read INSIDE the account's tenant scope (the store is partitioned by the ambient tenant), and the recent
/// events the feed holds for it, read by tenant. So one account's session id can never fold another account's facts.
/// </summary>
public sealed class ApiModeDisplayFacts
{
    private readonly IApiModeEventFeed _feed;
    private readonly ApiModeReceiptStore _receipts;
    private readonly HostedTenantBoundary? _boundary;

    public ApiModeDisplayFacts(IApiModeEventFeed feed, ApiModeReceiptStore receipts, HostedTenantBoundary? boundary)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _boundary = boundary;
    }

    /// <summary>The session's stored receipts (oldest prompt first) and what the feed holds for it, in
    /// <paramref name="tenant"/>.</summary>
    public (IReadOnlyList<ApiModeReceiptDto> Receipts, ApiModeEventReplay Held) Read(TenantId tenant, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        IReadOnlyList<ApiModeReceiptDto> receipts;
        using (_boundary?.EnterScope(tenant))
            receipts = _receipts.Read(sessionId);
        return (receipts, _feed.Read(tenant, sessionId));
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
        foreach (var s in rows)
        {
            if (facts is null || tenant is not { IsValid: true } t
                || !string.Equals(s.ConnectionMode, ConnectionModes.Api, StringComparison.Ordinal)
                || string.IsNullOrEmpty(s.SessionId))
            {
                s.ApiMode = null;
                continue;
            }
            var (receipts, held) = facts.Read(t, s.SessionId);
            s.ApiMode = ApiModeSessionFold.Fold(s.ConnectionMode, s.WheelHolder, receipts, held);
        }
    }
}
