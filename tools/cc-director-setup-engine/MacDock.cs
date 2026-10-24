using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Keeps an application in the macOS Dock - the "Keep in Dock" offer on Setup's last screen (issue #4427).
///
/// The Dock's own list lives in the <c>com.apple.dock</c> preferences under <c>persistent-apps</c>. Adding an entry
/// there and restarting the Dock is what "Options > Keep in Dock" does, and it is the documented way to do it from a
/// program. An app already in the Dock is not added a second time.
/// </summary>
public static class MacDock
{
    /// <summary>
    /// The argument line for <c>/usr/bin/defaults</c> that adds <paramref name="bundlePath"/> to the Dock. The entry is
    /// one quoted word: a file address (type 15) has its spaces escaped, and the entry holds no quote of its own.
    /// </summary>
    public static string AddArguments(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        var url = new Uri(bundlePath.TrimEnd('/') + "/").AbsoluteUri;
        var entry = "<dict><key>tile-data</key><dict><key>file-data</key><dict>"
                    + $"<key>_CFURLString</key><string>{System.Security.SecurityElement.Escape(url)}</string>"
                    + "<key>_CFURLStringType</key><integer>15</integer>"
                    + "</dict></dict></dict>";
        return $"write com.apple.dock persistent-apps -array-add \"{entry}\"";
    }

    /// <summary>
    /// Whether the Dock's list (the output of <c>defaults read com.apple.dock persistent-apps</c>) already holds
    /// <paramref name="bundlePath"/>. The Dock stores the bundle as a file address, so the address is what is looked for.
    /// </summary>
    public static bool IsInDock(string persistentApps, string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(persistentApps);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        var url = new Uri(bundlePath.TrimEnd('/') + "/").AbsoluteUri;
        return persistentApps.Contains(url, StringComparison.Ordinal)
               || persistentApps.Contains(url.TrimEnd('/') + "\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Put <paramref name="bundlePath"/> in the Dock unless it is there already, then restart the Dock so it shows.
    /// Returns a sentence for the person saying what happened.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static (bool Ok, string Message) KeepInDock(string bundlePath, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var (readExit, current) = ProcessRunner.Run("/usr/bin/defaults", "read com.apple.dock persistent-apps");
        if (readExit == 0 && IsInDock(current, bundlePath))
        {
            log($"[MacDock] {bundlePath} is already in the Dock");
            return (true, "DevThrottle is already in the Dock.");
        }

        var args = AddArguments(bundlePath);
        var (exit, output) = ProcessRunner.Run("/usr/bin/defaults", args);
        if (exit != 0)
        {
            log($"[MacDock] adding {bundlePath} FAILED (exit {exit}): {output}");
            return (false, $"DevThrottle could not be added to the Dock (defaults answered exit {exit}). Drag it from the Applications folder to the Dock instead.");
        }

        ProcessRunner.Run("/usr/bin/killall", "Dock");
        log($"[MacDock] added {bundlePath} to the Dock and restarted the Dock");
        return (true, "DevThrottle is in the Dock.");
    }
}
