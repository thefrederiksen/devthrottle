using System.Collections.Concurrent;
using System.Security.Cryptography;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// How a browser is let into one box's desktop on the desktop host (Factory Boxes, issue #4258). The desktop is
/// served on its own site, so it can never see the Gateway's sign-in: no cookie of the Gateway's, no Authorization
/// header, nothing in the Cockpit's storage. Instead:
///
/// <list type="number">
/// <item>The Cockpit, signed in on the Gateway's own host, asks for a <b>ticket</b> for one box. The ticket is
/// single-use, lives <see cref="TicketLifetime"/>, and names the account and the box.</item>
/// <item>The browser opens the desktop host with the ticket in the address. The desktop host redeems it - once - for
/// a <b>desktop session</b>, and sets that session's token in an HttpOnly, Secure, SameSite=Strict cookie scoped to
/// the one box's path, good for <see cref="SessionLifetime"/>.</item>
/// <item>Every desktop request and the WebSocket upgrade are checked against that cookie: the session must exist,
/// be unexpired, and be for THIS box.</item>
/// </list>
///
/// Held in memory: a Gateway restart ends every desktop session, and the person opens the box again from the
/// Cockpit. The hosted Gateway runs as one instance (Basic B2), so one memory is the whole truth. Neither a ticket
/// nor a session token is ever written to the log.
/// </summary>
public sealed class BoxDesktopTickets
{
    /// <summary>How long a ticket may wait to be redeemed. Long enough for a phone to open a link, short enough that a
    /// ticket seen in a browser's history is already dead.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How long a desktop session lasts from the moment its ticket was redeemed: a working day. The Cockpit's
    /// Open button asks for a fresh ticket each time, so an expired session costs one press.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    /// <summary>The name of the cookie that carries a desktop session. One per box, told apart by its path.</summary>
    public const string CookieName = "cc-box-desktop";

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    private sealed record Entry(TenantId Tenant, string BoxId, DateTimeOffset ExpiresAt);

    public BoxDesktopTickets(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Desktop sessions not yet expired or swept. For tests and diagnostics.</summary>
    public int LiveSessionCount => _sessions.Count;

    /// <summary>Mint a single-use ticket for <paramref name="boxId"/> in <paramref name="tenant"/>.</summary>
    public (string Ticket, DateTimeOffset ExpiresAt) Mint(TenantId tenant, string boxId)
    {
        Sweep();
        var ticket = NewSecret();
        var expires = _time.GetUtcNow() + TicketLifetime;
        _tickets[Hash(ticket)] = new Entry(tenant, boxId, expires);
        FileLog.Write($"[BoxDesktopTickets] Mint: box={boxId} tenant={tenant.ToLogString()} expires={expires:O}");
        return (ticket, expires);
    }

    /// <summary>
    /// Redeem a ticket for a desktop session of <paramref name="boxId"/>: once, before it expires, and only for the box
    /// it was minted for. Null (and logged) otherwise. A redeemed ticket is gone whatever the outcome.
    /// </summary>
    public BoxDesktopSession? Redeem(string? ticket, string boxId)
    {
        Sweep();
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(Hash(ticket), out var entry))
        {
            FileLog.Write($"[BoxDesktopTickets] Redeem REFUSED: box={boxId}: no such ticket (unknown, already used, or expired)");
            return null;
        }
        if (entry.ExpiresAt <= _time.GetUtcNow() || !string.Equals(entry.BoxId, boxId, StringComparison.Ordinal))
        {
            FileLog.Write($"[BoxDesktopTickets] Redeem REFUSED: box={boxId}: the ticket {(entry.ExpiresAt <= _time.GetUtcNow() ? "expired" : $"is for box {entry.BoxId}")}");
            return null;
        }
        var token = NewSecret();
        var session = new Entry(entry.Tenant, boxId, _time.GetUtcNow() + SessionLifetime);
        _sessions[Hash(token)] = session;
        FileLog.Write($"[BoxDesktopTickets] Redeem: box={boxId} tenant={entry.Tenant.ToLogString()} session until {session.ExpiresAt:O}");
        return new BoxDesktopSession(token, entry.Tenant, boxId, session.ExpiresAt);
    }

    /// <summary>The desktop session a cookie value holds for <paramref name="boxId"/>, or null when it holds none.</summary>
    public BoxDesktopSession? Authenticate(IEnumerable<string> tokens, string boxId)
    {
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(Hash(token), out var entry)) continue;
            if (entry.ExpiresAt <= _time.GetUtcNow())
            {
                _sessions.TryRemove(Hash(token), out _);
                continue;
            }
            if (!string.Equals(entry.BoxId, boxId, StringComparison.Ordinal)) continue;
            return new BoxDesktopSession(token, entry.Tenant, boxId, entry.ExpiresAt);
        }
        return null;
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _tickets)
            if (entry.ExpiresAt <= now) _tickets.TryRemove(key, out _);
        foreach (var (key, entry) in _sessions)
            if (entry.ExpiresAt <= now) _sessions.TryRemove(key, out _);
    }

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    // Kept by hash, so a lookup compares fixed-length digests and the store never holds a usable secret as a key.
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>One redeemed desktop session: its secret token (the cookie's value), whose it is, which box, until when.</summary>
public sealed record BoxDesktopSession(string Token, TenantId Tenant, string BoxId, DateTimeOffset ExpiresAt);
