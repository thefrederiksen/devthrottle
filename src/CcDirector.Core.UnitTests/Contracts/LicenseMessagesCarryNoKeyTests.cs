using System.Reflection;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Contracts;

/// <summary>
/// License Switching contract, section 6 rule 1: the Gateway never receives or stores a sign-in key. No property or
/// instance field of any License* type may be named like one. This lives in the Director's unit tests because the contracts project has
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

    // Every member that could carry a value: static and instance properties of any visibility, and instance fields.
    // Static fields are left out on purpose - they are the vocabulary words and numbers (LicenseSwitchReasonCodes.NotKeyHolder),
    // which are never sent as a member of a message.
    private const BindingFlags EveryProperty =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const BindingFlags InstanceFields =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Every License* type in the contracts namespace, whatever its declaration form - class, struct, record, abstract,
    /// static or interface - and every type nested inside one. Nothing is filtered by kind, so a new value type cannot
    /// slip past the rule the way a sealed-class-only scan let it.
    /// </summary>
    private static List<Type> LicenseTypes() => typeof(LicenseUsageReport).Assembly.GetTypes()
        .Where(t => t.Namespace == "CcDirector.Gateway.Contracts" && IsOrIsInsideLicenseType(t))
        .ToList();

    private static bool IsOrIsInsideLicenseType(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (t.Name.StartsWith("License", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static IEnumerable<(Type Type, string Name)> MemberNames(Type type) =>
        type.GetProperties(EveryProperty).Select(p => (type, p.Name))
            .Concat(type.GetFields(InstanceFields).Where(f => !f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
                .Select(f => (type, f.Name)));

    private static List<string> Offenders(IEnumerable<Type> types) => types
        .SelectMany(MemberNames)
        .Where(x => x.Name != nameof(LicenseSwitchResult.ReasonCode)
                    && ForbiddenWords.Any(w => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        .Select(x => $"{x.Type.Name}.{x.Name}")
        .ToList();

    [Fact]
    public void LicenseTypes_FoundByTheScan_IncludeEveryContractMessage()
    {
        // The rule below passes on an ABSENCE of bad names, so prove the scan actually sees the six messages.
        var found = LicenseTypes();

        foreach (var type in ContractMessages)
            Assert.Contains(type, found);
    }

    [Fact]
    public void LicenseTypes_FoundByTheScan_IncludeStaticTypes()
    {
        // Presence check for the declaration forms the first scan skipped: the static classes are now inspected too.
        var found = LicenseTypes();

        Assert.Contains(typeof(LicenseContract), found);
        Assert.Contains(typeof(LicenseSwitchReasonCodes), found);
    }

    [Fact]
    public void LicenseTypes_NoMemberNamedLikeASignInKey()
    {
        Assert.Empty(Offenders(LicenseTypes()));
    }

    [Fact]
    public void LicenseTypes_ScanCountsProperties()
    {
        // Presence check for the scan above: it read real properties, not an empty list.
        var count = LicenseTypes().Sum(t => t.GetProperties(EveryProperty).Length);

        Assert.True(count >= 37, $"expected the License messages to have at least 37 properties, the scan saw {count}");
    }

    [Fact]
    public void Offenders_EveryDeclarationForm_IsCaught()
    {
        // The scan's own check: each form the first version missed is caught when handed to it.
        var offenders = Offenders(new[]
        {
            typeof(LicenseProbeStruct), typeof(LicenseProbeAbstract), typeof(LicenseProbeRecord),
            typeof(LicenseProbeStatic), typeof(LicenseProbeField),
        });

        Assert.Equal(
            new[]
            {
                "LicenseProbeStruct.AccessToken", "LicenseProbeAbstract.RefreshToken", "LicenseProbeRecord.ClientSecret",
                "LicenseProbeStatic.AuthorizeCode", "LicenseProbeField.ApiKey",
            },
            offenders);
    }

    // Probes for Offenders_EveryDeclarationForm_IsCaught. They live in the test assembly, so the real scan never sees them.
    private readonly struct LicenseProbeStruct
    {
        public string? AccessToken { get; init; }
    }

    private abstract class LicenseProbeAbstract
    {
        public abstract string? RefreshToken { get; set; }
    }

    private sealed record LicenseProbeRecord(string ClientSecret);

    private static class LicenseProbeStatic
    {
        public static string? AuthorizeCode { get; set; }
    }

    private sealed class LicenseProbeField
    {
#pragma warning disable CS0649 // never assigned: only its name is read
        public string? ApiKey;
#pragma warning restore CS0649
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
        Assert.Equal(TimeSpan.FromDays(3), LicenseContract.SignInExpiryWarning);
        Assert.Equal("PushLicenseUsage", LicenseContract.PushUsageHubMethod);
        Assert.Equal("license-switch", LicenseContract.SwitchVerb);
    }
}
