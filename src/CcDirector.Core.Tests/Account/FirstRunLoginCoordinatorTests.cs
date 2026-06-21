using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the first-run login orchestration (issue #581): the system browser is opened at the sign-in
/// address, the credential the sign-in completion hands back is captured, and it is stored through the
/// credential service (issue #583). The browser launch and the loopback hand-back are injected so the
/// flow is provable without a real browser or a real backend.
/// </summary>
public sealed class FirstRunLoginCoordinatorTests
{
    private static DevThrottleAccountService MakeAccount(InMemoryTokenStore store) =>
        new(store, new JwtAccessTokenValidator(TestJwt.SigningSecret), new AuthEventLog(NullEventLogPath()), new StubTokenRefresher(null));

    private static string NullEventLogPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-dt-login-" + Guid.NewGuid().ToString("N") + ".jsonl");

    // Acceptance criterion: the Log in action opens the system browser at the expected sign-in address.
    [Fact]
    public void BuildSignInUrl_CarriesTheLoopbackCallbackAsRedirect()
    {
        var callback = new Uri("http://127.0.0.1:54321/devthrottle-login-callback/");

        var url = FirstRunLoginCoordinator.BuildSignInUrl(callback);

        Assert.StartsWith(FirstRunLoginCoordinator.DefaultSignInBaseUrl, url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(callback.ToString()), url);
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
