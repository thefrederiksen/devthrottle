namespace CcDirector.Core.Account;

/// <summary>
/// Reports a Director-startup event to the configured CC Director Gateway on launch (Gateway
/// Centralization Phase 1, issue #632). The Director POSTs one event to
/// <c>&lt;gateway.url&gt;/telemetry/director-startup</c> so the Gateway (and later the cloud) can see
/// Directors coming online.
///
/// Implementations must treat the report as best-effort: it is fired once after the services
/// initialize and a failure must never block or delay the Director starting. When no Gateway is
/// configured the report is a logged no-op with no cloud call.
/// </summary>
public interface IDirectorStartupTelemetryReporter
{
    /// <summary>
    /// Reports one Director-startup event for the given <paramref name="directorId"/>. Throws on a
    /// transport or non-success response so the fire-and-forget caller can log it; the caller, not
    /// this method, owns the best-effort swallow.
    /// </summary>
    Task ReportStartupAsync(string directorId, CancellationToken ct = default);
}
