namespace CcDirector.Setup.Engine;

/// <summary>The category of an installable component.</summary>
public enum ComponentKind
{
    /// <summary>The CC Director desktop app (per-user, self-updating).</summary>
    Director,

    /// <summary>The always-on Gateway service (Gateway-role machines only).</summary>
    Gateway,

    /// <summary>The Cockpit web app, supervised by the Gateway service.</summary>
    Cockpit,

    /// <summary>One of the cc-* command-line tools installed to bin/.</summary>
    Tool,

    /// <summary>
    /// The CC Launcher Windows tray app (issue #250): always-on launcher that provides
    /// clean process parentage and a token-gated loopback REST API. Ships to both roles.
    /// </summary>
    Launcher,
}
