using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The license switching setting: OFF unless config.json says exactly true, because until phase 5 nothing may touch a
/// machine's real default Claude login. Off means no usage is read or reported and no switch runs.
/// </summary>
public sealed class LicenseSwitchingSettingTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"licenseSwitching\": {}}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": false}}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": \"true\"}}")]
    [InlineData("{\"licenseSwitching\": true}")]
    public void ReadFrom_AnythingButAnExplicitTrue_IsOff(string config)
    {
        Assert.False(LicenseSwitchingSetting.ReadFrom(JsonNode.Parse(config)!.AsObject()));
    }

    [Fact]
    public void ReadFrom_ExplicitTrue_IsOn()
    {
        Assert.True(LicenseSwitchingSetting.ReadFrom(JsonNode.Parse("{\"licenseSwitching\": {\"enabled\": true}}")!.AsObject()));
    }

    [Fact]
    public void BuildPatch_ReadsBackAsWritten()
    {
        Assert.True(LicenseSwitchingSetting.ReadFrom(LicenseSwitchingSetting.BuildPatch(true)));
        Assert.False(LicenseSwitchingSetting.ReadFrom(LicenseSwitchingSetting.BuildPatch(false)));
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
