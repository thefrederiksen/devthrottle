using System.Runtime.InteropServices;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The operating system this process is running on, as an <see cref="OSPlatform"/>.
///
/// This exists so that every "which platform am I on" question in the setup engine has ONE answer
/// written in ONE place. The engine used to ask it with a two-way branch - Windows, or else macOS -
/// in several files at once, and each of those quietly handed Linux somebody else's answer: the
/// macOS Python bundle, the Windows Director executable, the macOS Director application bundle, and
/// a macOS install path. Adding Linux to four separate ternaries would have set up the next
/// platform to be wrong in four places again.
///
/// Callers that RESOLVE something per-platform should take an <see cref="OSPlatform"/> parameter and
/// let their caller pass this in, rather than reading it themselves. That is what makes every branch
/// assertable from a Windows test run - and a branch nothing can assert is exactly how Linux stayed
/// wrong while the suite stayed green.
/// </summary>
public static class HostPlatform
{
    /// <summary>The platform this process is running on.</summary>
    /// <exception cref="PlatformNotSupportedException">
    /// DevThrottle is not built for this operating system. Deliberately a throw rather than a
    /// default of Windows: defaulting is the defect this type exists to remove.
    /// </exception>
    public static OSPlatform Current =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : throw new PlatformNotSupportedException(
            $"DevThrottle does not run on this operating system: {RuntimeInformation.OSDescription}.");

    /// <summary>
    /// True when <paramref name="platform"/> is one DevThrottle ships for. Callers that resolve a
    /// per-platform value use this to reject an unknown platform loudly instead of falling through
    /// to whichever branch happens to be last.
    /// </summary>
    public static bool IsSupported(OSPlatform platform) =>
        platform == OSPlatform.Windows || platform == OSPlatform.OSX || platform == OSPlatform.Linux;
}
