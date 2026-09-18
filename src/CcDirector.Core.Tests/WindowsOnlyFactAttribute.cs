using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A test that describes behaviour only Windows has, and which therefore SKIPS - visibly, with a
/// stated reason - on every other platform.
///
/// WHY THIS EXISTS RATHER THAN AN EARLY RETURN. The idiom this replaces was
/// <c>if (!OperatingSystem.IsWindows()) return;</c> at the top of the test body. That reports a PASS
/// for a test that never ran a single assertion, which is a check that fails open: the suite's green
/// count silently includes tests that did nothing, and nobody can tell from the result which
/// platform's behaviour was actually proved. A skip with a reason is the honest answer - it appears in
/// the run as skipped, it says why, and the count of gated tests is visible instead of hidden inside
/// the pass count.
///
/// WHAT IT MUST NOT BE USED FOR. This is for behaviour that genuinely does not exist off Windows - a
/// registry-backed PATH, a command-interpreter shim, an executable-extension list. It is NOT a way to
/// quieten a test that fails because its inputs or its arrangement were written Windows-shaped; that
/// test should be fixed to run everywhere. The reason string has to say which it is, so the next
/// reader can tell a real platform boundary from a shortcut.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: " + because;
    }
}
