using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.UnitTests.Network;

/// <summary>
/// Which connect failures a retrying client may leave out of the error reports (issue #3641): only those that say
/// the Gateway could not be reached. Anything that reached the far side and still failed is reported.
/// </summary>
public sealed class GatewayHttpIsUnreachableTests
{
    public static TheoryData<Exception> Unreachable => new()
    {
        new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
        new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"),
        new HttpRequestException(HttpRequestError.ResponseEnded, "dropped"),
        new HttpRequestException("wrapped", new SocketException((int)SocketError.ConnectionRefused)),
        new HttpRequestException("restarting", null, HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway),
        new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout),
        new SocketException((int)SocketError.NetworkUnreachable),
        new TimeoutException("no answer"),
        new TaskCanceledException("timed out"),
        new IOException("reset", new SocketException((int)SocketError.ConnectionReset)),
    };

    public static TheoryData<Exception> Reported => new()
    {
        new HttpRequestException(HttpRequestError.SecureConnectionError, "certificate refused", new AuthenticationException("bad cert")),
        new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy refused the tunnel"),
        new HttpRequestException("server error", null, HttpStatusCode.InternalServerError),
        new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden),
        new WebSocketException("the proxy blocked the websocket"),
        new InvalidOperationException("our own code"),
        new NullReferenceException(),
    };

    [Theory]
    [MemberData(nameof(Unreachable))]
    public void IsUnreachable_TheGatewayCouldNotBeReached_IsTrue(Exception ex)
        => Assert.True(GatewayHttp.IsUnreachable(ex));

    [Theory]
    [MemberData(nameof(Reported))]
    public void IsUnreachable_ReachedTheFarSideAndStillFailed_IsFalse(Exception ex)
        => Assert.False(GatewayHttp.IsUnreachable(ex));
}
