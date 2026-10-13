using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using Xunit;

namespace CcDirector.Core.Tests;

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
