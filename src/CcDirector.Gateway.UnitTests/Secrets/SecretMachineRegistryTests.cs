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

    // THE MACHINE OF A CREDENTIAL (found in the live QA on 2026-10-11): a Mac enrolled as "Sorens-Mac-mini" whose Director
    // reports "devthrottle-mac-mini". A transfer names machines as the list does, so the credential must resolve to the
    // name its own Directors report - by name it was refused as a third machine on its own Mac.

    private static readonly Func<string, string?> NoEnrollment = _ => null;

    // SOREN_NORTH holds two device credentials (found in the same QA run): cc-secrets reads the machine's own config.json,
    // its Directors their instance's - two devices, both enrolled as SOREN_NORTH. The window's credential has no Director
    // of its own, and must still be SOREN_NORTH.
    private static readonly Func<string, string?> Enrolled = credential => credential switch
    {
        "device:north-tools" => "SOREN_NORTH",
        "device:north-director" => "soren_north",
        "device:mac-device" => "Sorens-Mac-mini",
        "device:mac-tools" => "Sorens-Mac-mini",
        "device:other-machine" => "SOMEWHERE_ELSE",
        _ => null,
    };

    [Fact]
    public void MachineOfCredential_ADeviceWithNoDirector_IsTheMachineOfTheDirectorsEnrolledUnderItsName()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "dir-north", "SOREN_NORTH", NewKey(), Now, "device:north-director");
        registry.Record(AccountA, "dir-mac", "devthrottle-mac-mini", NewKey(), Now, "device:mac-device");

        Assert.Equal("SOREN_NORTH", registry.MachineOfCredential(AccountA, "device:north-tools", Enrolled, _ => true, Now));
        Assert.Equal("devthrottle-mac-mini", registry.MachineOfCredential(AccountA, "device:mac-tools", Enrolled, _ => true, Now));
        // Enrolled under a name no live Director's device carries, or not enrolled at all: no machine.
        Assert.Null(registry.MachineOfCredential(AccountA, "device:other-machine", Enrolled, _ => true, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:unenrolled", Enrolled, _ => true, Now));
        // The same name in ANOTHER account never answers for this one.
        Assert.Null(registry.MachineOfCredential(AccountB, "device:north-tools", Enrolled, _ => true, Now));
    }

    [Fact]
    public void MachineOfCredential_TwoMachinesEnrolledUnderOneName_IsNull()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "dir-north", "SOREN_NORTH", NewKey(), Now, "device:north-director");
        registry.Record(AccountA, "dir-copy", "ANOTHER_BOX", NewKey(), Now, "device:north-tools");

        Assert.Null(registry.MachineOfCredential(AccountA, "device:north-tools", Enrolled, _ => true, Now));
    }

    [Fact]
    public void MachineOfCredential_IsTheNameItsOwnDirectorReports_NotTheEnrollmentName()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "dir-mac", "devthrottle-mac-mini", NewKey(), Now, "device:mac-device");
        registry.Record(AccountA, "dir-north", "SOREN_NORTH", NewKey(), Now, "device:north-device");

        Assert.Equal("devthrottle-mac-mini", registry.MachineOfCredential(AccountA, "device:mac-device", NoEnrollment, _ => true, Now));
        Assert.Equal("SOREN_NORTH", registry.MachineOfCredential(AccountA, "device:north-device", NoEnrollment, _ => true, Now));
    }

    [Fact]
    public void MachineOfCredential_TwoDirectorsOfOneCredentialOnOneMachine_IsThatMachine()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "dir-installed", "devthrottle-mac-mini", NewKey(), Now, "device:mac-device");
        registry.Record(AccountA, "dir-slot-5", "DEVTHROTTLE-MAC-MINI", NewKey(), Now, "device:mac-device");

        Assert.Equal("devthrottle-mac-mini",
            registry.MachineOfCredential(AccountA, "device:mac-device", NoEnrollment, _ => true, Now), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineOfCredential_IsNull_WhenItCannotBeTold()
    {
        var registry = new SecretMachineRegistry();
        registry.Record(AccountA, "dir-a", "machine-a", NewKey(), Now, "device:shared");
        registry.Record(AccountA, "dir-b", "machine-b", NewKey(), Now, "device:shared");     // two machines: no guess
        registry.Record(AccountA, "dir-old", "machine-c", NewKey(), Now - SecretMachineRegistry.StaleAfter - TimeSpan.FromSeconds(1),
            "device:stale");
        registry.Record(AccountA, "dir-gone", "machine-d", NewKey(), Now, "device:disconnected");
        registry.Record(AccountA, "dir-nokey", "machine-e", NewKey(), Now, "device:no-key");
        registry.Record(AccountA, "dir-nokey", "machine-e", null, Now, "device:no-key");      // an older build: no key now
        registry.Record(AccountB, "dir-other", "machine-f", NewKey(), Now, "device:other-account");

        Func<string, bool> connected = id => id != "dir-gone";
        Assert.Null(registry.MachineOfCredential(AccountA, "device:shared", NoEnrollment, connected, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:stale", NoEnrollment, connected, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:disconnected", NoEnrollment, connected, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:no-key", NoEnrollment, connected, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:other-account", NoEnrollment, connected, Now));
        Assert.Null(registry.MachineOfCredential(AccountA, "device:never-seen", NoEnrollment, connected, Now));
        Assert.Equal("machine-f", registry.MachineOfCredential(AccountB, "device:other-account", NoEnrollment, _ => true, Now));
    }
}
