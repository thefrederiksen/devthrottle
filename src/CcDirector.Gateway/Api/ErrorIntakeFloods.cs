using System.Globalization;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE FLOOD RECORD (the Error Logging mission, issue #3675, the owner's ruling of 9 October): every hour in which an
/// error-intake route dropped reports because a limit was hit becomes ONE row in the central error store - component
/// <c>gateway</c>, the route, how many were dropped and in which hour. Before this the only trace of a flood was a
/// FileLog line on the hosted container's temporary disk, gone on the next deploy, so the nightly reader could never
/// see that reports had been lost.
///
/// The routes count here when they refuse (<see cref="Dropped"/>); <see cref="GatewayErrorSink"/> writes the closed
/// hours into the store. Two properties hold by construction:
///   - The record is NEVER dropped by the limit it reports. It does not go through any route, so no route limit
///     applies, and the sink writes it straight to the store, outside its own hourly budget and pending table. A
///     write that fails puts the counts back (<see cref="Restore"/>) and the next flush tries again.
///   - It cannot recurse. Counting is an in-memory addition that logs nothing, and the sink's own drops are counted
///     here under <see cref="GatewayLog"/> - a count, never a report of its own.
///
/// ONE ROW PER INTAKE PER HOUR, because the counts are keyed on the intake and the clock hour, and an hour is only
/// taken once it has ended (<see cref="TakeClosed"/>). The one exception is a Gateway that stops: its open hour is
/// written as it goes (<see cref="TakeAll"/>), because the next process does not hold its counts. During a deploy two
/// processes can each write a row for the same hour; each counts only its own drops, so the two add up.
/// </summary>
internal sealed class ErrorIntakeFloods
{
    /// <summary>The process-wide counts every intake route writes to.</summary>
    public static ErrorIntakeFloods Shared { get; } = new();

    /// <summary>The installer's credential-less route.</summary>
    public const string InstallReports = "POST " + InstallReportEndpoints.Path;
    /// <summary>The Director, launcher and tool route.</summary>
    public const string DirectorErrors = "POST " + DirectorErrorEndpoints.Path;
    /// <summary>The browsers' route.</summary>
    public const string ClientErrors = "POST /client-errors";
    /// <summary>The website's route.</summary>
    public const string WebsiteErrors = WebsiteErrorEndpoints.Intake;
    /// <summary>Not a route: the Gateway's own failure lines, which <see cref="GatewayErrorSink"/> stores within a budget.</summary>
    public const string GatewayLog = "the Gateway's own error log";

    internal const string Source = "ErrorIntakeLimit";
    internal const string Kind = "flood";
    internal const string ErrorCode = "rate_limited";

    private readonly object _lock = new();
    private readonly Dictionary<(string Intake, DateTime HourUtc), ErrorIntakeFlood> _open = new();

    /// <summary>Count <paramref name="count"/> reports <paramref name="intake"/> dropped at <paramref name="nowUtc"/>.
    /// Never blocks on anything but its own lock and never logs, so a route may call it from inside its own limit.</summary>
    public void Dropped(string intake, int count, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intake);
        if (count <= 0) return;
        var hour = HourOf(nowUtc);
        lock (_lock)
        {
            var key = (intake, hour);
            _open[key] = _open.TryGetValue(key, out var flood)
                ? flood with
                {
                    Dropped = flood.Dropped + count,
                    FirstDropUtc = nowUtc < flood.FirstDropUtc ? nowUtc : flood.FirstDropUtc,
                    LastDropUtc = nowUtc > flood.LastDropUtc ? nowUtc : flood.LastDropUtc,
                }
                : new ErrorIntakeFlood(intake, hour, count, nowUtc, nowUtc);
        }
    }

    /// <summary>Take every hour that has ended by <paramref name="nowUtc"/>; the hour still running stays.</summary>
    public IReadOnlyList<ErrorIntakeFlood> TakeClosed(DateTime nowUtc)
    {
        var current = HourOf(nowUtc);
        lock (_lock)
        {
            var closed = _open.Where(e => e.Key.HourUtc < current).Select(e => e.Value).ToList();
            foreach (var flood in closed) _open.Remove((flood.Intake, flood.HourUtc));
            return closed;
        }
    }

    /// <summary>Take every hour, the running one included - for a Gateway that is stopping.</summary>
    public IReadOnlyList<ErrorIntakeFlood> TakeAll()
    {
        lock (_lock)
        {
            var all = _open.Values.ToList();
            _open.Clear();
            return all;
        }
    }

    /// <summary>Put back floods whose write failed, adding to anything counted for the same hour since.</summary>
    public void Restore(IEnumerable<ErrorIntakeFlood> floods)
    {
        lock (_lock)
        {
            foreach (var flood in floods)
            {
                var key = (flood.Intake, flood.HourUtc);
                _open[key] = _open.TryGetValue(key, out var since)
                    ? flood with
                    {
                        Dropped = flood.Dropped + since.Dropped,
                        FirstDropUtc = since.FirstDropUtc < flood.FirstDropUtc ? since.FirstDropUtc : flood.FirstDropUtc,
                        LastDropUtc = since.LastDropUtc > flood.LastDropUtc ? since.LastDropUtc : flood.LastDropUtc,
                    }
                    : flood;
            }
        }
    }

    /// <summary>How many reports are counted and not yet written, for every intake.</summary>
    internal long Pending
    {
        get { lock (_lock) return _open.Values.Sum(f => f.Dropped); }
    }

    /// <summary>Test seam: forget every count.</summary>
    internal void ResetForTests()
    {
        lock (_lock) _open.Clear();
    }

    private static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
}

/// <summary>One intake's drops in one clock hour.</summary>
internal sealed record ErrorIntakeFlood(string Intake, DateTime HourUtc, long Dropped, DateTime FirstDropUtc, DateTime LastDropUtc)
{
    /// <summary>
    /// The stored row. The message names the intake first, in the "VERB /route" shape the fingerprint keeps apart by
    /// route, then the count and the hour; the count and the hour fold away in the fingerprint, so every flood of one
    /// route is one problem, and the occurrences of that problem add up to the reports it lost.
    /// </summary>
    public ErrorReportRecord ToRecord(DateTime nowUtc, GatewayErrorSink.ProcessFacts facts) => new()
    {
        ReceivedUtc = nowUtc,
        Component = ErrorReportLimits.Gateway,
        Account = "",
        Device = "",
        MachineId = facts.MachineId,
        ProductVersion = facts.ProductVersion,
        Os = facts.Os,
        OsVersion = facts.OsVersion,
        Arch = facts.Arch,
        Source = ErrorIntakeFloods.Source,
        Kind = ErrorIntakeFloods.Kind,
        Message = $"{Intake} dropped {Dropped} report(s) over its limit in the hour from "
            + HourUtc.ToString("yyyy-MM-dd'T'HH':00Z'", CultureInfo.InvariantCulture),
        RepeatCount = (int)Math.Min(Dropped, int.MaxValue),
        FirstSeenUtc = FirstDropUtc,
        LastSeenUtc = LastDropUtc,
        Surface = Intake,
        HttpStatus = Intake == ErrorIntakeFloods.GatewayLog ? null : Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests,
        ErrorCode = ErrorIntakeFloods.ErrorCode,
    };
}
