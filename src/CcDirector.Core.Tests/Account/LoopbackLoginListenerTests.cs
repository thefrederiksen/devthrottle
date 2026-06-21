using System.Net;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the loopback listener that captures the credential the sign-in completion hands back (issue
/// #581). It must bind loopback only (security rule DT-07), capture both tokens from the callback,
/// and fail loud (no fallback) on a callback missing a token.
/// </summary>
public sealed class LoopbackLoginListenerTests
{
    // The listener binds 127.0.0.1 only - never a routable address (DT-07).
    [Fact]
    public void CallbackUrl_IsLoopbackOnly()
    {
        using var listener = new LoopbackLoginListener();

        Assert.Equal("127.0.0.1", listener.CallbackUrl.Host);
        Assert.True(listener.CallbackUrl.Port > 0);
    }

    // The credential the sign-in completion hands back is captured as both tokens.
    [Fact]
    public async Task WaitForCredentialAsync_CapturesBothTokensFromTheCallback()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostCallbackAsync(listener.CallbackUrl, "access-xyz", "refresh-abc");
        var tokens = await wait;

        Assert.Equal("access-xyz", tokens.AccessToken);
        Assert.Equal("refresh-abc", tokens.RefreshToken);
    }

    // A callback missing the refresh token fails loud (no half-credential is captured).
    [Fact]
    public async Task WaitForCredentialAsync_MissingRefreshToken_Throws()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostCallbackAsync(listener.CallbackUrl, "access-only", refreshToken: null, expectSuccess: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
    }

    private static async Task PostCallbackAsync(Uri callbackUrl, string? accessToken, string? refreshToken, bool expectSuccess = true)
    {
        var query = string.Empty;
        if (accessToken is not null)
            query += $"access_token={Uri.EscapeDataString(accessToken)}";
        if (refreshToken is not null)
            query += (query.Length > 0 ? "&" : "") + $"refresh_token={Uri.EscapeDataString(refreshToken)}";

        var builder = new UriBuilder(callbackUrl) { Query = query };
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var response = await http.GetAsync(builder.Uri);
                if (expectSuccess)
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                else
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Could not reach the loopback callback.");
    }
}
