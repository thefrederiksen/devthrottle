using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// The pure, UI-framework-free decisions and persistence the Gateway tray (CcDirector.GatewayApp)
/// makes for the first-run consent SCREEN shown once at the Gateway's first launch (Gateway
/// Centralization Phase 3, issue #650). Kept here in the library - not buried in the Avalonia
/// window's code-behind - so "should the consent screen be shown on launch?" and "what does
/// acknowledging it write?" are unit-testable without an Avalonia UI thread (mirroring
/// <see cref="GatewaySignInTraySurface"/> for the sign-in surface).
///
/// The screen is shown ONCE: the Gateway shows it on launch only while it has not yet been
/// acknowledged (<see cref="GatewayConsentAck"/>), and a subsequent launch never re-shows it. The
/// usage-sharing choice on the screen writes the Gateway's CENTRALIZED consent setting
/// (<see cref="TelemetryConsentConfig"/>, issue #649) - the one fleet-wide setting - not a per-Director
/// one. Acknowledging records both in one step: the centralized consent value and the acknowledgement.
/// </summary>
public static class GatewayConsentSurface
{
    /// <summary>
    /// Whether the Gateway should SHOW the first-run consent screen on launch: only when it has not
    /// yet been acknowledged. An already-acknowledged Gateway never re-shows it on a subsequent launch
    /// (acceptance criterion 2).
    /// </summary>
    public static bool ShouldShowConsentOnLaunch() => !GatewayConsentAck.HasAcknowledged();

    /// <summary>
    /// The usage-sharing choice the consent screen pre-selects, read from the Gateway's centralized
    /// consent setting (<see cref="TelemetryConsentConfig"/>) so the checkbox reflects the real current
    /// value (on by default) rather than a hardcoded one.
    /// </summary>
    public static bool CurrentUsageSharingChoice() => TelemetryConsentConfig.Get();

    /// <summary>
    /// Acknowledges the consent screen: writes the usage-sharing choice to the Gateway's centralized
    /// consent setting (<see cref="TelemetryConsentConfig.Set"/>, issue #649) and records that the
    /// screen has been shown and acknowledged (<see cref="GatewayConsentAck.MarkAcknowledged"/>), so it
    /// is shown only once. Both writes go to the Gateway's <c>config.json</c> (acceptance criteria 1
    /// and 3).
    /// </summary>
    /// <param name="shareUsage">
    /// The usage-sharing choice the user made on the screen (the checkbox value); becomes the
    /// centralized consent value.
    /// </param>
    public static void Acknowledge(bool shareUsage)
    {
        FileLog.Write($"[GatewayConsentSurface] Acknowledge: writing centralized consent shareUsage={shareUsage} and recording the gateway acknowledgement");
        TelemetryConsentConfig.Set(shareUsage);
        GatewayConsentAck.MarkAcknowledged();
        FileLog.Write("[GatewayConsentSurface] Acknowledge: centralized consent set and gateway acknowledgement recorded");
    }
}
