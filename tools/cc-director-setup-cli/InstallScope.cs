using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// Pure decisions about what a single install pass includes. Factored out of <see cref="Commands"/>
/// so the role -> scope rules can be unit-tested without touching the network or the filesystem.
/// </summary>
public static class InstallScope
{
    /// <summary>
    /// True when the install is narrowed to a single component (<c>--component &lt;id&gt;</c>) rather than a
    /// full install. <c>--component all</c> (or no option) is NOT narrowing. This is the single source of
    /// truth both the plan narrowing and the Python-tools gate read, so "is this a scoped sub-install?" is
    /// decided in exactly one place.
    /// </summary>
    public static bool IsComponentScoped(string? componentOption) =>
        !string.IsNullOrWhiteSpace(componentOption)
        && !componentOption.Equals("all", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this pass finishes the install on the MACHINE: the PATH entry, the Start Menu shortcut, the
    /// macOS login PATH and the Linux app-menu entry. Those live outside the install root, so a run with
    /// <c>--root &lt;dir&gt;</c> (a test install into a folder of its own) never makes them. Before this, a
    /// test install on a working machine repointed the machine's own DevThrottle shortcut at the test copy
    /// (issue #4567).
    /// </summary>
    public static bool FinalizesTheMachine(bool perUserTouched, string? rootOption) =>
        perUserTouched && rootOption is null;

    /// <summary>
    /// Whether this pass installs the per-user Python tools bundle (the shared venv that carries
    /// every cc-* tool). True for a FULL install of EITHER role on either platform: the Gateway is a
    /// per-user tray app (no elevation), so a Gateway install is a true SUPERSET of a Workstation
    /// install and must include the tools too (INSTALLATION.md section 1), and the release ships
    /// macOS bundles (cc-python-macos-arm64 / cc-tools-pyenv-macos-arm64) that the platform-aware
    /// PythonToolsInstaller consumes - the old Windows-only gate silently skipped the tools on a
    /// macOS install (issue #1445). It is deliberately role-INDEPENDENT; <paramref name="role"/>
    /// is kept in the signature to document and lock that fact.
    ///
    /// FALSE for a COMPONENT-SCOPED install (<paramref name="componentScoped"/>): the tools bundle is not
    /// a narrowable component, so <c>--component &lt;id&gt;</c> is asking for that one component only. This
    /// is what the GUI's Gateway sub-install (<c>install --role gateway --component gateway</c>) relies on -
    /// without it the GUI would still pay the multi-minute bundle install through the CLI subprocess, and
    /// the app-provisions-on-first-launch slice would be defeated for every Gateway install.
    /// </summary>
    public static bool InstallsPythonTools(InstallRole role, bool installMode, bool dryRun, bool componentScoped)
    {
        _ = role; // role-independent by design (see summary); both roles get the tools bundle.
        return installMode && !dryRun && !componentScoped;
    }
}
