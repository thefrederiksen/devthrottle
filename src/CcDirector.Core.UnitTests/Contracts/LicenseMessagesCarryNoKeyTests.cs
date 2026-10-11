using System.Reflection;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Contracts;

/// <summary>
/// License Switching contract, section 6 rule 1: the Gateway never receives or stores a sign-in key. No property of a
/// License* message may be named like one. This lives in the Director's unit tests because the contracts project has
/// no test project of its own and this suite runs in the default local gate.
/// </summary>
public sealed class LicenseMessagesCarryNoKeyTests
{
    private static readonly string[] ForbiddenWords = { "Token", "Secret", "Credential", "Key", "Code" };

    private static readonly Type[] ContractMessages =
    {
        typeof(LicenseUsageReport), typeof(LicenseHolding), typeof(LicenseWindow), typeof(LicenseExtraUsage),
        typeof(LicenseSwitchRequest), typeof(LicenseSwitchResult),
    };

    private static List<Type> LicenseMessageTypes() => typeof(LicenseUsageReport).Assembly.GetTypes()
        .Where(t => t.Namespace == "CcDirector.Gateway.Contracts" && t.Name.StartsWith("License", StringComparison.Ordinal)
                    && t.IsClass && !t.IsAbstract)
        .ToList();

    [Fact]
    public void LicenseTypes_FoundByTheScan_IncludeEveryContractMessage()
    {
        // The rule below passes on an ABSENCE of bad names, so prove the scan actually sees the six messages.
        var found = LicenseMessageTypes();

        foreach (var type in ContractMessages)
            Assert.Contains(type, found);
    }

    [Fact]
    public void LicenseTypes_NoPropertyNamedLikeASignInKey()
    {
        var offenders = LicenseMessageTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, p.Name)))
            .Where(x => x.Name != nameof(LicenseSwitchResult.ReasonCode)
                        && ForbiddenWords.Any(w => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Type.Name}.{x.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void LicenseTypes_ScanCountsProperties()
    {
        // Presence check for the scan above: it read real properties, not an empty list.
        var count = LicenseMessageTypes().Sum(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length);

        Assert.True(count >= 35, $"expected the License messages to have at least 35 properties, the scan saw {count}");
    }

    [Fact]
    public void SwitchRequest_WebJson_UsesTheContractNames()
    {
        var request = new LicenseSwitchRequest
        {
            SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
            TargetAccountEmail = "account-b@example.com",
            ExpectedActiveAccountEmail = "account-a@example.com",
            Reason = LicenseSwitchReasons.Threshold,
            RequestedAtUtc = new DateTime(2026, 10, 11, 3, 12, 30, DateTimeKind.Utc),
            NotAfterUtc = new DateTime(2026, 10, 11, 3, 22, 30, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"switchId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\"", json);
        Assert.Contains("\"expectedActiveAccountEmail\":\"account-a@example.com\"", json);
        Assert.Contains("\"notAfterUtc\":\"2026-10-11T03:22:30Z\"", json);
    }

    [Theory]
    [InlineData("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", true)]
    [InlineData("6F1C2A9E4B7D4E0F9A3C5B8D2E1F7A40", false)]
    [InlineData("6f1c2a9e-4b7d-4e0f-9a3c-5b8d2e1f7a40", false)]
    [InlineData("../../etc", false)]
    [InlineData("", false)]
    public void SwitchIdShape_OnlyGuidN_Matches(string switchId, bool expected)
        => Assert.Equal(expected, LicenseContract.SwitchIdShape.IsMatch(switchId));

    [Fact]
    public void Constants_MatchContractSectionSeven()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), LicenseContract.ReportInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), LicenseContract.SpareReadInterval);
        Assert.Equal(TimeSpan.FromMinutes(6), LicenseContract.ActiveHoldingStaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(30), LicenseContract.SpareHoldingStaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(6), LicenseContract.MachineStaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(60), LicenseContract.SwitchCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), LicenseContract.SwitchNotAfter);
        Assert.Equal(TimeSpan.FromHours(24), LicenseContract.SwitchResultRetention);
        Assert.Equal(TimeSpan.FromMinutes(2), LicenseContract.ClockSkewLoggedAbove);
        Assert.Equal("PushLicenseUsage", LicenseContract.PushUsageHubMethod);
        Assert.Equal("license-switch", LicenseContract.SwitchVerb);
    }
}
