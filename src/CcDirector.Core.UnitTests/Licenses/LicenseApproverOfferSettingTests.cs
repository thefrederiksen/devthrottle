using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The offer setting: whether this machine offers its browser profiles as approvers. OFF unless config.json says
/// exactly true, and it sits beside the license switching setting without disturbing it.
/// </summary>
public sealed class LicenseApproverOfferSettingTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": true}}")]
    [InlineData("{\"licenseSwitching\": {\"offerApprovers\": false}}")]
    [InlineData("{\"licenseSwitching\": {\"offerApprovers\": \"true\"}}")]
    [InlineData("{\"offerApprovers\": true}")]
    public void ReadFrom_AnythingButAnExplicitTrue_IsOff(string config)
    {
        Assert.False(LicenseApproverOfferSetting.ReadFrom(JsonNode.Parse(config)!.AsObject()));
    }

    [Fact]
    public void ReadFrom_ExplicitTrue_IsOn_AndLeavesLicenseSwitchingAsItIs()
    {
        var root = JsonNode.Parse("{\"licenseSwitching\": {\"enabled\": false, \"offerApprovers\": true}}")!.AsObject();

        Assert.True(LicenseApproverOfferSetting.ReadFrom(root));
        Assert.False(LicenseSwitchingSetting.ReadFrom(root));
    }

    [Fact]
    public void BuildPatch_ReadsBackAsWritten_AndTouchesOnlyItsOwnKey()
    {
        Assert.True(LicenseApproverOfferSetting.ReadFrom(LicenseApproverOfferSetting.BuildPatch(true)));
        Assert.False(LicenseApproverOfferSetting.ReadFrom(LicenseApproverOfferSetting.BuildPatch(false)));
        Assert.Equal(new[] { "offerApprovers" },
            LicenseApproverOfferSetting.BuildPatch(true)["licenseSwitching"]!.AsObject().Select(p => p.Key));
    }
}
