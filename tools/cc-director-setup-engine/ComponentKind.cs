namespace CcDirector.Setup.Engine;

/// <summary>The category of an installable component.</summary>
public enum ComponentKind
{
    /// <summary>The CC Director desktop app (per-user, self-updating).</summary>
    Director,

    /// <summary>The always-on Gateway service (Gateway-role machines only).</summary>
    Gateway,

    /// <summary>One of the cc-* command-line tools installed to bin/.</summary>
    Tool,

    /// <summary>
    /// The shared Python tools bundle - one download holding every cc-* tool, installed into the
    /// per-machine venv. It is the thing that is really downloaded, versioned and recorded for the
    /// tools; an individual <see cref="Tool"/> has had no asset of its own for some time.
    ///
    /// It is installed by PythonToolsInstaller, NOT by the generic UpdateRunner, so the planner never
    /// marks it actionable - see <see cref="PlanItemKind.InstalledByTheToolsStep"/>.
    /// </summary>
    ToolsBundle,

    /// <summary>
    /// The CC Launcher Windows tray app (issue #250): always-on launcher that provides
    /// clean process parentage and a token-gated loopback REST API. Ships to both roles.
    /// </summary>
    Launcher,
}
