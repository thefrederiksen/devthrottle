using System.Net;
using System.Text;
using CcRecorder.Account;
using Xunit;

namespace CcRecorder.Tests;

public class LoopbackSignInListenerTests
{
    private const string S = "0123456789abcdef0123456789abcdef";
    private const string Crlf = "\r\n";

    private static LoopbackSignInListener.HttpRequestData Get(string pathAndQuery)
    {
        var q = pathAndQuery.IndexOf('?');
        return q < 0
            ? new("GET", pathAndQuery, "", "")
            : new("GET", pathAndQuery[..q], pathAndQuery[q..], "");
    }

    [Fact]
    public void Handle_QueryWithBothTokens_ReturnsTokens()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?access_token=a1&refresh_token=r1&state=" + S), S);

        Assert.NotNull(outcome.Tokens);
        Assert.Equal("a1", outcome.Tokens!.AccessToken);
        Assert.Equal("r1", outcome.Tokens.RefreshToken);
        Assert.Equal(200, outcome.Status);
    }

    [Fact]
    public void Handle_CallbackWithoutTrailingSlash_IsStillTheCallback()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback?access_token=a1&refresh_token=r1&state=" + S), S);

        Assert.NotNull(outcome.Tokens);
    }

    [Fact]
    public void Handle_QueryWithOnlyOneToken_FailsLoud()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?access_token=a1&state=" + S), S);

        Assert.Null(outcome.Tokens);
        Assert.NotNull(outcome.Failure);
        Assert.Equal(400, outcome.Status);
    }

    [Fact]
    public void Handle_UserDeclined_FailsWithReason()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?error=access_denied&state=" + S), S);

        Assert.Null(outcome.Tokens);
        Assert.Contains("access_denied", outcome.Failure);
    }

    [Fact]
    public void Handle_NoTokenInUrl_ServesTheFragmentHandbackPage()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/"), S);

        Assert.Null(outcome.Tokens);
        Assert.Null(outcome.Failure);
        Assert.Contains("location.hash", outcome.Body);
        Assert.DoesNotContain("access_token=", outcome.Body);
    }

    [Fact]
    public void Handle_PostedBodyWithBothTokens_ReturnsTokens()
    {
        var outcome = LoopbackSignInListener.Handle(new("POST", "/devthrottle-login-callback/", "",
            "{\"access_token\":\"a2\",\"refresh_token\":\"r2\",\"state\":\"" + S + "\"}"), S);

        Assert.Equal("a2", outcome.Tokens!.AccessToken);
        Assert.Equal("r2", outcome.Tokens.RefreshToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"access_token\":\"a2\"}")]
    [InlineData("[1,2]")]
    public void Handle_PostedBodyIncomplete_IsRefusedButDoesNotEndTheWait(string body)
    {
        // The hand-back page only posts complete bodies, so an incomplete one is a stranger's: it must
        // not be able to cancel a sign-in in progress.
        var outcome = LoopbackSignInListener.Handle(new("POST", "/devthrottle-login-callback/", "", body), S);

        Assert.Null(outcome.Tokens);
        Assert.Null(outcome.Failure);
        Assert.Equal(400, outcome.Status);
    }

    [Fact]
    public void Handle_OtherPath_IsNotFoundAndKeepsWaiting()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/favicon.ico"), S);

        Assert.Equal(404, outcome.Status);
        Assert.Null(outcome.Tokens);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void CallbackUrl_IsLoopbackOnTheWebsitesOneAllowedPath()
    {
        using var listener = new LoopbackSignInListener();

        Assert.Equal("http", listener.CallbackUrl.Scheme);
        Assert.Equal("127.0.0.1", listener.CallbackUrl.Host);
        Assert.Equal("/devthrottle-login-callback/", listener.CallbackUrl.AbsolutePath);
        Assert.True(listener.CallbackUrl.Port > 0);
    }

    [Fact]
    public async Task WaitForTokensAsync_QueryHandbackOverARealSocket_ReturnsTokens()
    {
        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(CancellationToken.None);

        using var http = new HttpClient();
        var response = await http.GetAsync(new Uri(listener.CallbackUrl, "?access_token=a3&refresh_token=r3&state=" + listener.State));
        var tokens = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a3", tokens.AccessToken);
        Assert.Equal("r3", tokens.RefreshToken);
    }

    [Fact]
    public async Task WaitForTokensAsync_FragmentHandbackOverARealSocket_ServesPageThenTakesThePost()
    {
        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(CancellationToken.None);

        using var http = new HttpClient();
        var page = await http.GetStringAsync(listener.CallbackUrl);
        Assert.False(waiting.IsCompleted);
        Assert.Contains("fetch(location.pathname", page);

        var post = await http.PostAsync(listener.CallbackUrl,
            new StringContent("{\"access_token\":\"a4\",\"refresh_token\":\"r4\",\"state\":\"" + listener.State + "\"}", Encoding.UTF8, "application/json"));
        var tokens = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("a4", tokens.AccessToken);
    }

    [Fact]
    public async Task WaitForTokensAsync_IncompleteHandback_ThrowsSignInFailed()
    {
        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync(new Uri(listener.CallbackUrl, "?access_token=only&state=" + listener.State));

        await Assert.ThrowsAsync<SignInFailedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WaitForTokensAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var listener = new LoopbackSignInListener();
        using var cts = new CancellationTokenSource();
        var waiting = listener.WaitForTokensAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&state=wrong")]
    public void Handle_QueryWithoutThisSignInsState_IsRefusedButDoesNotEndTheWait(string stateSuffix)
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?access_token=evil&refresh_token=evil" + stateSuffix), S);

        Assert.Equal(400, outcome.Status);
        Assert.Null(outcome.Tokens);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void Handle_DeclineWithoutThisSignInsState_DoesNotCancelTheSignIn()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?error=access_denied&state=wrong"), S);

        Assert.Null(outcome.Failure);
        Assert.Null(outcome.Tokens);
    }

    [Theory]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"state\":\"wrong\"}")]
    public void Handle_PostWithoutThisSignInsState_IsRefusedButDoesNotEndTheWait(string body)
    {
        var outcome = LoopbackSignInListener.Handle(new("POST", "/devthrottle-login-callback/", "", body), S);

        Assert.Equal(400, outcome.Status);
        Assert.Null(outcome.Tokens);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void Handle_PercentEncodedTokens_AreDecoded()
    {
        var outcome = LoopbackSignInListener.Handle(Get("/devthrottle-login-callback/?access_token=eyJ%2Ba%3D%3D&refresh_token=r%2F1&state=" + S), S);

        Assert.Equal("eyJ+a==", outcome.Tokens!.AccessToken);
        Assert.Equal("r/1", outcome.Tokens.RefreshToken);
    }

    [Fact]
    public void State_IsRandomPerSignIn()
    {
        using var first = new LoopbackSignInListener();
        using var second = new LoopbackSignInListener();

        Assert.Equal(32, first.State.Length);
        Assert.NotEqual(first.State, second.State);
    }

    [Fact]
    public async Task WaitForTokensAsync_AStrangersHandbackThenTheRealOne_TakesOnlyTheRealOne()
    {
        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(CancellationToken.None);

        using var http = new HttpClient();
        var stranger = await http.GetAsync(new Uri(listener.CallbackUrl, "?access_token=evil&refresh_token=evil&state=guess"));
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.False(waiting.IsCompleted);

        await http.GetAsync(new Uri(listener.CallbackUrl, "?access_token=real&refresh_token=real-r&state=" + listener.State));
        var tokens = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("real", tokens.AccessToken);
    }

    [Theory]
    [InlineData("Content-Length: nonsense")]
    [InlineData("Content-Length: -5")]
    [InlineData("Content-Length: 99999999")]
    public async Task WaitForTokensAsync_UnreadableRequest_IsDroppedAndTheWaitGoesOn(string badHeader)
    {
        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(CancellationToken.None);

        using (var raw = new System.Net.Sockets.TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, listener.CallbackUrl.Port);
            var request = "POST /devthrottle-login-callback/ HTTP/1.1" + Crlf + "Host: x" + Crlf + badHeader + Crlf + Crlf;
            var bytes = Encoding.ASCII.GetBytes(request);
            await raw.GetStream().WriteAsync(bytes);
            await Task.Delay(200);
        }
        Assert.False(waiting.IsCompleted);

        using var http = new HttpClient();
        await http.GetAsync(new Uri(listener.CallbackUrl, "?access_token=a5&refresh_token=r5&state=" + listener.State));
        var tokens = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("a5", tokens.AccessToken);
    }
}
