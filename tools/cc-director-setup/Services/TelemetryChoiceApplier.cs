using CcDirector.Core.Account;

namespace CcDirectorSetup.Services;

/// <summary>
/// Applies the person's usage-telemetry choice from the installer Privacy step (issue #659). The
/// per-account server flag is the source of truth: this writes the chosen value to the server with
/// <c>PATCH /api/v1/account/telemetry</c> using the Sign-in Bearer token, and ALWAYS mirrors the chosen
/// value into the local <c>config.json</c> <c>telemetry.enabled</c> as an offline cache the app can read
/// before it can reach <c>/auth/me</c>.
///
/// The server write is best-effort and must never block the install: a failed PATCH is logged and the
/// install continues (the choice can be re-set later in-app). The local mirror reflects the chosen value
/// either way. The access token is used only as the Bearer and is never written to the installer log.
/// </summary>
public sealed class TelemetryChoiceApplier
{
    private readonly AccountTelemetryClient _client;
    private readonly Action<bool> _mirrorToConfig;

    /// <summary>
    /// Creates the applier. <paramref name="client"/> defaults to a real <see cref="AccountTelemetryClient"/>
    /// (honoring the <c>DEVTHROTTLE_API_URL</c> override); tests inject one over a fake handler.
    /// <paramref name="mirrorToConfig"/> defaults to writing the local <c>config.json</c> mirror via
    /// <see cref="TelemetrySettings.SetEnabled"/>; tests inject a recording seam.
    /// </summary>
    public TelemetryChoiceApplier(AccountTelemetryClient? client = null, Action<bool>? mirrorToConfig = null)
    {
        _client = client ?? new AccountTelemetryClient();
        _mirrorToConfig = mirrorToConfig ?? TelemetrySettings.SetEnabled;
    }

    /// <summary>
    /// Reads the current per-account telemetry flag from <c>/auth/me</c> to pre-fill the Privacy
    /// checkbox. Returns the server value when it can be read; returns the default ON
    /// (<c>true</c>) when there is no token or the read fails, so an unreachable backend never leaves the
    /// checkbox in an unknown state. Never throws to the caller.
    /// </summary>
    /// <param name="accessToken">The Bearer access token captured at Sign-in, or null when unavailable.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<bool> ReadPrefillAsync(string? accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            SetupLog.Write("[TelemetryChoiceApplier] ReadPrefillAsync: no access token available -> default ON");
            return true;
        }

        try
        {
            var state = await _client.GetTelemetryStateAsync(accessToken, ct).ConfigureAwait(false);
            SetupLog.Write($"[TelemetryChoiceApplier] ReadPrefillAsync: server telemetry_enabled={state.TelemetryEnabled}");
            return state.TelemetryEnabled;
        }
        catch (Exception ex)
        {
            // Best-effort pre-fill: an unreachable or unauthorized backend (a dev stand-in token returns
            // 401 against production) defaults the checkbox ON, matching the server default.
            SetupLog.Write($"[TelemetryChoiceApplier] ReadPrefillAsync: could not read server flag ({ex.Message}) -> default ON");
            return true;
        }
    }

    /// <summary>
    /// Applies the chosen value: writes it to the server flag (best-effort) and mirrors it to the local
    /// <c>config.json</c> (always). Returns true when the server write succeeded, false when it was
    /// skipped or failed - either way the local mirror is written and the install continues. Never throws
    /// to the caller, so the wizard is never blocked.
    /// </summary>
    /// <param name="accessToken">The Bearer access token captured at Sign-in, or null when unavailable.</param>
    /// <param name="enabled">The chosen value (checkbox state).</param>
    /// <param name="ct">Cancels the server request.</param>
    public async Task<bool> ApplyChoiceAsync(string? accessToken, bool enabled, CancellationToken ct = default)
    {
        SetupLog.Write($"[TelemetryChoiceApplier] ApplyChoiceAsync: enabled={enabled}");

        var serverWritten = await WriteServerFlagBestEffortAsync(accessToken, enabled, ct).ConfigureAwait(false);

        // The local mirror is an offline cache the app reads before it can reach /auth/me. It always
        // reflects the chosen value, whether or not the server write succeeded.
        _mirrorToConfig(enabled);
        SetupLog.Write($"[TelemetryChoiceApplier] ApplyChoiceAsync: local config.json mirror written, enabled={enabled}, serverWritten={serverWritten}");

        return serverWritten;
    }

    /// <summary>
    /// Writes the chosen value to the per-account server flag, swallowing any failure into a logged
    /// false so a failed telemetry call never blocks the install. Returns true only when the PATCH
    /// succeeded.
    /// </summary>
    private async Task<bool> WriteServerFlagBestEffortAsync(string? accessToken, bool enabled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            SetupLog.Write("[TelemetryChoiceApplier] WriteServerFlagBestEffortAsync: no access token available -> skipping server write (local mirror only)");
            return false;
        }

        try
        {
            await _client.SetTelemetryEnabledAsync(accessToken, enabled, ct).ConfigureAwait(false);
            SetupLog.Write("[TelemetryChoiceApplier] WriteServerFlagBestEffortAsync: server flag written");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[TelemetryChoiceApplier] WriteServerFlagBestEffortAsync: server write failed ({ex.Message}) -> continuing (best-effort)");
            return false;
        }
    }
}
