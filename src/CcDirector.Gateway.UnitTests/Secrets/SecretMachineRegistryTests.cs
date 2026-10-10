using System.Security.Cryptography;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Secrets;
using Xunit;

namespace CcDirector.Gateway.Tests.Secrets;

/// <summary>
/// The machines that can receive a secret (the Secret Handoff mission), as the Gateway learns them from each Director's
/// Hello: connected Directors only, one row per machine, public keys only, and a machine whose Directors disagree on
/// the key is said to conflict rather than one key being picked.
/// </summary>
public sealed class SecretMachineRegistryTests
{
    private static readonly TenantId AccountA = new("tenant-a");
    private static readonly TenantId AccountB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Machines_ListsAConnectedDirectorsKey_WithItsFingerprint()
    {
        var registry = new SecretMachineRegistry();
        var key = NewKey();
        registry.Record(AccountA, "dir-1", "SOREN_NORTH", key, Now);

        var row = Assert.Single(registry.Machines(AccountA, _ => true, Now));

        Assert.Equal("SOREN_NORTH", row.Machine);
        Assert.Equal(key, row.PublicKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(key))).ToLowerInvariant(), row.Fingerprint);
        Assert.Equal(1, row.Directors);
        Assert.Null(row.Conflict);
    }

    // Each exclusion below keeps one live Director of account A beside the one left out, so the proof is that exactly
    // the live row remains - a registry that recorded nothing at all would fail it.
    private static SecretMachineRegistry WithALiveNorth()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "live", "SOREN_NORTH", NewKey(), Now);
        return registry;
    }

    private static void OnlyTheLiveNorth(List<CcDirector.Gateway.Contracts.SecretMachineDto> rows)
        => Assert.Equal("SOREN_NORTH", Assert.Single(rows).Machine);

    [Fact]
    public void Machines_LeavesOut_ADirectorThatIsNotConnected()
    {
        var registry = WithALiveNorth();
        registry.Record(AccountA, "gone", "MAC-MINI", NewKey(), Now);

        OnlyTheLiveNorth(registry.Machines(AccountA, id => id != "gone", Now));
    }

    [Fact]
    public void Machines_LeavesOut_ADirectorThatHasGoneQuiet()
    {
        var registry = WithALiveNorth();
        registry.Record(AccountA, "quiet", "LINUX", NewKey(), Now - SecretMachineRegistry.StaleAfter - TimeSpan.FromSeconds(1));

        OnlyTheLiveNorth(registry.Machines(AccountA, _ => true, Now));
    }

    [Fact]
    public void Machines_LeavesOut_AnotherAccountsDirector()
    {
        var registry = WithALiveNorth();
        registry.Record(AccountB, "other", "THEIRS", NewKey(), Now);

        OnlyTheLiveNorth(registry.Machines(AccountA, _ => true, Now));
    }

    [Fact]
    public void Record_WithNoKey_ForgetsAnEarlierKey_SoAnOlderBuildStopsBeingListed()
    {
        var registry = WithALiveNorth();
        registry.Record(AccountA, "dir-1", "MAC-MINI", NewKey(), Now);

        registry.Record(AccountA, "dir-1", "MAC-MINI", "", Now);

        OnlyTheLiveNorth(registry.Machines(AccountA, _ => true, Now));
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void Record_AMalformedKey_IsNotListed(string key)
    {
        var registry = WithALiveNorth();

        registry.Record(AccountA, "dir-1", "MAC-MINI", key, Now);

        OnlyTheLiveNorth(registry.Machines(AccountA, _ => true, Now));
    }

    [Fact]
    public void Record_ForgetsADirectorLongGone_SoTheListDoesNotGrowForever()
    {
        var registry = WithALiveNorth();
        registry.Record(AccountA, "long-gone", "OLD-LAPTOP", NewKey(), Now - SecretMachineRegistry.ForgetAfter - TimeSpan.FromSeconds(1));

        registry.Record(AccountA, "live", "SOREN_NORTH", NewKey(), Now);

        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Machines_TwoDirectorsOnOneMachine_WithTheSameKey_AreOneRow()
    {
        var registry = new SecretMachineRegistry();
        var key = NewKey();
        registry.Record(AccountA, "installed", "SOREN_NORTH", key, Now);
        registry.Record(AccountA, "slot-5", "soren_north", key, Now);

        var row = Assert.Single(registry.Machines(AccountA, _ => true, Now));

        Assert.Equal(2, row.Directors);
        Assert.Equal(key, row.PublicKey);
    }

    [Fact]
    public void Machines_TwoDirectorsOnOneMachine_WithDifferentKeys_Conflict_AndOfferNoKey()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "installed", "SOREN_NORTH", NewKey(), Now);
        registry.Record(AccountA, "slot-5", "SOREN_NORTH", NewKey(), Now);

        var row = Assert.Single(registry.Machines(AccountA, _ => true, Now));

        Assert.Equal("", row.PublicKey);
        Assert.Equal("", row.Fingerprint);
        Assert.Contains("2 different keys", row.Conflict);
    }
}
