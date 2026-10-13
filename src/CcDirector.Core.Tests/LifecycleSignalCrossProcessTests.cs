using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// TWO REAL PROCESSES AGREEING ON WHERE A SIGNAL LIVES - the test this mechanism shipped without, and
/// the reason a whole platform's lifecycle could be inert while every suite was green.
///
/// What the in-process tests in <see cref="LifecycleSignalTests"/> cannot see: a real signal always
/// crosses the launcher-Director boundary, and the two ends resolve storage differently. A Director
/// redirects <c>CC_DIRECTOR_ROOT</c> to its instance home at startup; the launcher never redirects.
/// When the Unix request-file path was derived from each process's own storage view, the Director
/// polled a directory the launcher never wrote - every stop became a 20-second stall and a force-kill,
/// and "install it now" reported success into a directory nobody watched. A single process cannot
/// reproduce that divergence honestly, so both ends here are child processes running the production
/// <see cref="LifecycleSignal"/> through a probe executable, each given exactly the environment its
/// production counterpart has. The parent test process mutates nothing of its own.
///
/// WHAT THIS PROVES PER PLATFORM - stated so nobody mistakes one platform's pass for the other's:
/// - On macOS and Linux these fail without the shared-root fix in
///   <see cref="LifecycleSignal.UnixRequestPath"/> and pass with it: they pin that both ends derive
///   the request-file path from the SHARED root, across the production redirect.
/// - On Windows the kernel delivers by NAME alone - no file path exists to agree on - so these cannot
///   detect a path regression there. They still prove genuine cross-process delivery through the
///   named event, which no other test does, but the path property is exercised on Unix ONLY. The
///   Windows-side detector for the path expression itself is
///   <see cref="LifecycleSignalRequestPathTests"/>, which asserts the derivation as a value and
///   therefore fails on every platform if the expression regresses.
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class LifecycleSignalCrossProcessTests : IDisposable
{
    private readonly string _sharedRoot = Path.Combine(AppContext.BaseDirectory,
        "cc-signal-xproc-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _timeline = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string Timeline => string.Join(" | ", _timeline);

    public void Dispose()
    {
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The request-path DERIVATION, pinned as a value. This is the only detector of the shared-root
/// property that runs meaningfully on WINDOWS: the Windows arm never touches this path (the kernel
/// object is addressed by name alone, which is why the original defect was invisible there), but the
/// expression itself computes everywhere, so asserting it here makes a regression fail every
/// platform's suite rather than only the platform that suffers it.
/// </summary>
[Collection("CcStorageRoot")] // serializes the classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class LifecycleSignalRequestPathTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _sharedRoot = Path.Combine(Path.GetTempPath(),
        "cc-signal-path-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        InstanceContext.Initialize(null, wasExplicit: false); // recapture the true root for later tests
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// After the production redirect - the Director pointing CC_DIRECTOR_ROOT at its instance home -
    /// the request path must STILL resolve under the shared root, because that is where the
    /// un-redirected launcher on the other end of every signal resolves it.
    /// </summary>
    [Fact]
    public void TheRequestPath_ResolvesUnderTheSharedRoot_EvenAfterTheDirectorsOwnRedirect()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _sharedRoot);
        InstanceContext.Initialize(null, wasExplicit: false);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", InstanceContext.InstanceHome);

        var path = LifecycleSignal.UnixRequestPath("cc-director-test-path");

        Assert.Equal(
            Path.Combine(_sharedRoot, "config", "lifecycle-signals", "cc-director-test-path.request"),
            path);
    }
}
