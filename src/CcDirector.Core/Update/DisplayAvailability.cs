using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>Whether this machine can start a windowed application right now.</summary>
public enum DisplayReadiness
{
    /// <summary>
    /// Nothing here can tell. Every platform except macOS answers this way, and so does macOS when the
    /// window server cannot be asked. It means "carry on", never "wait" - see <see cref="DisplayAvailability"/>
    /// for why only a measured negative is allowed to stop anything.
    /// </summary>
    Unknown,

    /// <summary>At least one display is attached and drawable. A windowed application can start.</summary>
    Ready,

    /// <summary>A display is attached but none is drawable - it is asleep. A windowed application will die on start.</summary>
    Asleep,

    /// <summary>No display is attached at all. Waking cannot help; there is nothing to wake.</summary>
    None,
}

/// <summary>
/// Can a windowed application start on this machine at this instant, and if not, can that be fixed?
///
/// THIS EXISTS BECAUSE OF ONE NIGHT. On 2026-09-03 at 1:57 in the morning the launcher installed
/// version 2.0.5 on the owner's Mac, started it, and watched it die. It condemned the build, restored
/// the previous one, and pinned 2.0.5 so it could never be offered again. The machine sat five days on
/// a superseded build. Version 2.0.5 was perfectly good. What actually happened is this:
///
///   Avalonia's macOS render loop is a CoreVideo display link, created with
///   CVDisplayLinkCreateWithActiveCGDisplays. With no ACTIVE display that call returns -6661,
///   kCVReturnInvalidArgument, and Avalonia has no fallback, so the failure is fatal before the
///   application reaches its first window.
///
/// The condition is a SLEEPING DISPLAY, not a locked screen. We had previously written the lock down as
/// the cause and mitigated it by never letting the machine lock, which does not help at all: Avalonia
/// never asks whether the session is unlocked, it asks CoreGraphics for a drawable display. The same
/// setup script then set the display to sleep after ten minutes and called that harmless. It is not
/// harmless. It is sufficient, on its own, to make every unattended restart fail.
///
/// So this asks the machine the question the render loop is about to ask, using the same family of API
/// the render loop itself depends on, and offers the one remedy macOS provides for it.
///
/// WHY ONLY A MEASURED NEGATIVE STOPS ANYTHING. An update that is held because a check could not run is
/// an update that never happens, and a machine that silently stops updating is the failure this whole
/// area already suffered from once. So <see cref="DisplayReadiness.Unknown"/> - every non-Mac platform,
/// and a Mac whose window server will not answer - means carry on exactly as before. Only
/// <see cref="DisplayReadiness.Asleep"/> and <see cref="DisplayReadiness.None"/>, which are positive
/// measurements of a condition that WILL kill the new build, are allowed to hold an update back.
/// </summary>
public static class DisplayAvailability
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // Passing a null list with a maximum of zero is the documented way to ask only for the count.
    // "Active" means attached, awake and drawable; "online" includes displays that are merely asleep,
    // which is exactly the difference between "wake it" and "there is nothing to wake".
    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    private static extern int CGGetOnlineDisplayList(uint maxDisplays, uint[]? onlineDisplays, out uint displayCount);

    /// <summary>How long the display is asked to stay awake once woken. Comfortably longer than a swap,
    /// a start and the health wait that follows it, so the display cannot go dark mid-update.</summary>
    public static readonly TimeSpan WakeDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ask the window server what is drawable right now. Never throws: an instrument that cannot be read
    /// reports <see cref="DisplayReadiness.Unknown"/>, which changes no behaviour.
    /// </summary>
    public static DisplayReadiness Check()
    {
        if (!OperatingSystem.IsMacOS())
            return DisplayReadiness.Unknown;

        try
        {
            if (CGGetActiveDisplayList(0, null, out var active) != 0)
                return DisplayReadiness.Unknown;
            if (active > 0)
                return DisplayReadiness.Ready;

            if (CGGetOnlineDisplayList(0, null, out var online) != 0)
                return DisplayReadiness.Unknown;

            return online > 0 ? DisplayReadiness.Asleep : DisplayReadiness.None;
        }
        catch (Exception ex)
        {
            // The boundary of a native call, and the only place a caught exception is right: this is an
            // instrument reading, and a broken instrument must report that it does not know rather than
            // take an update loop down with it.
            FileLog.Write($"[DisplayAvailability] Check FAILED (reporting Unknown): {ex.Message}");
            return DisplayReadiness.Unknown;
        }
    }

    /// <summary>
    /// Turn the display on and hold it on for <see cref="WakeDuration"/>, then confirm it actually came
    /// up. Returns the readiness AFTER the attempt, so a caller never has to believe this worked.
    ///
    /// The remedy is macOS's own: caffeinate's user-activity assertion is documented to turn the display
    /// on when it is off and to postpone display sleep. It is the same thing that happens when somebody
    /// touches the keyboard, which is precisely the event this machine was missing at 1:57 in the
    /// morning.
    /// </summary>
    public static DisplayReadiness Wake()
    {
        var before = Check();
        if (before != DisplayReadiness.Asleep)
        {
            // Ready needs nothing, None cannot be helped, and Unknown must not be acted on.
            FileLog.Write($"[DisplayAvailability] Wake: nothing to do, readiness={before}");
            return before;
        }

        FileLog.Write($"[DisplayAvailability] Wake: display asleep; declaring user activity for {WakeDuration.TotalSeconds:F0}s");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/caffeinate",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(((int)WakeDuration.TotalSeconds).ToString());

            // Deliberately NOT waited on. caffeinate holds the assertion for as long as it runs, so
            // waiting for it would wait out the whole window it exists to open.
            using var process = Process.Start(psi);
            if (process is null)
            {
                FileLog.Write("[DisplayAvailability] Wake FAILED: caffeinate did not start");
                return before;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DisplayAvailability] Wake FAILED: {ex.Message}");
            return before;
        }

        // Waking is not instant, and the answer that matters is the one after it has happened. Poll for a
        // few seconds rather than sleeping a fixed time and hoping.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = Check();
            if (now == DisplayReadiness.Ready)
            {
                FileLog.Write("[DisplayAvailability] Wake: display is awake and drawable");
                return now;
            }

            Thread.Sleep(500);
        }

        var after = Check();
        FileLog.Write($"[DisplayAvailability] Wake: display did not come up within 10s, readiness={after}");
        return after;
    }

    /// <summary>
    /// One plain sentence about why a windowed application cannot start, for the person reading a status
    /// panel. Null when nothing is wrong, so a caller cannot accidentally report a problem that is not
    /// there.
    /// </summary>
    public static string? DescribeObstacle(DisplayReadiness readiness) => readiness switch
    {
        DisplayReadiness.Asleep =>
            "this machine's display is asleep, and a Director cannot start without one - macOS gives a "
            + "starting application no render loop when no display is drawable",
        DisplayReadiness.None =>
            "no display is attached to this machine, and a Director cannot start without one",
        _ => null,
    };
}
