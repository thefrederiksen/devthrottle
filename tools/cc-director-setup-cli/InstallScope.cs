using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// Pure decisions about what a single install pass includes. Factored out of <see cref="Commands"/>
/// so the role -> scope rules can be unit-tested without touching the network or the filesystem.
/// </summary>
public static class InstallScope
{
    /// <summary>
    /// Whether this pass installs the per-user Python tools bundle (the shared venv that carries
    /// every cc-* tool). True for an install of EITHER role on Windows: the Gateway is a per-user
    /// tray app (no elevation), so a Gateway install is a true SUPERSET of a Workstation install
    /// and must include the tools too (INSTALLATION.md section 1). It is deliberately role-INDEPENDENT;
    /// <paramref name="role"/> is kept in the signature to document and lock that fact (the old
    /// workstation-only gate dated from when the Gateway was an elevated Windows service).
    /// </summary>
    public static bool InstallsPythonTools(InstallRole role, bool installMode, bool dryRun, bool isWindows)
    {
        _ = role; // role-independent by design (see summary); both roles get the tools bundle.
        return installMode && !dryRun && isWindows;
    }
}
