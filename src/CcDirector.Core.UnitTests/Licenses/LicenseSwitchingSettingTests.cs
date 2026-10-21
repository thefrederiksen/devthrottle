using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The license switching setting (issue #4327): an explicit true is on and an explicit false is off; when config.json
/// says nothing, it is on exactly when the machine holds a spare. Off means no usage is read and no switch runs - and,
/// when the machine holds spares, one report that says so.
/// </summary>
public sealed class LicenseSwitchingSettingTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"licenseSwitching\": {}}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": \"true\"}}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": null}}")]
    [InlineData("{\"licenseSwitching\": true}")]
    public void ReadExplicit_AnythingButABoolean_SaysNothing(string config)
    {
        Assert.Null(LicenseSwitchingSetting.ReadExplicit(JsonNode.Parse(config)!.AsObject()));
    }

    [Theory]
    [InlineData("{\"licenseSwitching\": {\"enabled\": true}}", true)]
    [InlineData("{\"licenseSwitching\": {\"enabled\": false}}", false)]
    public void ReadExplicit_ABoolean_IsWhatItSays(string config, bool expected)
    {
        Assert.Equal(expected, LicenseSwitchingSetting.ReadExplicit(JsonNode.Parse(config)!.AsObject()));
    }

    [Fact]
    public void Resolve_NothingWrittenOnAMachineHoldingASpare_IsOn()
    {
        // The case that dropped a machine out of a threshold switch on 21 October 2026: a fresh config.json with no
        // licenseSwitching section, on a machine whose key store still held its spares.
        var fresh = JsonNode.Parse("{\"gateway\": {}, \"agent\": {}}")!.AsObject();

        Assert.True(LicenseSwitchingSetting.Resolve(LicenseSwitchingSetting.ReadExplicit(fresh), holdsSpare: true));
    }

    [Fact]
    public void Resolve_NothingWrittenOnAMachineWithNoSpare_IsOff()
    {
        Assert.False(LicenseSwitchingSetting.Resolve(null, holdsSpare: false));
    }

    [Fact]
    public void Resolve_ExplicitFalse_StaysOffWhateverIsHeld()
    {
        Assert.False(LicenseSwitchingSetting.Resolve(false, holdsSpare: true));
        Assert.True(LicenseSwitchingSetting.Resolve(true, holdsSpare: false));
    }

    [Fact]
    public void BuildPatch_ReadsBackAsWritten()
    {
        Assert.True(LicenseSwitchingSetting.ReadExplicit(LicenseSwitchingSetting.BuildPatch(true)));
        Assert.False(LicenseSwitchingSetting.ReadExplicit(LicenseSwitchingSetting.BuildPatch(false)));
    }

    [Fact]
    public void Start_TurnedOffWhileTheMachineHoldsSpares_SchedulesOnlyTheSwitchedOffReport()
    {
        var store = Path.Combine(_folder.Path, "store");
        var home = Path.Combine(_folder.Path, "home");
        WriteAccount(Path.Combine(store, "spares", "b"), "account-b@example.com");
        var tool = new FakeLicenseCommandLine().Hold("account-a@example.com", active: true).Hold("account-b@example.com");
        var licenses = new LicenseSwitching(tool, Path.Combine(_folder.Path, "director"), new FakeClock().UtcNow, "the-windows-machine", "owner",
            enabled: false, heldOnDisk: new LicenseHeldOnDisk(null, store, home));
        try
        {
            licenses.Start();

            Assert.True(licenses.Reporter.IsScheduled);   // the switched-off report, so the Gateway knows
            Assert.Equal(0, tool.ListCalls);              // and still the tool is never run
            Assert.Empty(tool.UsageCalls);
            Assert.False(licenses.HolderLock.IsHeld);
        }
        finally
        {
            licenses.Dispose();
        }
    }

    internal static void WriteAccount(string folder, string email)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, LicenseHeldOnDisk.AccountFile),
            "{\"oauthAccount\": {\"emailAddress\": \"" + email + "\"}}");
    }

    [Fact]
    public async Task Start_TurnedOff_ReadsNothingTakesNoLockAndRefusesSwitches()
    {
        var tool = new FakeLicenseCommandLine().Hold("account-a@example.com", active: true).Hold("account-b@example.com");
        var licenses = new LicenseSwitching(tool, _folder.Path, () => DateTime.UtcNow, "the-windows-machine", "owner", enabled: false);
        try
        {
            licenses.Start();
            var result = await licenses.Coordinator.SwitchAsync(Request(), CancellationToken.None);

            Assert.False(licenses.Enabled);
            Assert.Equal(LicenseSwitchOutcomes.Failed, result.Outcome);
            Assert.Equal(LicenseSwitchCoordinator.TurnedOffMessage, result.Message);
            Assert.Empty(tool.SwitchCalls);
            Assert.False(licenses.Reporter.IsScheduled);   // no report loop: nothing is read or sent
            Assert.Equal(0, tool.ListCalls);
            Assert.Empty(tool.UsageCalls);
            Assert.False(licenses.HolderLock.IsHeld);
        }
        finally
        {
            licenses.Dispose();
        }
    }

    private static LicenseSwitchRequest Request() => new()
    {
        SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
        TargetAccountEmail = "account-b@example.com",
        ExpectedActiveAccountEmail = "account-a@example.com",
        Reason = LicenseSwitchReasons.Manual,
        RequestedAtUtc = DateTime.UtcNow,
        NotAfterUtc = DateTime.UtcNow + LicenseContract.SwitchNotAfter,
    };
}
