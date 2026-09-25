using CcRecorder.Account;
using Xunit;

namespace CcRecorder.Tests;

// AppForeground is process-wide state, so these tests must not run in parallel with each other.
[Collection("AppForeground")]
public class AppForegroundTests
{
    [Fact]
    public async Task WaitAsync_WhileOnScreen_CompletesAtOnce()
    {
        AppForeground.Set(true);

        await AppForeground.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitAsync_WhileInBackground_WaitsUntilTheAppComesBack()
    {
        AppForeground.Set(false);
        try
        {
            var waiting = AppForeground.WaitAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);

            AppForeground.Set(true);

            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            AppForeground.Set(true);
        }
    }

    [Fact]
    public async Task WaitAsync_LeftAndCameBackTwice_TheSecondWaitIsFresh()
    {
        AppForeground.Set(false);
        AppForeground.Set(true);
        AppForeground.Set(false);
        try
        {
            var waiting = AppForeground.WaitAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            AppForeground.Set(true);
        }
    }

    [Fact]
    public async Task WaitAsync_Cancelled_Throws()
    {
        AppForeground.Set(false);
        try
        {
            using var cts = new CancellationTokenSource();
            var waiting = AppForeground.WaitAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            AppForeground.Set(true);
        }
    }
}
