namespace CcDirector.Setup.Engine;

/// <summary>The on-disk state of a component: present or not, and at what version.</summary>
/// <param name="ComponentId">The component this describes.</param>
/// <param name="Present">True if the component's file exists on disk.</param>
/// <param name="Version">The installed version, or null if absent / unreadable. This is the
/// recorded version (from installed.json) when one exists, otherwise the on-disk file stamp.</param>
/// <param name="Path">The resolved on-disk path that was inspected.</param>
/// <param name="FileVersion">The actual on-disk file-version stamp of the component's exe (null if
/// absent / unreadable / not stamped). Kept distinct from <paramref name="Version"/> so the update
/// planner can cross-check a recorded version against the file the exe really is - and prefer this
/// real stamp when the recorded version is anomalous (issue #176 test-pollution guard).</param>
public sealed record InstalledComponent(
    string ComponentId, bool Present, string? Version, string Path, string? FileVersion = null);
