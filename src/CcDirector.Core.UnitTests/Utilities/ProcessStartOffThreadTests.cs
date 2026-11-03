using CcDirector.Core.Utilities;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// A child process is started on the thread pool, never on the caller's thread (issue #4556). The
/// desktop's UI thread calls async git reads, and each one ran synchronously up to its first await -
/// which sat after Process.Start - so the UI thread itself started git: a mean of 2.05 s per session
/// switch on the owner's machine.
/// </summary>
public sealed class ProcessStartOffThreadTests
{
    [Fact]
    public async Task StartAsync_RunsTheStartOnAThreadPoolThread_NotTheCallersThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        int startThread = -1;
        bool onPool = false;

        var started = await ProcessStartOffThread.StartAsync(() =>
        {
            startThread = Environment.CurrentManagedThreadId;
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            return true;
        });

        Assert.True(started);
        Assert.True(onPool, "the start must run on a thread-pool thread");
        Assert.NotEqual(callerThread, startThread);
    }

    [Fact]
    public async Task StartAsync_AnExceptionFromTheStart_ReachesTheAwaitingCaller()
    {
        // A missing executable throws Win32Exception from Process.Start; every runner turns that into a
        // failed result, so it must still arrive at the await rather than vanish on the pool.
        var ex = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            ProcessStartOffThread.StartAsync(() => throw new System.ComponentModel.Win32Exception(2)));
        Assert.Equal(2, ex.NativeErrorCode);
    }

    /// <summary>The runners the desktop reaches from its UI thread start their process through the
    /// helper. Reads the source, so a bare start added back is caught at commit time.</summary>
    [Theory]
    [InlineData("src/CcDirector.Core/Utilities/ProcessRunner.cs")]
    [InlineData("src/CcDirector.Core/Git/GitCommandRunner.cs")]
    [InlineData("src/CcDirector.Core/Git/GitWriteService.cs")]
    public void Runner_StartsItsProcessThroughTheHelper_NeverABareStart(string relativePath)
    {
        var path = Path.Combine(RepositorySourceIndex.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);
        Assert.Contains("ProcessStartOffThread.StartAsync(proc)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("proc.Start()", source, StringComparison.Ordinal);
    }
}
