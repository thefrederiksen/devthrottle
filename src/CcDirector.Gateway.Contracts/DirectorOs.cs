namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The operating systems a Director reports in its Hello (the Systems tab of Your Throttle). One spelling per
/// system, written by the Director and read by the Gateway, so the two ends never disagree on a name.
/// </summary>
public static class DirectorOs
{
    public const string Windows = "windows";
    public const string Linux = "linux";
    public const string MacOs = "macos";

    /// <summary>The system this process is running on, or "" on one DevThrottle does not ship for.</summary>
    public static string Current()
    {
        if (OperatingSystem.IsWindows()) return Windows;
        if (OperatingSystem.IsMacOS()) return MacOs;
        if (OperatingSystem.IsLinux()) return Linux;
        return "";
    }

    /// <summary>A reported value in its one spelling, or null when it is empty or not a system this list names.
    /// A Hello is client-written, so anything else is "not known" rather than stored as written.</summary>
    public static string? Normalize(string? reported)
    {
        var value = reported?.Trim().ToLowerInvariant();
        return value is Windows or Linux or MacOs ? value : null;
    }

    /// <summary>The name a page shows for a system; "Unknown" for null.</summary>
    public static string DisplayName(string? os) => os switch
    {
        Windows => "Windows",
        Linux => "Linux",
        MacOs => "Mac",
        _ => "Unknown",
    };
}
