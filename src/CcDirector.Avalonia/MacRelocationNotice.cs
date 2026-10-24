using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Says "DevThrottle is in this folder, and here is where it belongs" once per place, not at every start (issue #4427).
/// A Mac copy that could not be moved into /Applications stays where it is, and a notice repeated at every start would
/// teach the person to dismiss it unread. The place last told is kept in one small file; a different place is told again.
/// </summary>
public static class MacRelocationNotice
{
    private const string FileName = "mac-app-location-notice.txt";

    /// <summary>True the first time <paramref name="bundlePath"/> is asked about, and records it so the next start is quiet.</summary>
    public static bool ShouldShow(string bundlePath) => ShouldShow(bundlePath, CcStorage.ToolConfig("director"));

    /// <summary>The same, with the folder the record is kept in named - for a test.</summary>
    public static bool ShouldShow(string bundlePath, string recordDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordDirectory);
        var record = Path.Combine(recordDirectory, FileName);
        if (File.Exists(record) && string.Equals(File.ReadAllText(record).Trim(), bundlePath, StringComparison.Ordinal))
        {
            FileLog.Write($"[MacRelocationNotice] already told about {bundlePath}; not showing it again");
            return false;
        }

        Directory.CreateDirectory(recordDirectory);
        File.WriteAllText(record, bundlePath);
        FileLog.Write($"[MacRelocationNotice] telling the person where the app is: {bundlePath}");
        return true;
    }
}
