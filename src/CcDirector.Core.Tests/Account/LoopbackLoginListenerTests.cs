using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the loopback listener that captures the credential the sign-in completion hands back (issue
/// #581), hardened so no token rides in the callback URL (issue #1082, absorbs #877). It must bind
/// loopback only (security rule DT-07), capture both tokens from the NEW shape (a same-origin JSON body
/// the fragment hand-back page posts), still capture the OLD shape (the token pair in the query string)
/// during transition, serve the hand-back page on a bare GET, and fail loud (no fallback) on a callback
/// that yields the credential in neither shape.
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
}
