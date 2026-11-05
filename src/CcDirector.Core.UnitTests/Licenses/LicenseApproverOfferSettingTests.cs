using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The offer setting: whether this machine offers its browser profiles as approvers. ON unless config.json says
/// exactly false, and it sits beside the license switching setting without disturbing it.
/// </summary>
public sealed class LicenseApproverOfferSettingTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"licenseSwitching\": {\"enabled\": true}}")]
    [InlineData("{\"licenseSwitching\": {\"offerApprovers\": true}}")]
    [InlineData("{\"licenseSwitching\": {\"offerApprovers\": \"false\"}}")]
    [InlineData("{\"offerApprovers\": false}")]
    public void ReadFrom_AnythingButAnExplicitFalse_IsOn(string config)
    {
        Assert.True(LicenseApproverOfferSetting.ReadFrom(JsonNode.Parse(config)!.AsObject()));
    }

    [Fact]
    public void ReadFrom_ExplicitFalse_IsOff_AndLeavesLicenseSwitchingAsItIs()
    {
        var root = JsonNode.Parse("{\"licenseSwitching\": {\"enabled\": true, \"offerApprovers\": false}}")!.AsObject();

        Assert.False(LicenseApproverOfferSetting.ReadFrom(root));
        Assert.True(LicenseSwitchingSetting.ReadExplicit(root));
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
