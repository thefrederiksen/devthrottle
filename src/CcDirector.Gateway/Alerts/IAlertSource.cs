using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Alerts;

/// <summary>
/// ONE FEATURE'S RECORDS, AS ALERTS (the Alerts mission, section 5.1). Each feature that asks the owner for something
/// implements this beside its own store, as a pure mapping from that store: no copied rows, no new state. The Gateway
/// composes every source into one list per account at read time (<see cref="AlertsService"/>).
///
/// A source returns every record that needs an answer, and every closed record that changed within
/// <see cref="AlertWindow.Earlier"/>. It fills every field but <see cref="AlertDto.Seen"/>, which is the marks' to set.
/// </summary>
public interface IAlertSource
{
    /// <summary>The kind this source produces, one of <see cref="AlertKinds"/>.</summary>
    string Kind { get; }

    /// <summary>This account's alerts of this kind, as they stand at <paramref name="nowUtc"/>.</summary>
    IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc);
}

/// <summary>The window every source applies to closed records, in one place.</summary>
public static class AlertWindow
{
    /// <summary>A closed record is an "Earlier" alert for this long after it last changed (section 5.2).</summary>
    public static readonly TimeSpan Earlier = TimeSpan.FromHours(24);

    /// <summary>True when an alert belongs on the list: it needs an answer, or it changed within <see cref="Earlier"/>.</summary>
    public static bool Keeps(bool needsAnswer, DateTime updatedAtUtc, DateTime nowUtc)
        => needsAnswer || nowUtc - updatedAtUtc <= Earlier;

    /// <summary>The latest of the given instants, all UTC.</summary>
    public static DateTime Latest(DateTime first, params DateTime?[] others)
    {
        var latest = first;
        foreach (var other in others)
            if (other is { } o && o > latest) latest = o;
        return latest;
    }
}
