namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The one clock the Gateway's license code is judged by: the time a usage report is taken at, and the time the
/// once-a-minute check decides at. One clock for both, because the check judges how old a report is by comparing
/// the two. The real clock in production (<see cref="GatewayHost.LicenseClock"/>); a test gives the host its own.
/// </summary>
public sealed class LicenseGatewayClock
{
    private readonly Func<DateTime> _utcNow;

    public LicenseGatewayClock(Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        _utcNow = utcNow;
    }

    /// <summary>The time now, in UTC.</summary>
    public DateTime UtcNow() => _utcNow();
}
