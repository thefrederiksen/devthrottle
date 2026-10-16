using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A test of behaviour only Linux and macOS have (an absolute path that starts "/"), which SKIPS - visibly,
/// with its reason - on Windows, rather than returning early and reporting a pass that asserted nothing.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(string because)
    {
        if (OperatingSystem.IsWindows())
            Skip = "Linux and macOS only: " + because;
    }
}
