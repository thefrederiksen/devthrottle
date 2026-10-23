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
        typeof(LicenseApprovalRequest), typeof(LicenseApproveRequest), typeof(LicenseApprovalResult),
        typeof(LicenseSignInRequest), typeof(LicenseSignInResult),
    };

    // Every member that could carry a value: static and instance properties of any visibility, and instance fields.
    // Static fields are left out on purpose - they are the vocabulary words and numbers (LicenseSwitchReasonCodes.NotKeyHolder),
    // which are never sent as a member of a message. Each is read one type at a time (DeclaredOnly) so that MemberNames can
    // walk the base types and interfaces itself: GetProperties without DeclaredOnly still hides a base type's private members.
    private const BindingFlags EveryProperty =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private const BindingFlags InstanceFields =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private const string ContractsNamespace = "CcDirector.Gateway.Contracts";

    /// <summary>The types the rule is about: every License* type in the contracts assembly.</summary>
    private static List<Type> LicenseTypes() =>
        LicenseTypes(typeof(LicenseUsageReport).Assembly.GetTypes(), ContractsNamespace);

    /// <summary>
    /// Every License* type among the candidates in the given namespace, whatever its declaration form - class, struct,
    /// record, abstract, static or interface - and every type nested inside one. Nothing is filtered by kind, so a new
    /// value type cannot slip past the rule the way a sealed-class-only scan let it. It takes the candidates as a
    /// parameter so the probe test below goes through this same discovery: a kind filter added here turns that test red.
    /// </summary>
    private static List<Type> LicenseTypes(IEnumerable<Type> candidates, string ns) => candidates
        .Where(t => t.Namespace == ns && IsOrIsInsideLicenseType(t))
        .ToList();

    private static bool IsOrIsInsideLicenseType(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (t.Name.StartsWith("License", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// The names of every property and instance field the type carries, its own and inherited: each base type is walked,
    /// whatever its name, and so is every interface it implements or extends. A License* message whose forbidden member
    /// comes from a base type named anything else is still reported under the message's own name.
    /// </summary>
    private static IEnumerable<(Type Type, string Name)> MemberNames(Type type) =>
        SelfAndAncestors(type)
            .SelectMany(t => t.GetProperties(EveryProperty).Select(p => p.Name)
                .Concat(t.GetFields(InstanceFields)
                    .Where(f => !f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
                    .Select(f => f.Name)))
            .Distinct()
            .Select(name => (type, name));

    private static IEnumerable<Type> SelfAndAncestors(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            yield return t;
        foreach (var i in type.GetInterfaces())
            yield return i;
    }

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
    public void SealedApproval_IsTheOneMemberThatCarriesAnythingFromASignIn_ByName()
    {
        // Contract 8.3, the one stated exception to rule 6.1: the answer to a sign-in approved on another machine
        // travels SEALED to the waiting machine's public half, in LicenseApprovalResult.SealedApproval and nowhere
        // else. Any other member named as sealed, or as an approval's content, would be a second place for it.
        var carriers = LicenseTypes()
            .SelectMany(MemberNames)
            .Where(x => x.Name.Contains("Sealed", StringComparison.OrdinalIgnoreCase)
                        || x.Name.Equals("Approval", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Type.Name}.{x.Name}")
            .ToList();

        Assert.Equal(new[] { $"{nameof(LicenseApprovalResult)}.{nameof(LicenseApprovalResult.SealedApproval)}" }, carriers);
        // What the approving machine is told to seal TO is a public half; it is named for what it is.
        Assert.NotNull(typeof(LicenseApproveRequest).GetProperty(nameof(LicenseApproveRequest.SealToRecipient)));
    }

    [Fact]
    public void SignInMessages_WebJson_UseTheContractNames()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var notAfter = new DateTime(2026, 10, 12, 3, 22, 30, DateTimeKind.Utc);

        var request = JsonSerializer.Serialize(new LicenseApprovalRequest
        {
            SignInId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", AccountEmail = "account-b@example.com", AuthorizeUrl = "https://claude.ai/oauth/authorize", NotAfterUtc = notAfter,
        }, web);
        var approve = JsonSerializer.Serialize(new LicenseApproveRequest { SignInId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", BrowserProfile = "P", SealToRecipient = "R" }, web);
        var result = JsonSerializer.Serialize(new LicenseApprovalResult { Outcome = LicenseApprovalOutcomes.Approved, SealedApproval = "S" }, web);
        var holding = JsonSerializer.Serialize(new LicenseHolding { RenewalFailedAtUtc = notAfter, RenewalError = LicenseRenewalErrors.NoApprover, RenewalErrorDetail = "d" }, web);

        Assert.Contains("\"signInId\":\"6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\"", request);
        Assert.Contains("\"authorizeUrl\":", request);
        Assert.Contains("\"notAfterUtc\":\"2026-10-12T03:22:30Z\"", request);
        Assert.Contains("\"browserProfile\":\"P\"", approve);
        Assert.Contains("\"sealToRecipient\":\"R\"", approve);
        Assert.Contains("\"sealedApproval\":\"S\"", result);
        Assert.Contains("\"renewalFailedAtUtc\":\"2026-10-12T03:22:30Z\"", holding);
        Assert.Contains("\"renewalError\":\"no-approver\"", holding);
        Assert.Contains("\"renewalErrorDetail\":\"d\"", holding);
    }

    [Fact]
    public void RenewalErrors_AreTheContractsEightWords()
    {
        Assert.Equal(
            new[] { "approval-failed", "approver-offline", "browser-not-signed-in", "expired", "login-failed", "no-approver", "store-refused", "wrong-account" },
            LicenseRenewalErrors.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Constants_MatchContractSectionEight()
    {
        Assert.Equal("RequestLicenseApproval", LicenseContract.RequestApprovalHubMethod);
        Assert.Equal("license-approve", LicenseContract.ApproveVerb);
        Assert.Equal("license-approval-result", LicenseContract.ApprovalResultVerb);
        Assert.Equal("license-sign-in", LicenseContract.SignInVerb);
        Assert.Equal("license-approval", LicenseContract.ApprovalSealPurpose);
        Assert.Equal(TimeSpan.FromDays(5), LicenseContract.RenewAhead);
        Assert.Equal(TimeSpan.FromDays(1), LicenseContract.RenewalRunInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), LicenseContract.SignInCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), LicenseContract.ApproveCommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), LicenseContract.ApprovalResultCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), LicenseContract.SignInNotAfter);
        Assert.Equal(TimeSpan.FromHours(24), LicenseContract.SignInResultRetention);
        Assert.Matches(LicenseContract.SignInIdShape, "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40");
        Assert.DoesNotMatch(LicenseContract.SignInIdShape, "../../etc");
    }

    [Fact]
    public void LicenseTypes_ScanCountsProperties()
    {
        // Presence check for the scan above: it read real properties, not an empty list.
        var count = LicenseTypes().Sum(t => t.GetProperties(EveryProperty).Length);

        Assert.True(count >= 37, $"expected the License messages to have at least 37 properties, the scan saw {count}");
    }

    [Fact]
    public void Scan_EveryDeclarationFormAndInheritedMember_IsCaught()
    {
        // The scan's own check, end to end: the probes go through the same discovery as the real types, then through
        // Offenders. Each form the first version missed must be found, and so must the members LicenseProbeInherited
        // only inherits from ProbeBaseWithKey, and the property LicenseProbeInterface only inherits from the interface
        // IProbeWithKey - candidates the discovery itself leaves out, because of their names, so their members can be
        // reported only through the inheritance walk, under the name of the License* type.
        var probes = typeof(LicenseMessagesCarryNoKeyTests).GetNestedTypes(BindingFlags.NonPublic)
            .Append(typeof(ProbeBaseWithKey))
            .Append(typeof(IProbeWithKey));

        var offenders = Offenders(LicenseTypes(probes, typeof(LicenseMessagesCarryNoKeyTests).Namespace!));

        Assert.Equal(
            new[]
            {
                "LicenseProbeAbstract.RefreshToken", "LicenseProbeField.ApiKey", "LicenseProbeInherited.InheritedSecret",
                "LicenseProbeInherited.InheritedToken", "LicenseProbeInterface.InterfaceToken", "LicenseProbeRecord.ClientSecret",
                "LicenseProbeStatic.AuthorizeCode", "LicenseProbeStruct.AccessToken",
            },
            offenders.Order(StringComparer.Ordinal));
    }

    // Probes for Scan_EveryDeclarationFormAndInheritedMember_IsCaught. They live in the test assembly, so the real scan
    // never sees them.
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

    private sealed class LicenseProbeInherited : ProbeBaseWithKey
    {
        public string? Label { get; init; }
    }

    private interface LicenseProbeInterface : IProbeWithKey
    {
        string? Label { get; }
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
    [InlineData("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\n", false)]
    [InlineData("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40\r\n", false)]
    [InlineData("\n6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", false)]
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

// Probe base types for LicenseMessagesCarryNoKeyTests.Scan_EveryDeclarationFormAndInheritedMember_IsCaught. They sit outside
// the test class on purpose: nested inside it, they would count as inside a License* type and be found directly.
internal interface IProbeWithKey
{
    string? InterfaceToken { get; }
}

internal abstract class ProbeBaseWithKey
{
    public string? InheritedToken { get; init; }

#pragma warning disable CS0169 // never used: only its name is read
    private string? InheritedSecret;
#pragma warning restore CS0169
}
