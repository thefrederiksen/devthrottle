namespace CcDirector.Core.Account;

/// <summary>
/// Reports a successful login to the DevThrottle backend's always-on login telemetry endpoint
/// (devthrottle_internal issue #57). This is the authentication floor (issue #40): having an account
/// inherently means a login is recorded, so it carries NO consent gate and is distinct from the
/// richer, user-controllable usage telemetry (<see cref="UsageTelemetry"/>, governed by
/// <see cref="TelemetrySettings"/>).
///
/// Implementations must treat the report as best-effort: it is fired once on a successful login
/// completion and a failure must never block or fail the user's login.
/// </summary>
public interface ILoginTelemetryReporter
{
    /// <summary>
    /// Reports one login event for the signed-in account, authenticating with the access token the
    /// login completion produced. Throws on a transport or non-success response so the fire-and-forget
    /// caller can log it; the caller, not this method, owns the best-effort swallow.
    /// </summary>
    Task ReportLoginAsync(string accessToken, CancellationToken ct = default);
}
