using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A test that describes behaviour only macOS and Linux have, and which therefore SKIPS - visibly,
/// with a stated reason - on Windows. The counterpart of <see cref="WindowsOnlyFactAttribute"/>,
/// and it exists for the same reason: <c>if (OperatingSystem.IsWindows()) return;</c> reports a PASS
/// for a test that never ran an assertion, which is a check that fails open.
///
/// THIS MATTERS PARTICULARLY HERE. Continuous integration runs the .NET suites on windows-latest
/// only, so a test wearing this attribute is SKIPPED on every automated run - it is proof only when
/// somebody runs it on a Mac or a Linux box and says so. A skip says that plainly in the run
/// output; an early return would have hidden it inside the pass count.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(string because)
    {
        if (OperatingSystem.IsWindows())
            Skip = "macOS and Linux only: " + because;
    }
}
