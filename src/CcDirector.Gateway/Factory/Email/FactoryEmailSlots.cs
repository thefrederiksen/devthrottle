using System.Globalization;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>The three kinds of factory email, one per shift boundary.</summary>
public static class FactoryEmailKinds
{
    /// <summary>08:00, every day: every problem still open, then one line per factory for the last 24 hours.</summary>
    public const string Morning = "morning-report";

    /// <summary>16:00 and midnight: sent only when the shift that just ended raised a new problem.</summary>
    public const string Check = "check";
}

/// <summary>
/// One email slot: a shift boundary in the account's time zone, and what is sent at it.
/// </summary>
/// <param name="Key">The slot's key, unique per account: the boundary's local date and hour, for example
/// <c>2026-10-11T08</c>. A midnight boundary carries the date of the day it starts.</param>
/// <param name="Kind">One of the <see cref="FactoryEmailKinds"/>.</param>
/// <param name="BoundaryUtc">The boundary (the end of <see cref="EndedShift"/>).</param>
/// <param name="EndedShift">The shift that ended at the boundary.</param>
/// <param name="PreviousBoundaryUtc">The boundary before this one - where the last slot's new problems were counted to.</param>
/// <param name="DayStartUtc">24 wall-clock hours before the boundary (three shifts back), the start of the 08:00 report's
/// one line per factory.</param>
public sealed record FactoryEmailSlot(
    string Key, string Kind, DateTime BoundaryUtc, WorkShift EndedShift, DateTime PreviousBoundaryUtc, DateTime DayStartUtc)
{
    /// <summary>The cut-off for problems this slot reports: the boundary plus <see cref="FactoryEmailSlots.Settle"/>, so a
    /// "ran past its shift" the cron sweep records just after the boundary is counted in the shift it belongs to.</summary>
    public DateTime CutoffUtc => BoundaryUtc + FactoryEmailSlots.Settle;

    /// <summary>Where the previous slot's cut-off was: a check's NEW problems were recorded after this.</summary>
    public DateTime PreviousCutoffUtc => PreviousBoundaryUtc + FactoryEmailSlots.Settle;
}

/// <summary>
/// WHEN THE FACTORY EMAIL IS SENT (Factory Control, step 4, the owner's ruling of 10 October 2026): at 08:00, 16:00 and
/// midnight in the account's time zone - the three shift boundaries <see cref="WorkShifts"/> owns. The boundaries are
/// worked out there and nowhere else, so a slot is always exactly the end of a shift, daylight-saving days included.
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
    /// The slot due at <paramref name="nowUtc"/>, or null when none is (inside a slot's settle time, or past its late
    /// window). Pure.
    /// </summary>
    public static FactoryEmailSlot? DueAt(DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var boundary = WorkShifts.ShiftOf(now - Settle, zone).StartUtc;
        if (now < boundary + Settle || now > boundary + LateWindow)
            return null;
        return AtBoundary(boundary, zone);
    }

    /// <summary>The slot at a shift boundary. Throws when <paramref name="boundaryUtc"/> is not one. Pure.</summary>
    public static FactoryEmailSlot AtBoundary(DateTime boundaryUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var boundary = DateTime.SpecifyKind(boundaryUtc, DateTimeKind.Utc);
        var starting = WorkShifts.ShiftOf(boundary, zone);
        if (starting.StartUtc != boundary)
            throw new ArgumentException($"{boundary:o} is not the start of a shift in {zone.Id}", nameof(boundaryUtc));

        var ended = WorkShifts.ShiftOf(boundary.AddTicks(-1), zone);
        var previous = ended.StartUtc;
        var dayStart = WorkShifts.ShiftOf(WorkShifts.ShiftOf(previous.AddTicks(-1), zone).StartUtc.AddTicks(-1), zone).StartUtc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(boundary, zone);
        var kind = starting.Name == WorkShifts.Morning ? FactoryEmailKinds.Morning : FactoryEmailKinds.Check;
        var key = local.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        return new FactoryEmailSlot(key, kind, boundary, ended, previous, dayStart);
    }
}
