using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hosted enrollment logic (Hosted Multi-Tenancy increment 1): validate a remote Director's OWN Supabase
/// account token (signature + expiry + audience + issuer), map its subject to a tenant, and bind a per-device
/// key to that tenant. Exercised through the endpoint's extracted <see cref="HostedEnrollmentEndpoint.Enroll"/>
/// with a real ES256-signed token, so the audience/issuer enforcement is proven for real.
/// </summary>
public sealed class HostedEnrollmentEndpointTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
    }

    private (DeviceRegistry devices, TenantRegistry tenants, JwtAccessTokenValidator validator) Wire()
    {
        var db = _harness.Open();
        var devices = new DeviceRegistry(db, _harness.LegacyPath("devices.json"));
        var tenants = new TenantRegistry(db);
        // ES256-only, exactly as production BuildAuthorizationValidator configures it - HS256 is refused.
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        return (devices, tenants, validator);
    }

    private static EnrollSignedInRequest Req(string deviceId) => new()
    {
        DeviceId = deviceId,
        MachineName = "M",
        Platform = "linux",
        DeviceType = "workstation",
    };

    [Fact]
    public void ValidToken_MintsADeviceKeyBoundToTheAccountTenant()
    {
        var (devices, tenants, validator) = Wire();
        var token = _key.Token("sub-alice", "alice@example.com", Audience, Issuer);

        var result = HostedEnrollmentEndpoint.Enroll(token, Req("dev-a"), devices, tenants, validator);

        Assert.Equal(200, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Response!.DeviceKey));
        // The device is bound to the account's tenant, resolvable from the same key (what the tunnel does).
        Assert.False(string.IsNullOrEmpty(devices.TenantForKey(result.Response.DeviceKey)));
    }

    [Fact]
    public void TwoDistinctAccounts_GetTwoDistinctTenants_AndSameAccountReusesItsTenant()
    {
        var (devices, tenants, validator) = Wire();

        var a = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-alice", "a@x.com", Audience, Issuer), Req("dev-a"), devices, tenants, validator);
        var b = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-bob", "b@x.com", Audience, Issuer), Req("dev-b"), devices, tenants, validator);
        // A SECOND device of the SAME account (alice) must land in alice's existing tenant.
        var a2 = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-alice", "a@x.com", Audience, Issuer), Req("dev-a2"), devices, tenants, validator);

        var tenantA = devices.TenantForKey(a.Response!.DeviceKey);
        var tenantB = devices.TenantForKey(b.Response!.DeviceKey);
        var tenantA2 = devices.TenantForKey(a2.Response!.DeviceKey);

        Assert.NotEqual(tenantA, tenantB);
        Assert.Equal(tenantA, tenantA2);
    }

    [Fact]
    public void EnrollmentDeviceCount_IsScopedToTheCallersOwnTenant_NotTheFleetTotal()
    {
        var (devices, tenants, validator) = Wire();

        // Alice enrolls her first device: her own count is 1.
        var a1 = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-alice", "a@x.com", Audience, Issuer), Req("dev-a"), devices, tenants, validator);
        Assert.Equal(200, a1.Status);
        Assert.Equal(1, a1.Response!.DeviceCount);

        // Bob enrolls his FIRST device AFTER alice has one. The leak this closes: the response must report
        // bob's OWN count (1), never the fleet total (2). Before the fix this reported the global registry size.
        var b1 = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-bob", "b@x.com", Audience, Issuer), Req("dev-b"), devices, tenants, validator);
        Assert.Equal(200, b1.Status);
        Assert.Equal(1, b1.Response!.DeviceCount);

        // Alice enrolls a SECOND device: her count is now 2 - proving the count still reflects the caller's own
        // tenant growing, not a constant.
        var a2 = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-alice", "a@x.com", Audience, Issuer), Req("dev-a2"), devices, tenants, validator);
        Assert.Equal(200, a2.Status);
        Assert.Equal(2, a2.Response!.DeviceCount);

        // Idempotent re-enrollment of bob's existing device: the fleet now holds 3 devices, but bob must still
        // read back only his OWN 1 - he cannot poll this route to observe the total hosted fleet size.
        var b1Again = HostedEnrollmentEndpoint.Enroll(_key.Token("sub-bob", "b@x.com", Audience, Issuer), Req("dev-b"), devices, tenants, validator);
        Assert.Equal(200, b1Again.Status);
        Assert.Equal(1, b1Again.Response!.DeviceCount);
    }

    [Fact]
    public void MissingToken_Is401()
    {
        var (devices, tenants, validator) = Wire();
        var result = HostedEnrollmentEndpoint.Enroll(bearer: null, Req("dev-a"), devices, tenants, validator);
        Assert.Equal(401, result.Status);
    }

    [Fact]
    public void WrongIssuer_Is401_NoBinding()
    {
        var (devices, tenants, validator) = Wire();
        var token = _key.Token("sub-alice", "a@x.com", Audience, issuer: "https://attacker.example.com/auth/v1");

        var result = HostedEnrollmentEndpoint.Enroll(token, Req("dev-a"), devices, tenants, validator);

        Assert.Equal(401, result.Status);
    }

    [Fact]
    public void WrongAudience_Is401()
    {
        var (devices, tenants, validator) = Wire();
        var token = _key.Token("sub-alice", "a@x.com", audience: "some-other-audience", issuer: Issuer);

        var result = HostedEnrollmentEndpoint.Enroll(token, Req("dev-a"), devices, tenants, validator);

        Assert.Equal(401, result.Status);
    }

    [Fact]
    public void MissingDeviceId_Is400()
    {
        var (devices, tenants, validator) = Wire();
        var token = _key.Token("sub-alice", "a@x.com", Audience, Issuer);

        var result = HostedEnrollmentEndpoint.Enroll(token, Req("   "), devices, tenants, validator);

        Assert.Equal(400, result.Status);
    }

    [Fact]
    public void DeviceIdCollisionAcrossAccounts_CannotHijackAnotherTenantsKey()
    {
        var (devices, tenants, validator) = Wire();

        // Attacker pre-enrolls a client-chosen deviceId "shared" under its OWN account.
        var attacker = HostedEnrollmentEndpoint.Enroll(
            _key.Token("sub-attacker", "atk@x.com", Audience, Issuer), Req("shared"), devices, tenants, validator);
        var attackerKey = attacker.Response!.DeviceKey;
        var attackerTenant = devices.TenantForKey(attackerKey);

        // Victim later enrolls the SAME deviceId "shared" under the victim account.
        var victim = HostedEnrollmentEndpoint.Enroll(
            _key.Token("sub-victim", "vic@x.com", Audience, Issuer), Req("shared"), devices, tenants, validator);
        var victimKey = victim.Response!.DeviceKey;
        var victimTenant = devices.TenantForKey(victimKey);

        // The victim gets a DISTINCT key and tenant, and - the security property - the attacker's key was
        // NEVER handed over or rebound: it still resolves to the attacker's OWN tenant, not the victim's.
        Assert.NotEqual(attackerKey, victimKey);
        Assert.NotEqual(attackerTenant, victimTenant);
        Assert.Equal(attackerTenant, devices.TenantForKey(attackerKey));
        Assert.NotEqual(victimTenant, devices.TenantForKey(attackerKey));
    }

    [Fact]
    public void Hs256Token_IsRefused_EvenWithAKnownSecret()
    {
        var (devices, tenants, validator) = Wire();
        // An attacker forges an HS256 token with an arbitrary subject, signed with a known/placeholder secret.
        var forged = TestEs256Key.Hs256Token("test-signing-secret", "sub-attacker", Audience, Issuer);

        var result = HostedEnrollmentEndpoint.Enroll(forged, Req("dev-a"), devices, tenants, validator);

        Assert.Equal(401, result.Status);
    }

    [Fact]
    public void TokenWithNoExpClaim_IsRefused()
    {
        var (devices, tenants, validator) = Wire();
        var noExp = _key.Token("sub-alice", "a@x.com", Audience, Issuer, includeExp: false);

        var result = HostedEnrollmentEndpoint.Enroll(noExp, Req("dev-a"), devices, tenants, validator);

        Assert.Equal(401, result.Status);
    }

    /// <summary>The Supabase audience/issuer the production validator enforces are the project's real values.</summary>
    [Fact]
    public void BuildAuthorizationValidator_DefaultsToTheSupabaseAudienceAndIssuer()
    {
        Assert.Equal("authenticated", CcDirector.Gateway.Account.GatewayAccountFactory.DefaultSupabaseAudience);
        Assert.Equal("https://ompujpfrglgqvqprilxa.supabase.co/auth/v1",
            CcDirector.Gateway.Account.GatewayAccountFactory.DefaultSupabaseIssuer);
    }
}
