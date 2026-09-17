using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Messaging;

/// <summary>
/// What waits UNREAD in one session's fleet inbox, counted - the only input the row line is folded from.
/// Every unread message is in exactly one of the four counts: a stuck message is counted as stuck whatever
/// its kind, and an open one by its kind.
/// </summary>
/// <param name="Waiting">Open messages from another session: a message, a report, a team or a granted copy.</param>
/// <param name="Replies">Open replies to a question this session asked (slice 3).</param>
/// <param name="Notices">Open notices the Gateway itself wrote (a stuck or a no-reply notice).</param>
/// <param name="Stuck">Unread messages of any kind the doorbell gave up on (slice 2).</param>
/// <param name="OldestStuckWrittenUtc">When the oldest stuck message was written; null when none is stuck.</param>
public sealed record FleetInboxCounts(int Waiting, int Replies, int Notices, int Stuck, DateTime? OldestStuckWrittenUtc)
{
    /// <summary>Nothing waits.</summary>
    public static readonly FleetInboxCounts None = new(0, 0, 0, 0, null);
}

/// <summary>
/// Where the fold reads the inbox from: every session's counts in ONE read per fold pass. A seam so a test can
/// count the reads and so the fold does not reach into the store's type. The production source is
/// <see cref="FleetMessageStore"/>.
/// </summary>
public interface IFleetInboxLineSource
{
    /// <summary>Every session in this account with at least one unread message, keyed by session id (lower
    /// case, as the store keeps it). A session with nothing unread is absent.</summary>
    IReadOnlyDictionary<string, FleetInboxCounts> UnreadCountsByRecipient(TenantId tenant);
}

/// <summary>
/// THE ROW LINE (Message Load mission, slice 4, ruling 12): one string per session saying what waits in its
/// fleet inbox, or null when nothing does. PURE - the counts and the clock in, the words out - so every shape
/// is provable without a database.
///
/// The parts, in this order, joined with "; ":
///  - stuck: "1 message stuck, unread for 20 minutes" / "3 messages stuck, the oldest unread for 2 hours";
///  - waiting: "2 messages waiting";
///  - replies: "1 reply waiting";
///  - notices: "1 notice from the Gateway waiting".
/// Stuck leads because it is the one part that says something went wrong. A part with a zero count is left out.
///
/// THE AGE is measured from when the stuck message was WRITTEN, because "unread for" is how long it has been
/// waiting to be read, not how long since it was marked. It is worded in whole minutes under two hours, whole
/// hours under two days, and whole days after that, always rounded down. A write time in the future (a clock
/// step) reads as "less than a minute" rather than a negative age.
/// </summary>
public static class FleetInboxLineFold
{
    /// <summary>The separator between parts. A stuck part carries a comma of its own, so parts are not
    /// separated by commas.</summary>
    public const string PartSeparator = "; ";

    /// <summary>The line for these counts at this moment, or null when nothing waits.</summary>
    public static string? Fold(FleetInboxCounts? counts, DateTime nowUtc)
    {
        if (counts is null) return null;
        if (counts.Waiting < 0 || counts.Replies < 0 || counts.Notices < 0 || counts.Stuck < 0)
            throw new ArgumentOutOfRangeException(nameof(counts), counts, "An inbox count cannot be negative.");

        var parts = new List<string>(4);
        if (counts.Stuck > 0)
        {
            if (counts.OldestStuckWrittenUtc is not { } written)
                throw new ArgumentException("A stuck count needs the time the oldest stuck message was written.", nameof(counts));
            var age = Age(nowUtc - written);
            parts.Add(counts.Stuck == 1
                ? $"1 message stuck, unread for {age}"
                : $"{counts.Stuck} messages stuck, the oldest unread for {age}");
        }
        if (counts.Waiting > 0)
            parts.Add($"{Count(counts.Waiting, "message", "messages")} waiting");
        if (counts.Replies > 0)
            parts.Add($"{Count(counts.Replies, "reply", "replies")} waiting");
        if (counts.Notices > 0)
            parts.Add($"{Count(counts.Notices, "notice", "notices")} from the Gateway waiting");

        return parts.Count == 0 ? null : string.Join(PartSeparator, parts);
    }

    /// <summary>How long, in the words the line uses. Rounded down.</summary>
    public static string Age(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (elapsed < TimeSpan.FromHours(2)) return Count((int)elapsed.TotalMinutes, "minute", "minutes");
        if (elapsed < TimeSpan.FromDays(2)) return Count((int)elapsed.TotalHours, "hour", "hours");
        return Count((int)elapsed.TotalDays, "day", "days");
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

/// <summary>
/// Stamps <see cref="SessionDto.InboxLine"/> onto a set of rows for the fold in
/// <c>GatewayEndpoints.StampFleetRolesAndFold</c>.
///
/// ONE READ PER FOLD. The fold runs on the hot path - every roster read, every display sweep, every accepted
/// Director push - so the inbox is counted once, grouped, for the whole account, and never per session.
///
/// EVERY ROW IS ASSIGNED, in both directions. The roster re-serves rows a fold stamped before, so a line that
/// was only ever set would outlive the messages it counted. No source or no account stamps null on every row:
/// a fold that cannot read the inbox says nothing, rather than guessing.
/// </summary>
public static class FleetInboxLineStamp
{
    /// <param name="rows">The rows to stamp.</param>
    /// <param name="source">Where the counts come from; null stamps null.</param>
    /// <param name="tenant">The account the rows belong to; null or invalid stamps null (the inbox is
    /// partitioned by account and there is no read without one).</param>
    /// <param name="nowUtc">The fold's one moment, which the stuck age is measured to.</param>
    public static void Stamp(IReadOnlyList<SessionDto> rows, IFleetInboxLineSource? source, TenantId? tenant, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (source is null || tenant is not { IsValid: true } account)
        {
            foreach (var s in rows) s.InboxLine = null;
            return;
        }

        var counts = source.UnreadCountsByRecipient(account);
        foreach (var s in rows)
        {
            if (string.IsNullOrEmpty(s.SessionId) || counts.Count == 0)
            {
                s.InboxLine = null;
                continue;
            }
            // The store keeps recipient ids trimmed and lower-cased; a Director's id is a GUID in either case.
            counts.TryGetValue(s.SessionId.Trim().ToLowerInvariant(), out var mine);
            s.InboxLine = FleetInboxLineFold.Fold(mine, nowUtc);
        }
    }
}
