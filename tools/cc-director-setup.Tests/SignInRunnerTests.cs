using System.Net.Http;
using CcDirector.Core.Account;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the installer sign-in runner (issues #657 and #658). They drive the runner against a REAL
/// <see cref="LoopbackLoginListener"/> on a free loopback port (no backend), with the "browser" stood
/// in by a direct HTTP call to the loopback callback - the same hand-back the dev stand-in and the real
/// backend perform. This proves the cancel, timeout, success, and failure outcomes end to end, and
/// (issue #658) that a captured credential is persisted to the Director credential store on success and
/// nothing is written on any incomplete sign-in.
///
/// The success-path tests inject a recording persistence seam so they never touch the real per-user
/// credential blob, except the one test that deliberately exercises the full default persistence path
/// (the real <see cref="WindowsProtectedTokenStore"/>) at an explicit temporary path it cleans up.
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
            persistCredential: _ => { /* no-op: this test asserts only the outcome */ },
            timeout: TimeSpan.FromSeconds(10));

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(SignInOutcome.SignedIn, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_BrowserHandsBackCredential_PersistsTheCapturedTokenPair()
    {
        // Arrange: a real listener and a fake "browser" that hands back a known token pair. The
        // persistence seam records what it was asked to store (issue #658: the captured credential
        // must be handed to the credential store).
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();
        DevThrottleTokens? persisted = null;

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: url =>
            {
                var callback = listener.CallbackUrl.ToString();
                var handBack = $"{callback}?access_token=captured-access&refresh_token=captured-refresh";
                _ = Task.Run(() => http.GetAsync(handBack));
            },
            persistCredential: tokens => persisted = tokens,
            timeout: TimeSpan.FromSeconds(10));

        // Act
        var result = await runner.RunAsync();

        // Assert: the exact captured pair was handed to the store.
        Assert.True(result.Succeeded);
        Assert.NotNull(persisted);
        Assert.Equal("captured-access", persisted.AccessToken);
        Assert.Equal("captured-refresh", persisted.RefreshToken);
    }

    [Fact]
    public async Task RunAsync_UserCancels_ReturnsCancelled()
    {
        // Arrange: a real listener and a browser that never hands anything back, so the only way the
        // wait ends is the caller's cancellation.
        var listener = new LoopbackLoginListener();
        using var cts = new CancellationTokenSource();
        var persistCalled = false;

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => cts.CancelAfter(TimeSpan.FromMilliseconds(100)),
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromSeconds(30));

        // Act
        var result = await runner.RunAsync(cts.Token);

        // Assert: cancelled, and nothing was written (issue #658: no credential on an incomplete sign-in).
        Assert.Equal(SignInOutcome.Cancelled, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_NoHandBackWithinTimeout_ReturnsTimedOut()
    {
        // Arrange: a real listener and a browser that never hands anything back, with a tiny timeout.
        var listener = new LoopbackLoginListener();
        var persistCalled = false;

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => { /* abandoned sign-in: nothing comes back */ },
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromMilliseconds(200));

        // Act
        var result = await runner.RunAsync();

        // Assert: timed out, and nothing was written.
        Assert.Equal(SignInOutcome.TimedOut, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_BrowserCannotOpen_ReturnsFailed()
    {
        // Arrange: opening the browser throws, mirroring "no default browser".
        var listener = new LoopbackLoginListener();
        var persistCalled = false;

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: _ => throw new InvalidOperationException("no browser"),
            persistCredential: _ => persistCalled = true,
            timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await runner.RunAsync();

        // Assert: failed, and nothing was written.
        Assert.Equal(SignInOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(persistCalled);
    }

    [Fact]
    public async Task RunAsync_PersistenceFails_ReturnsFailed()
    {
        // Arrange: the credential is captured but the store throws. With no fallback, the runner must
        // report failure rather than claim "open already signed in" it cannot deliver (issue #658).
        var listener = new LoopbackLoginListener();
        using var http = new HttpClient();

        var runner = new SignInRunner(
            listenerFactory: () => listener,
            openBrowser: url =>
            {
                var callback = listener.CallbackUrl.ToString();
                var handBack = $"{callback}?access_token=dev-access&refresh_token=dev-refresh";
                _ = Task.Run(() => http.GetAsync(handBack));
            },
            persistCredential: _ => throw new InvalidOperationException("store unavailable"),
            timeout: TimeSpan.FromSeconds(10));

        // Act
        var result = await runner.RunAsync();

        // Assert
        Assert.Equal(SignInOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_DefaultPersistencePath_WritesDecryptableBlob()
    {
        // Arrange: drive the FULL default persistence path (the real WindowsProtectedTokenStore +
        // DevThrottleAccountService) at an explicit temporary credential path, then read it back the
        // way the Director's account gate does. This proves the captured pair is written encrypted and
        // decrypts to exactly what was captured (issue #658, acceptance criterion 1).
        var blobPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"devthrottle-cred-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new WindowsProtectedTokenStore(blobPath);
            var service = DevThrottleAccountFactory.Build(store);

            var listener = new LoopbackLoginListener();
            using var http = new HttpClient();

            var runner = new SignInRunner(
                listenerFactory: () => listener,
                openBrowser: url =>
                {
                    var callback = listener.CallbackUrl.ToString();
                    var handBack = $"{callback}?access_token=real-access&refresh_token=real-refresh";
                    _ = Task.Run(() => http.GetAsync(handBack));
                },
                persistCredential: service.StoreTokens,
                timeout: TimeSpan.FromSeconds(10));

            // Act
            var result = await runner.RunAsync();

            // Assert: the encrypted blob exists and decrypts to the captured pair.
            Assert.True(result.Succeeded);
            Assert.True(System.IO.File.Exists(blobPath));
            var roundTripped = store.Load();
            Assert.NotNull(roundTripped);
            Assert.Equal("real-access", roundTripped.AccessToken);
            Assert.Equal("real-refresh", roundTripped.RefreshToken);
        }
        finally
        {
            if (System.IO.File.Exists(blobPath))
                System.IO.File.Delete(blobPath);
        }
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
