using System.Reflection;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Contracts;

/// <summary>
/// License Switching contract rule 6.1: the Gateway never receives or stores a sign-in key, so no property of a
/// License* message type may be named for one. This lives in Core.UnitTests rather than a contracts test project
/// because there is none, and this suite runs in the default local gate (Core references the contracts).
///
/// Presence, not absence (skill checks-that-fail-open): the scan first proves it found every message type the
/// contract names, so a scan that read nothing cannot pass.
/// </summary>
public sealed class LicenseMessagesTests
{
    private static readonly string[] ForbiddenWords = { "Token", "Secret", "Credential", "Key", "Code" };
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "ReasonCode" };

    private static readonly Type[] MessageTypes =
    {
        typeof(LicenseUsageReport), typeof(LicenseHolding), typeof(LicenseWindow), typeof(LicenseExtraUsage),
        typeof(LicenseSwitchRequest), typeof(LicenseSwitchResult),
    };

    [Fact]
    public void LicenseTypes_NoPropertyNamedForASignInKey()
    {
        var licenseTypes = typeof(LicenseUsageReport).Assembly.GetExportedTypes()
            .Where(t => t.Name.StartsWith("License", StringComparison.Ordinal))
            .ToList();

        foreach (var expected in MessageTypes)
            Assert.Contains(expected, licenseTypes);

        var properties = licenseTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .ToList();
        Assert.Contains(properties, p => p.Name == "ReasonCode");
        Assert.True(properties.Count >= 30, $"expected the contract's properties, found {properties.Count}");

        var offenders = properties
            .Where(p => !Allowed.Contains(p.Name))
            .Where(p => ForbiddenWords.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void LicenseContract_Constants_MatchContractSectionSeven()
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

    [Fact]
    public void LicenseSwitchRequest_ContractExampleJson_ReadsWithWebDefaults()
    {
        const string json = """
            {
              "switchId": "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
              "targetAccountEmail": "account-b@example.com",
              "expectedActiveAccountEmail": "account-a@example.com",
              "reason": "threshold",
              "requestedAtUtc": "2026-10-11T03:12:30Z",
              "notAfterUtc": "2026-10-11T03:22:30Z"
            }
            """;

        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Matches(LicenseContract.SwitchIdPattern, request.SwitchId);
        Assert.Equal("account-b@example.com", request.TargetAccountEmail);
        Assert.Equal(LicenseSwitchReasons.Threshold, request.Reason);
        Assert.Equal(LicenseContract.SwitchNotAfter, request.NotAfterUtc - request.RequestedAtUtc);
        Assert.Equal(DateTimeKind.Utc, request.NotAfterUtc.Kind);
    }

    [Fact]
    public void SwitchIdPattern_UpperCaseOrDashedGuid_DoesNotMatch()
    {
        Assert.DoesNotMatch(LicenseContract.SwitchIdPattern, "6F1C2A9E4B7D4E0F9A3C5B8D2E1F7A40");
        Assert.DoesNotMatch(LicenseContract.SwitchIdPattern, "6f1c2a9e-4b7d-4e0f-9a3c-5b8d2e1f7a40");
        Assert.DoesNotMatch(LicenseContract.SwitchIdPattern, "../../escape");
    }
}
