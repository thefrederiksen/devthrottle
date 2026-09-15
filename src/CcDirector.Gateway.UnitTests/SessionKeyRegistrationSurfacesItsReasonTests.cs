using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A FAILED SESSION-KEY REGISTRATION MUST SAY WHY (issue #2848).
///
/// On 2026-09-14 one machine logged 2,310 consecutive failures of `RegisterSessionKey`, refusing every
/// agent session key on the fleet, and not one of those lines named a cause. The Director received only
/// SignalR's wrapper - "An unexpected error occurred invoking 'RegisterSessionKey' on the server" - which
/// is what SignalR sends when a hub method throws anything that is NOT a <c>HubException</c>.
///
/// The registry's own guards are the untyped throws. Its three <see cref="ArgumentException"/> checks sit
/// ABOVE its try block, so its filtered catch cannot reach them:
///
///     if (!tenant.IsValid) throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
///
/// These tests pin that behaviour at the registry, which is where it starts. The hub's job - converting it
/// into a sentence the Director can log - is pinned beside them in the hub's own tests.
///
/// The cost of the missing sentence was not the outage. It was that the outage taught nobody anything:
/// 2,310 identical lines, each of which could have named the argument.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class SessionKeyRegistrationSurfacesItsReasonTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private static readonly TenantId Account = new("tenant-a");

    public void Dispose() => _harness.Dispose();

    private SessionKeyRegistry Registry() => new(_harness.Open(), isHosted: false);

    private static DateTime Later => DateTime.UtcNow.AddHours(12);

    [Fact]
    public void An_invalid_tenant_throws_ArgumentException_which_the_registrys_own_catch_cannot_reach()
    {
        // THE LEADING CANDIDATE FOR THE LIVE FAILURE, pinned so the fix has something to hold.
        //
        // This guard is above the try, so it is NOT one of the deliberate `return false` refusals that
        // become a typed HubException. It escapes as an ArgumentException - and at the hub that became
        // SignalR's anonymous wrapper. The hub must therefore validate the tenant itself, or convert.
        var registry = Registry();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.Register(default, "director-1", Guid.NewGuid().ToString(), new string('a', 64), Later));

        Assert.Equal("tenant", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_session_id_throws_rather_than_refusing(string sessionId)
    {
        var registry = Registry();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.Register(Account, "director-1", sessionId, new string('a', 64), Later));

        Assert.Equal("sessionId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_key_hash_throws_rather_than_refusing(string keyHash)
    {
        var registry = Registry();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.Register(Account, "director-1", Guid.NewGuid().ToString(), keyHash, Later));

        Assert.Equal("keyHash", ex.ParamName);
    }

    [Fact]
    public void A_deliberate_refusal_returns_false_and_does_NOT_throw()
    {
        // The contrast that makes the above meaningful. A refusal the registry INTENDED - here, a key hash
        // already owned by another session - comes back as false, and the hub turns that into a
        // HubException naming the session. Those paths were always fine; it is only the guards above the
        // try that were silent.
        var registry = Registry();
        var hash = new string('b', 64);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        Assert.True(registry.Register(Account, "director-1", first, hash, Later));

        var refused = registry.Register(Account, "director-1", second, hash, Later);

        Assert.False(refused);
    }

    [Fact]
    public void A_valid_registration_still_succeeds()
    {
        // The guard rail on the guard rails: none of this may cost the ordinary path.
        var registry = Registry();

        Assert.True(registry.Register(
            Account, "director-1", Guid.NewGuid().ToString(), new string('c', 64), Later));
    }
}
