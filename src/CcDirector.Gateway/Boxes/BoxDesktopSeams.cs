using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// The relay leg's view of which connected Director hosts a box. NARROW ON PURPOSE, and a STAND-IN: the
/// hosted-desktop list task (branch boxes/phase1-hosted-desktop-list) builds the pushed list and the one way to
/// locate a box's Director. When it merges, this is wired to that locate and the stand-in below is deleted.
/// </summary>
public interface IBoxDesktopHosts
{
    /// <summary>
    /// The id of the stream-connected Director that hosts <paramref name="boxId"/> in <paramref name="tenant"/>, or
    /// null when no Director of that tenant hosts it. A box hosted only in another tenant is null here - exactly
    /// like a box that does not exist - so another account's box is never located.
    /// </summary>
    string? LocateDirector(TenantId tenant, string boxId);
}

/// <summary>
/// The relay leg's view of the owner-tenant "Boxes" feature flag. A STAND-IN for the flag the hosted-desktop list
/// task adds; when it merges, this is wired to that flag and <see cref="BoxesFeatureOff"/> is deleted.
/// </summary>
public interface IBoxesFeature
{
    /// <summary>True when Boxes is turned on for <paramref name="tenant"/>.</summary>
    bool IsEnabled(TenantId tenant);
}

/// <summary>STAND-IN until the hosted-desktop list merges: no Director hosts any box.</summary>
public sealed class NoBoxDesktopHosts : IBoxDesktopHosts
{
    public static readonly NoBoxDesktopHosts Instance = new();

    public string? LocateDirector(TenantId tenant, string boxId)
    {
        FileLog.Write($"[NoBoxDesktopHosts] LocateDirector: box={boxId} tenant={tenant.ToLogString()} -> the hosted-desktop list is not wired yet");
        return null;
    }
}

/// <summary>STAND-IN until the Boxes flag merges: Boxes is off for every tenant, so the desktop routes answer 404.</summary>
public sealed class BoxesFeatureOff : IBoxesFeature
{
    public static readonly BoxesFeatureOff Instance = new();

    public bool IsEnabled(TenantId tenant) => false;
}
