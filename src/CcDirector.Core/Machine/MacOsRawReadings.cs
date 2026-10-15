namespace CcDirector.Core.Machine;

/// <summary>
/// WHAT THE OPERATING SYSTEM HANDED BACK, BEFORE ANYTHING WAS MADE OF IT.
///
/// These captures exist so that the system calls and the interpretation of their output can be
/// separated. <see cref="MacOsPlatformReader"/> fills them; <see cref="MacOsResourceParsing"/> reads
/// them and returns figures; the tests fill them from files captured on a real Mac and committed
/// beside the tests, which is what lets the arithmetic be covered on a Windows or Linux build
/// machine.
///
/// A capture is deliberately CLOSE TO THE METAL - property list text, raw counters, nanoseconds -
/// and holds no verdict and no percentage. The moment a capture starts carrying a derived figure,
/// that figure stops being covered by a test.
/// </summary>
internal static class MacOsRawReadings
{
    /// <summary>
    /// One physical disk, as three pieces of IOKit registry. Each is the operating system's own
    /// property list text, or null when that property was not present on this machine - which is a
    /// reading the caller must handle, never a figure to invent.
    /// </summary>
    internal sealed record DiskCapture(
        string? BsdNamePropertyList,
        string? DeviceCharacteristicsPropertyList,
        string? StatisticsPropertyList);
}
