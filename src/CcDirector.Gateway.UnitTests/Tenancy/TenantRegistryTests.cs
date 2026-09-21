using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The account-to-tenant resolver (Hosted Multi-Tenancy increment 1). These tests exercise the mint-or-lookup
/// contract over a real (throwaway) EF database: a first-seen account subject mints a fresh tenant, the SAME
/// subject resolves to the SAME tenant (so a second device of one account lands in one tenant), distinct
/// subjects get distinct tenants, and the email is display metadata only - never the mapping key.
/// </summary>
public sealed class TenantRegistryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MintOrLookup_FirstSeenSubject_MintsAValidTenant()
    {
        var registry = new TenantRegistry(_harness.Open());

        var tenant = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.True(tenant.IsValid);
        Assert.False(tenant.IsLocal);
        // A code-generated GUID string, not the subject and not the email.
        Assert.True(Guid.TryParse(tenant.Value, out _));
    }

    [Fact]
    public void MintOrLookup_SameSubjectTwice_ResolvesToTheSameTenant()
    {
        var registry = new TenantRegistry(_harness.Open());

        var first = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var second = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void MintOrLookup_SameSubjectAfterRestart_StillResolvesToTheSameTenant()
    {
        var minted = new TenantRegistry(_harness.Open()).MintOrLookupBySubject("sub-alice", "alice@example.com");

        // Re-open the SAME database file (a Gateway restart) and resolve again.
        var afterRestart = new TenantRegistry(_harness.Open()).MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.Equal(minted.Value, afterRestart.Value);
    }

    [Fact]
    public void MintOrLookup_DifferentSubjects_GetDifferentTenants()
    {
        var registry = new TenantRegistry(_harness.Open());

        var alice = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var bob = registry.MintOrLookupBySubject("sub-bob", "bob@example.com");

        Assert.NotEqual(alice.Value, bob.Value);
    }

    [Fact]
    public void MintOrLookup_EmailIsNotTheKey_TwoSubjectsSharingAnEmailGetTwoTenants()
    {
        var registry = new TenantRegistry(_harness.Open());

        // The stable subject is the key; a shared (or reused) email must NEVER collapse two accounts.
        var first = registry.MintOrLookupBySubject("sub-one", "shared@example.com");
        var second = registry.MintOrLookupBySubject("sub-two", "shared@example.com");

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void MintOrLookup_NullOrBlankEmail_StillMints()
    {
        var registry = new TenantRegistry(_harness.Open());

        var tenant = registry.MintOrLookupBySubject("sub-no-email", null);

        Assert.True(tenant.IsValid);
    }

    [Fact]
    public void MintOrLookup_BlankSubject_Throws()
    {
        var registry = new TenantRegistry(_harness.Open());

        Assert.Throws<ArgumentException>(() => registry.MintOrLookupBySubject("   ", "x@example.com"));
    }

    [Fact]
    public void Lookup_UnknownSubject_ReturnsNull()
    {
        var registry = new TenantRegistry(_harness.Open());

        Assert.Null(registry.LookupBySubject("sub-never-seen"));
    }

    [Fact]
    public void Lookup_KnownSubject_ReturnsTheMintedTenant()
    {
        var registry = new TenantRegistry(_harness.Open());
        var minted = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        var looked = registry.LookupBySubject("sub-alice");

        Assert.NotNull(looked);
        Assert.Equal(minted.Value, looked!.Value.Value);
    }

    // ----- the held census (devthrottle_internal#2199: every sweep re-read it every cycle) -----

    [Fact]
    public void AllTenantIds_IsHeld_AndAMintIsSeenAtOnce()
    {
        var registry = new TenantRegistry(_harness.Open());
        var alice = registry.MintOrLookupBySubject("sub-alice", null);
        Assert.Equal(new[] { alice }, registry.AllTenantIds());

        using (var counter = new ReaderCommandCounter(_harness.DbPath))
        {
            registry.AllTenantIds();
            registry.AllTenantIds();
            Assert.Equal(0, counter.Reads);
        }

        var bob = registry.MintOrLookupBySubject("sub-bob", null);
        Assert.Equal(new[] { alice, bob }.OrderBy(t => t.Value), registry.AllTenantIds().OrderBy(t => t.Value));
    }

    /// <summary>An account another Gateway process minted (a deploy overlap) is swept once the held census is older
    /// than its ceiling, and not before.</summary>
    [Fact]
    public void AllTenantIds_SeesAnotherProcesssMint_AfterItsCeiling()
    {
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var db = _harness.Open();
        var registry = new TenantRegistry(db, () => now);
        var other = new TenantRegistry(db, () => now);
        Assert.Empty(registry.AllTenantIds());

        var minted = other.MintOrLookupBySubject("sub-carol", null);

        now += TenantRegistry.CensusMaxAge - TimeSpan.FromSeconds(1);
        Assert.Empty(registry.AllTenantIds());
        now += TimeSpan.FromSeconds(1);
        Assert.Equal(new[] { minted }, registry.AllTenantIds());
    }
}
