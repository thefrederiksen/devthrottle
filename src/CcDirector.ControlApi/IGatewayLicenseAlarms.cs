using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The seam through which this desktop reads the license alarms open now (<c>GET /gateway/licenses/alarms</c>, mission
/// "Licenses - Always Ready, Loud When Not", step 2): the same answer the Cockpit and the phone draw their banner from,
/// with the Gateway's finished words and tone. A read only; nothing on the desktop raises or clears an alarm.
/// </summary>
public interface IGatewayLicenseAlarms
{
    /// <summary>The alarms open now, or null when no Gateway is configured. Throws when one is configured and the read
    /// fails.</summary>
    Task<LicenseAlarmsResponse?> GetLicenseAlarmsAsync(CancellationToken ct = default);
}
