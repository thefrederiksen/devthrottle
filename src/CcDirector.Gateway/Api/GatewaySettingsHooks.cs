namespace CcDirector.Gateway.Api;

/// <summary>
/// The host-process-owned bits the Cockpit Settings page needs that the <see cref="GatewayHost"/>
/// itself cannot know. The Gateway library hosts the REST surface, but the GatewayApp tray process
/// owns the run mode ("managed" vs "dev") and the per-user autostart Run-key (which needs the tray
/// exe path + arguments). GatewayApp populates these before <c>StartAsync</c>; they are optional so
/// the dev console host (which has no tray, no autostart) can leave them null and the settings
/// endpoint degrades to "unknown"/"unsupported" cleanly.
/// </summary>
public sealed class GatewaySettingsHooks
{
    /// <summary>Run mode token shown on the Settings page: "managed" (installed) or "dev".</summary>
    public Func<string>? Mode { get; init; }

    /// <summary>Current autostart state, or null when autostart is not supported on this host/OS.</summary>
    public Func<bool?>? AutostartEnabled { get; init; }

    /// <summary>Apply the autostart toggle; returns the new effective state. Null when unsupported.</summary>
    public Func<bool, bool>? SetAutostart { get; init; }
}
