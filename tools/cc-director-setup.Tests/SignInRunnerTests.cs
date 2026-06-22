using System.Net.Http;
using CcDirector.Core.Account;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the installer sign-in runner (issue #657). They drive the runner against a REAL
/// <see cref="LoopbackLoginListener"/> on a free loopback port (no backend), with the "browser" stood
/// in by a direct HTTP call to the loopback callback - the same hand-back the dev stand-in and the real
/// backend perform. This proves the cancel, timeout, success, and failure outcomes end to end.
/// </summary>
public sealed class SignInRunnerTests
{
    [Fact]
    public async Task RunAsync_BrowserHandsBackCredential_ReturnsSignedIn()
    {
        // Arrange: a real listener, and a fake "browser" that POSTs the token pair back to the
        // listener's loopback callback as soon as it is "opened".
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: url =>
            {
                var callback = listener.CallbackUrl.ToString();
                var handBack = $"{callback}?access_token=dev-access&refresh_token=dev-refresh";
                // Fire-and-forget the hand-back; the runner is concurrently awaiting it. The listener
                // queues the request, so the order (open then await) does not race.
                _ = Task.Run(() => http.GetAsync(handBack));
            },
            timeout: TimeSpan.FromSeconds(10));

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(SignInOutcome.SignedIn, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_UserCancels_ReturnsCancelled()
    {
        // Arrange: a real listener and a browser that never hands anything back, so the only way the
        // wait ends is the caller's cancellation.
        var listener = new LoopbackLoginListener();
        using var cts = new CancellationTokenSource();

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => cts.CancelAfter(TimeSpan.FromMilliseconds(100)),
            timeout: TimeSpan.FromSeconds(30));

        // Act
        var result = await runner.RunAsync(cts.Token);

        // Assert
        Assert.Equal(SignInOutcome.Cancelled, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_NoHandBackWithinTimeout_ReturnsTimedOut()
    {
        // Arrange: a real listener and a browser that never hands anything back, with a tiny timeout.
        var listener = new LoopbackLoginListener();

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => { /* abandoned sign-in: nothing comes back */ },
            timeout: TimeSpan.FromMilliseconds(200));

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(SignInOutcome.TimedOut, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_BrowserCannotOpen_ReturnsFailed()
    {
        // Arrange: opening the browser throws, mirroring "no default browser".
        var listener = new LoopbackLoginListener();

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => throw new InvalidOperationException("no browser"),
            timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(SignInOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void SignInResult_SignedIn_ReportsSucceeded()
    {
        // Arrange + Act
        var signedIn = new SignInResult(SignInOutcome.SignedIn, "Signed in to DevThrottle.");
        var cancelled = new SignInResult(SignInOutcome.Cancelled, "Cancelled.");

        // Assert
        Assert.True(signedIn.Succeeded);
        Assert.False(cancelled.Succeeded);
    }
}
