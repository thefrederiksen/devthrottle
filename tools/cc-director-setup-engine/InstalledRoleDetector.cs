namespace CcDirector.Setup.Engine;

/// <summary>
/// Detects the role a machine was installed as, from what is actually present on disk: a machine
/// that has the always-on Gateway tray app is a Gateway host, otherwise a Workstation.
///
/// The update path needs this because role is a first-install CHOICE that the update wizard does not
/// re-ask. Without it an update silently narrows a Gateway host to a Workstation refresh - it updates
/// only the Director and never touches the Gateway/Cockpit, leaving them version-drifted and (worse)
/// never re-asserting the managed tray launch + autostart. Detecting the installed role keeps a
/// Gateway host a Gateway host across updates.
/// </summary>
public static class InstalledRoleDetector
{
    /// <summary>
    /// Gateway when the Gateway component is present on disk under <paramref name="layout"/>, else
    /// Workstation. <paramref name="reader"/> is injectable for tests; the default reads the real layout.
    /// </summary>
    public static InstallRole Detect(InstallLayout layout, InstalledStateReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        reader ??= new InstalledStateReader(layout);
        return reader.Read(ComponentRegistry.Gateway).Present
            ? InstallRole.Gateway
            : InstallRole.Workstation;
    }
}
