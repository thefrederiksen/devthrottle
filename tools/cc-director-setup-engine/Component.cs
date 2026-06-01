namespace CcDirector.Setup.Engine;

/// <summary>
/// A single installable component. Immutable description used by the registry,
/// the install layout, and the update planner.
/// </summary>
/// <param name="Id">Canonical id (e.g. "director", "gateway", "cc-pdf").</param>
/// <param name="Kind">Category.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="WindowsAsset">
/// The release-asset filename this component ships as, on Windows
/// (e.g. "cc-director-win-x64.exe"). This is the key into the release manifest.
/// </param>
/// <param name="Roles">Which install roles include this component.</param>
public sealed record Component(
    string Id,
    ComponentKind Kind,
    string DisplayName,
    string WindowsAsset,
    IReadOnlySet<InstallRole> Roles)
{
    public bool InRole(InstallRole role) => Roles.Contains(role);
}
