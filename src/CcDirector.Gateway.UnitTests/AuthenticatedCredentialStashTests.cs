using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The authentication gate resolves the caller's identity ONCE and passes it forward:
/// <see cref="AuthMiddleware.AuthenticatedCredentialItemKey"/> carries THE EXACT CREDENTIAL STRING THE GATE
/// ACCEPTED, for every accepted credential shape.
///
/// Anything needing a per-caller identity - a storage partition, a per-device context key - must read that,
/// instead of reading the raw request a second time and reaching its own conclusion. The gate accepts a
/// request if ANY presented credential is valid (it tries the Bearer, then every raw cc-gateway-token
/// cookie), so a second reader with its own preference order will disagree with it: authenticated on the
/// cookie, identified by an attacker-chosen Bearer. These tests pin the stash for each shape, including the
/// two disagreement shapes, so that mechanism cannot quietly stop being available.
/// </summary>
public sealed class AuthenticatedCredentialStashTests : IDisposable
{
    private const string SharedToken = "shared-machine-token-stash-tests";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DeviceRegistry _devices;
    private readonly string _deviceKey;

    public AuthenticatedCredentialStashTests()
    {
        _devices = _harness.OpenDevices();
        _deviceKey = _devices.Register("device-one", "PHONE-ONE", "android", "phone").DeviceKey;
    }

    public void Dispose() => _harness.Dispose();

    private static string? StashedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedCredentialItemKey, out var v) ? v as string : null;

    private bool Authenticate(HttpContext ctx)
        => AuthMiddleware.HasValidToken(ctx, SharedToken, _devices, rejectSharedToken: false);

    private static HttpContext WithBearer(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {value}";
        return ctx;
    }

    private static HttpContext WithRawCookieHeader(string cookieHeader)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = cookieHeader;
        return ctx;
    }

    [Fact]
    public void BearerDeviceKey_StashesThatDeviceKey()
    {
        var ctx = WithBearer(_deviceKey);

        Assert.True(Authenticate(ctx)); // positive control: this credential really did authenticate
        Assert.Equal(_deviceKey, StashedCredential(ctx));
    }

    [Fact]
    public void BearerSharedToken_StashesTheSharedToken()
    {
        // The shared machine token is accepted on a branch that stashes NO device key, so this is the shape
        // most easily left with no identity at all.
        var ctx = WithBearer(SharedToken);

        Assert.True(Authenticate(ctx));
        Assert.Equal(SharedToken, StashedCredential(ctx));
    }

    [Fact]
    public void CookieDeviceKey_StashesThatDeviceKey()
    {
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}={_deviceKey}");

        Assert.True(Authenticate(ctx));
        Assert.Equal(_deviceKey, StashedCredential(ctx));
    }

    [Fact]
    public void CookieSharedToken_StashesTheSharedToken()
    {
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}={SharedToken}");

        Assert.True(Authenticate(ctx));
        Assert.Equal(SharedToken, StashedCredential(ctx));
    }

    [Fact]
    public void ChosenBearerBesideAValidCookie_StashesTheCOOKIE_TheOneThatAuthenticated()
    {
        // The disagreement shape. The Bearer is rejected and the cookie is what let the request in, so the
        // identity is the cookie's. A reader that preferred the Bearer would be identifying the caller by a
        // value nothing ever validated.
        const string chosenBearer = "attacker-chosen-value-9001";
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}={_deviceKey}");
        ctx.Request.Headers.Authorization = $"Bearer {chosenBearer}";

        Assert.True(Authenticate(ctx));
        Assert.Equal(_deviceKey, StashedCredential(ctx));
        Assert.NotEqual(chosenBearer, StashedCredential(ctx));
    }

    [Fact]
    public void DuplicateCookies_StashTheVALIDATEDOne_NotTheFirstOne()
    {
        // The gate tolerates a stale duplicate cc-gateway-token and accepts on whichever value is valid.
        const string stale = "stale-duplicate-value-9002";
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}={stale}; {AuthMiddleware.CookieName}={_deviceKey}");

        Assert.True(Authenticate(ctx));
        Assert.Equal(_deviceKey, StashedCredential(ctx));
        Assert.NotEqual(stale, StashedCredential(ctx));
    }

    [Fact]
    public void ARejectedRequest_StashesNothing()
    {
        // No credential was authenticated, so there is no identity to pass forward. Absent must stay absent:
        // it is the signal that there is nothing to partition by, not an invitation to go and read the
        // headers.
        var ctx = WithBearer("not-a-valid-credential-at-all");

        Assert.False(Authenticate(ctx));
        Assert.Null(StashedCredential(ctx));
    }

    [Fact]
    public void TheSharedToken_DoesNotStashADeviceKey()
    {
        // The pre-existing device-key stash keeps its meaning: absent means "shared machine token, no
        // device", which hosted tenant resolution depends on. The new credential stash is separate.
        var ctx = WithBearer(SharedToken);

        Assert.True(Authenticate(ctx));
        Assert.False(ctx.Items.ContainsKey(AuthMiddleware.DeviceKeyItemKey));
        Assert.Equal(SharedToken, StashedCredential(ctx));
    }

    [Fact]
    public void ADeviceKey_StillStashesTheDeviceKey()
    {
        // The other half of the control above: the device-key stash is untouched by the new one.
        var ctx = WithBearer(_deviceKey);

        Assert.True(Authenticate(ctx));
        Assert.Equal(_deviceKey, ctx.Items[AuthMiddleware.DeviceKeyItemKey]);
        Assert.Equal(_deviceKey, StashedCredential(ctx));
    }
}
