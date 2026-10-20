using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi.Boxes;

/// <summary>
/// The relay leg's view of the Director's hosted-desktop list: resolve a box id to the local port its desktop
/// listens on, or refuse. NARROW ON PURPOSE, and a STAND-IN for the real list, which the hosted-desktop list task
/// (branch boxes/phase1-hosted-desktop-list) owns. The relay never keeps a list of its own; when that task merges,
/// this is wired to its one lookup and <see cref="HostsNoDesktops"/> is deleted.
/// </summary>
public interface IHostedDesktopLookup
{
    /// <summary>
    /// The port on 127.0.0.1 where this Director hosts <paramref name="boxId"/>'s desktop, or false when this
    /// Director does not host that box. A port is only ever this list's answer - never anything a caller supplied.
    /// </summary>
    bool TryResolvePort(string boxId, out int port);
}

/// <summary>
/// STAND-IN until the hosted-desktop list merges: a Director that hosts no desktop. Every box id is refused (and the
/// relay logs the refusal), which is the safe answer for a Director that has not been given a list.
/// </summary>
public sealed class HostsNoDesktops : IHostedDesktopLookup
{
    public static readonly HostsNoDesktops Instance = new();

    public bool TryResolvePort(string boxId, out int port)
    {
        FileLog.Write($"[HostsNoDesktops] TryResolvePort: box={boxId} -> this Director has no hosted-desktop list yet");
        port = 0;
        return false;
    }
}
