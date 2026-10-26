using System.Globalization;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>The kinds of factory email a kept row can carry.</summary>
public static class FactoryEmailKinds
{
    /// <summary>08:00, every day: every problem still open, then one line per factory for the last 24 hours. The only
    /// factory email the Gateway sends.</summary>
    public const string Morning = "morning-report";

    /// <summary>The 16:00 and midnight check, sent until 26 October 2026, when the owner chose one factory email a day.
    /// No slot is a check any more and nothing writes this kind; it stays defined only so the checks already kept are
    /// still read, listed and named until the store's retention drops them.</summary>
    public const string Check = "check";
}

/// <summary>
/// One email slot: the 08:00 shift boundary of one day in the account's time zone.
/// </summary>
/// <param name="Key">The slot's key, unique per account: the boundary's local date and hour, for example
/// <c>2026-10-11T08</c>.</param>
/// <param name="Kind">Always <see cref="FactoryEmailKinds.Morning"/>.</param>
/// <param name="BoundaryUtc">The boundary: the end of the night shift.</param>
/// <param name="DayStartUtc">24 wall-clock hours before the boundary (three shifts back), the start of the 08:00 report's
/// one line per factory.</param>
public sealed record FactoryEmailSlot(
    string Key, string Kind, DateTime BoundaryUtc, DateTime DayStartUtc)
{
    /// <summary>The cut-off for problems this slot reports: the boundary plus <see cref="FactoryEmailSlots.Settle"/>, so a
    /// "ran past its shift" the cron sweep records just after the boundary is counted in the shift it belongs to.</summary>
    public DateTime CutoffUtc => BoundaryUtc + FactoryEmailSlots.Settle;
}

/// <summary>
/// WHEN THE FACTORY EMAIL IS SENT: once a day, at 08:00 in the account's time zone - the start of the morning shift
/// <see cref="WorkShifts"/> owns (the owner, 26 October 2026: "one email a day - a new problem shows first in the next
/// 08:00 email"). The boundary is worked out there and nowhere else, so the slot is always exactly the end of the night
/// shift, daylight-saving days included. The 16:00 and midnight boundaries are no slot at all: a problem raised in the
/// day waits, open, for the next 08:00 email's "Still not fixed".
///
/// A slot is DUE from <see cref="Settle"/> after its boundary - the cron sweep runs every minute, and that is long
/// enough for it to record the "ran past its shift" problems of the shift that just ended - until
/// <see cref="LateWindow"/> after it. Past that it is not sent at all: a Gateway that was down all morning does not send
/// the 08:00 report at three in the afternoon.
/// </summary>
public static class FactoryEmailSlots
{
    /// <summary>How long after a boundary its email is composed.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);

    /// <summary>How long after a boundary its email may still be sent (a retry after a failed send included).</summary>
    public static readonly TimeSpan LateWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// The slot due at <paramref name="nowUtc"/>, or null when none is (outside the 08:00 email's window: inside its settle
    /// time, past its late window, or any other hour of the day). Pure.
    /// </summary>
    public static FactoryEmailSlot? DueAt(DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        // The late window is shorter than any morning shift, so a due slot always lies in the morning shift's start.
        var shift = WorkShifts.ShiftOf(now - Settle, zone);
        if (shift.Name != WorkShifts.Morning)
            return null;
        var boundary = shift.StartUtc;
        if (now < boundary + Settle || now > boundary + LateWindow)
            return null;
        return AtBoundary(boundary, zone);
    }

    /// <summary>The slot at a day's 08:00 boundary. Throws when <paramref name="boundaryUtc"/> is not the start of a
    /// morning shift. Pure.</summary>
    public static FactoryEmailSlot AtBoundary(DateTime boundaryUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var boundary = DateTime.SpecifyKind(boundaryUtc, DateTimeKind.Utc);
        var starting = WorkShifts.ShiftOf(boundary, zone);
        if (starting.StartUtc != boundary)
            throw new ArgumentException($"{boundary:o} is not the start of a shift in {zone.Id}", nameof(boundaryUtc));
        if (starting.Name != WorkShifts.Morning)
            throw new ArgumentException(
                $"{boundary:o} starts the {starting.Name} shift in {zone.Id}; the factory email is sent at 08:00 only", nameof(boundaryUtc));

        var night = WorkShifts.ShiftOf(boundary.AddTicks(-1), zone);
        var dayStart = WorkShifts.ShiftOf(WorkShifts.ShiftOf(night.StartUtc.AddTicks(-1), zone).StartUtc.AddTicks(-1), zone).StartUtc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(boundary, zone);
        var key = local.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        return new FactoryEmailSlot(key, FactoryEmailKinds.Morning, boundary, dayStart);
    }
}
