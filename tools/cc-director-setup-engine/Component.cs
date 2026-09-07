using System.Runtime.InteropServices;

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
/// <param name="MacAsset">
/// The release-asset filename this component ships as, on macOS
/// (e.g. "cc-launcher-mac-arm64"), or null when the component has no macOS build.
/// Like <paramref name="WindowsAsset"/>, this is the key into the release manifest.
/// </param>
/// <param name="LinuxAsset">
/// The release-asset filename this component ships as, on Linux
/// (e.g. "cc-director-linux-x64"), or null when the component has no Linux build.
/// Like <paramref name="WindowsAsset"/>, this is the key into the release manifest.
/// </param>
public sealed record Component(
    string Id,
    ComponentKind Kind,
    string DisplayName,
    string WindowsAsset,
    IReadOnlySet<InstallRole> Roles,
    string? MacAsset = null,
    string? LinuxAsset = null)
{
    public bool InRole(InstallRole role) => Roles.Contains(role);

    /// <summary>
    /// The release-asset filename for the requested platform, or null when this component has no
    /// build for it. The platform is a parameter, not read from the environment, so planning stays
    /// pure and testable on any development machine.
    ///
    /// This used to take a <c>bool macOs</c>, which cannot carry three platforms: on Linux it was
    /// false, so a Linux machine was told to install <c>cc-director-win-x64.exe</c>. Note the shape
    /// of that failure - not a refusal, but a confident wrong answer that downloads and places a
    /// Windows executable and reports success. There is deliberately no "everything else" branch
    /// here for that reason: an unknown platform throws.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// <paramref name="platform"/> is not one DevThrottle ships for. Distinct from returning null,
    /// which means "this component has no build on a platform we do support".
    /// </exception>
    public string? AssetFor(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows) return WindowsAsset;
        if (platform == OSPlatform.OSX) return MacAsset;
        if (platform == OSPlatform.Linux) return LinuxAsset;
        throw new PlatformNotSupportedException(
            $"{Id} has no release asset for {platform}; DevThrottle ships for Windows, macOS and Linux.");
    }
}
