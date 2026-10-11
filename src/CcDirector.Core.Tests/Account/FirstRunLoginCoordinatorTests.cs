using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the first-run login orchestration (issue #581): the system browser is opened at the sign-in
/// address, the credential the sign-in completion hands back is captured, and it is stored through the
/// credential service (issue #583). The browser launch and the loopback hand-back are injected so the
/// flow is provable without a real browser or a real backend.
/// </summary>
[Collection("ProcessWideState")]
public sealed class FirstRunLoginCoordinatorTests
{
    private static DevThrottleAccountService MakeAccount(InMemoryTokenStore store) =>
        new(store, new JwtAccessTokenValidator(TestJwt.SigningSecret), new StubTokenRefresher(null));

    // Acceptance criterion: the Log in action opens the system browser at the expected sign-in address.
    [Fact]
    public void BuildSignInUrl_CarriesTheLoopbackCallbackAsRedirect()
    {
        var callback = new Uri("http://127.0.0.1:54321/devthrottle-login-callback/");

        var url = FirstRunLoginCoordinator.BuildSignInUrl(callback);

        Assert.StartsWith(FirstRunLoginCoordinator.DefaultSignInBaseUrl, url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(callback.ToString()), url);
    }

    // Issue #856: the plain sign-in URL (no loopback callback, no secret) defaults to the documented
    // DevThrottle sign-in page when the env seam is unset. This is what the Add-a-device QR encodes.
    [Fact]
    public void ResolveSignInBaseUrl_WhenEnvUnset_ReturnsTheDefaultSignInPage()
    {
        var prior = Environment.GetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar, null);

            var url = FirstRunLoginCoordinator.ResolveSignInBaseUrl();

            Assert.Equal(FirstRunLoginCoordinator.DefaultSignInBaseUrl, url);
            // The plain URL must NOT carry a loopback callback - that is only for the on-machine flow.
            Assert.DoesNotContain("redirect_uri", url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar, prior);
        }
    }

    // Issue #856: the env seam overrides the default so a non-production sign-in page can be pointed at.
    [Fact]
    public void ResolveSignInBaseUrl_WhenEnvSet_ReturnsTheConfiguredUrl()
    {
        var prior = Environment.GetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar, "https://staging.devthrottle.com/signin");

            var url = FirstRunLoginCoordinator.ResolveSignInBaseUrl();

            Assert.Equal("https://staging.devthrottle.com/signin", url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FirstRunLoginCoordinator.SignInBaseUrlEnvVar, prior);
        }
    }

    // Acceptance criterion: the browser is opened at the sign-in URL (here captured by the injected opener).
    [Fact]
    public async Task RunAsync_OpensBrowserAtSignInUrlThenCapturesAndStoresCredential()
    {
        var store = new InMemoryTokenStore();
        var account = MakeAccount(store);
        string? openedUrl = null;
        var capturedTokens = new DevThrottleTokens(TestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1");

        // Drive a real loopback listener and a stand-in completion that posts the token to it.
        using var listener = new LoopbackLoginListener();
        var coordinator = new FirstRunLoginCoordinator(
            account,
            openBrowser: url => { openedUrl = url; return Task.CompletedTask; },
            listenerFactory: () => listener);

        var run = coordinator.RunAsync();
        await PostStandInCompletionAsync(listener.CallbackUrl, capturedTokens);
        var result = await run;

        Assert.True(result.Succeeded);
        Assert.NotNull(openedUrl);
        Assert.Contains(FirstRunLoginCoordinator.DefaultSignInBaseUrl, openedUrl);
        var stored = store.Load();
        Assert.NotNull(stored);
        Assert.Equal(capturedTokens.AccessToken, stored!.AccessToken);
        Assert.Equal("refresh-1", stored.RefreshToken);
        Assert.True(account.IsLoggedIn());

    }

    // Issue #642: the Director wires the coordinator with the non-persisting persist action, so a
    // successful sign-in captures the hand-back but stores NO credential.
    [Fact]
    public async Task RunAsync_DirectorNonPersistingVariant_CapturesButStoresNothing()
    {
        var store = new InMemoryTokenStore();
        var account = MakeAccount(store);
        var capturedTokens = new DevThrottleTokens(TestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1");

        using var listener = new LoopbackLoginListener();
        var coordinator = new FirstRunLoginCoordinator(
            account,
            openBrowser: _ => Task.CompletedTask,
            listenerFactory: () => listener,
            persistCredential: FirstRunLoginCoordinator.WithoutPersisting);

        var run = coordinator.RunAsync();
        await PostStandInCompletionAsync(listener.CallbackUrl, capturedTokens);
        var result = await run;

        Assert.True(result.Succeeded);

        // The Director holds NO credential: nothing was stored, and IsLoggedIn stays false.
        Assert.False(store.HasTokens);
        Assert.Null(store.Load());
        Assert.False(account.IsLoggedIn());

    }

    // Failure path: a browser that cannot be opened returns a user-safe failure, not a thrown exception.
    [Fact]
    public async Task RunAsync_BrowserCannotOpen_ReturnsFailureWithoutStoring()
    {
        var store = new InMemoryTokenStore();
        var account = MakeAccount(store);

        var coordinator = new FirstRunLoginCoordinator(
            account,
            openBrowser: _ => throw new InvalidOperationException("no browser"),
            listenerFactory: () => new LoopbackLoginListener());

        var result = await coordinator.RunAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.False(store.HasTokens);
    }

    // Cancellation: cancelling before the hand-back returns a user-safe failure and stores nothing.
    [Fact]
    public async Task RunAsync_CancelledBeforeHandBack_ReturnsFailureWithoutStoring()
    {
        var store = new InMemoryTokenStore();
        var account = MakeAccount(store);
        using var listener = new LoopbackLoginListener();
        using var cts = new CancellationTokenSource();

        var coordinator = new FirstRunLoginCoordinator(
            account,
            openBrowser: _ => Task.CompletedTask,
            listenerFactory: () => listener);

        var run = coordinator.RunAsync(cts.Token);
        cts.Cancel();
        var result = await run;

        Assert.False(result.Succeeded);
        Assert.False(store.HasTokens);
    }

    /// <summary>
    /// Simulates the sign-in completion (the local stand-in for the backend) handing the credential
    /// back to the loopback callback as the access_token and refresh_token query parameters.
    /// </summary>
    private static async Task PostStandInCompletionAsync(Uri callbackUrl, DevThrottleTokens tokens)
    {
        var builder = new UriBuilder(callbackUrl)
        {
            Query = $"access_token={Uri.EscapeDataString(tokens.AccessToken)}&refresh_token={Uri.EscapeDataString(tokens.RefreshToken)}",
        };
        using var http = new HttpClient();
        // Give the listener a moment to begin accepting; retry briefly so the test is not timing-fragile.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var response = await http.GetAsync(builder.Uri);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Stand-in completion could not reach the loopback callback.");
    }
}
