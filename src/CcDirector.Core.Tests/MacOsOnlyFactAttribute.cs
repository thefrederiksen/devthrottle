using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A test that reads the real macOS operating system, and which therefore SKIPS - visibly, with a
/// stated reason - on Windows and Linux.
///
/// The reasoning is <see cref="WindowsOnlyFactAttribute"/>'s, in the other direction: an early
/// <c>if (!OperatingSystem.IsMacOS()) return;</c> reports a PASS for a test that ran no assertion,
/// so the suite's green count would quietly include tests that did nothing on two platforms out of
/// three.
///
/// WHAT IT IS FOR HERE. The resource probe's system calls - Mach, IOKit, <c>getfsstat</c>,
/// <c>libproc</c> - exist only on this platform, so a test that calls them cannot run elsewhere. It
/// is NOT for the probe's PARSING, which is pure, has captured output committed beside it and is
/// covered on every platform by the unit tests. If a parsing test ever needs this attribute,
/// something that should have been pure has reached for the operating system instead.
/// </summary>
public sealed class MacOsOnlyFactAttribute : FactAttribute
{
    public MacOsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS-only: " + because;
    }
}
