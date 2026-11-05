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

    /// <summary>
    /// The runners the desktop reaches from its UI thread never start a process on the caller's
    /// thread. Reads the source, so a bare start added back is caught at commit time.
    ///
    /// EACH FILE MUST ANSWER ONE OF TWO WAYS, and the test says which it took rather than accepting
    /// silence from either. A file that builds its own start information must start it through the
    /// helper; a file that builds none must be delegating to <see cref="ProcessRunner"/>, which
    /// does. The second case arrived with devthrottle#4577: <c>GitCommandRunner</c> stopped driving
    /// a process of its own and now goes through ProcessRunner, which on macOS and Linux spawns
    /// with posix_spawn instead of fork(). Had this test simply dropped that file from the list, a
    /// bare start could have been written back into it unnoticed - so it stays on the list and is
    /// held to the other half of the rule.
    /// </summary>
    [Theory]
    [InlineData("src/CcDirector.Core/Utilities/ProcessRunner.cs")]
    [InlineData("src/CcDirector.Core/Git/GitCommandRunner.cs")]
    [InlineData("src/CcDirector.Core/Git/GitWriteService.cs")]
    public void Runner_NeverStartsAProcessOnTheCallersThread(string relativePath)
    {
        var path = Path.Combine(RepositorySourceIndex.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);

        // A bare synchronous start is refused in every one of them, whichever way they answer.
        Assert.DoesNotContain("proc.Start()", source, StringComparison.Ordinal);

        var buildsItsOwnStart = source.Contains("ProcessStartInfo", StringComparison.Ordinal);
        if (buildsItsOwnStart)
            Assert.Contains("ProcessStartOffThread.StartAsync(proc)", source, StringComparison.Ordinal);
        else
            Assert.Contains("ProcessRunner.RunAsync(", source, StringComparison.Ordinal);
    }
}
