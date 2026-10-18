using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// WHY AN ACCOUNT'S APPROVER CANNOT APPROVE, IN PLAIN ENGLISH (License Switching phase 6, Track C). Every sentence is
/// pinned exactly, because the Cockpit and Track A's readiness show it verbatim: it names the account, the machine and
/// the browser profile, and says the one thing to do. And the not-checked rule: a record never checked, or checked
/// more than 26 hours ago, is never read as healthy.
/// </summary>
public sealed class LicenseApproverReadinessTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private const string Alice = "alice@example.com";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _licenses;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseApproverReadiness _readiness;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseApproverReadinessTests()
    {
        var db = _harness.Open();
        _licenses = new LicenseStore(db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(db));
        _readiness = new LicenseApproverReadiness(_approvers, new LicenseReadModel(_licenses), () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private static LicenseApprover Approver(string? state, DateTime? checkedAt, string? shown = null, string? detail = null)
        => new(Alice, "machine-a", "owner", "work", LicenseApproverSources.Registered, state, checkedAt, shown, detail);

    private static string? Sentence(LicenseApprover? approver) => LicenseApproverReadiness.Sentence(approver, Alice, "machine-a", T0);

    // ----- every sentence, exactly -----

    [Fact]
    public void Sentence_NoApprover_SaysNoMachineIsSet_AndWhereToSetOne()
    {
        Assert.Equal("No machine is set to approve sign-ins for alice@example.com. Set an approver for alice@example.com on the Licenses page.",
            Sentence(null));
    }

    [Fact]
    public void Sentence_CanApprove_IsNull()
    {
        Assert.Null(Sentence(Approver(LicenseApproverStates.CanApprove, T0.AddHours(-2), Alice)));
    }

    [Fact]
    public void Sentence_SignedOut_SaysSoAndToSignInAsTheAccount()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) is not signed in to claude.ai. "
                     + "Open that browser on machine-a and sign in to claude.ai as alice@example.com once.",
            Sentence(Approver(LicenseApproverStates.SignedOut, T0)));
    }

    [Fact]
    public void Sentence_NeedsFreshSignIn_IsTheDesignsSentence()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) needs a fresh claude.ai sign-in on the approval page. "
                     + "Open that browser on machine-a and sign in to claude.ai once.",
            Sentence(Approver(LicenseApproverStates.NeedsFreshSignIn, T0)));
    }

    [Fact]
    public void Sentence_WrongAccount_NamesWhoItIsSignedInAs()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) is signed in to claude.ai as bob@example.com, "
                     + "not as alice@example.com. Open that browser on machine-a, sign out of claude.ai and sign in as alice@example.com once.",
            Sentence(Approver(LicenseApproverStates.WrongAccount, T0, shown: "bob@example.com")));
    }

    [Fact]
    public void Sentence_WrongAccount_WithNobodyShown_SaysAnotherAccount()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) is signed in to claude.ai as another account, "
                     + "not as alice@example.com. Open that browser on machine-a, sign out of claude.ai and sign in as alice@example.com once.",
            Sentence(Approver(LicenseApproverStates.WrongAccount, T0)));
    }

    [Theory]
    [InlineData("The browser profile could not be opened.", ": The browser profile could not be opened.")]
    [InlineData("The browser profile could not be opened", ": The browser profile could not be opened.")]
    [InlineData(null, ".")]
    public void Sentence_CheckFailed_GivesTheDirectorsDetail_AndWhatToCheck(string? detail, string said)
    {
        Assert.Equal($"The approver for alice@example.com (browser profile \"work\" on machine-a) could not be checked{said} "
                     + "Make sure the browser profile \"work\" opens on machine-a; the check runs again within a day.",
            Sentence(Approver(LicenseApproverStates.CheckFailed, T0, detail: detail)));
    }

    [Fact]
    public void Sentence_NeverChecked_SaysSo_AndThatTheMachinesDirectorMustBeRunning()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) has never been checked. "
                     + "Make sure the Director on machine-a is running with license switching on; it checks its approvers once a day.",
            Sentence(Approver(null, null)));
    }

    [Fact]
    public void Sentence_LastCheckedOver26HoursAgo_SaysWhen_AndThatTheMachinesDirectorMustBeRunning()
    {
        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a) was last checked 2026-10-10 01:11 UTC, "
                     + "more than 26 hours ago. Make sure the Director on machine-a is running with license switching on; it checks its approvers once a day.",
            Sentence(Approver(LicenseApproverStates.CanApprove, T0 - LicenseApproverContract.NotCheckedAfter - TimeSpan.FromMinutes(1), Alice)));
    }

    [Fact]
    public void Verdict_CanApprove_SaysWhenItWasChecked_AndHasNothingToDo()
    {
        var verdict = LicenseApproverReadiness.Verdict(Approver(LicenseApproverStates.CanApprove, T0.AddHours(-2), Alice), "machine-a", T0);

        Assert.Equal(new LicenseApproverHealthVerdict(LicenseApproverStates.CanApprove,
            "The approver for alice@example.com (browser profile \"work\" on machine-a) can approve sign-ins; it was last checked 2026-10-11 01:12 UTC.",
            null), verdict);
    }

    [Fact]
    public void Verdict_SaysTheDisplayNameTheLastCheckSaw_InPlaceOfTheIdentifier()
    {
        var approver = Approver(LicenseApproverStates.NeedsFreshSignIn, T0) with { BrowserProfile = "work-chrome", BrowserProfileName = "Work Chrome" };

        var verdict = LicenseApproverReadiness.Verdict(approver, "machine-a", T0);

        Assert.Equal("The approver for alice@example.com (browser profile \"Work Chrome\" on machine-a) needs a fresh claude.ai sign-in on the approval page.",
            verdict.HealthText);
        Assert.DoesNotContain("work-chrome", verdict.HealthText + verdict.WhatToDo);
    }

    [Fact]
    public void Verdict_AStateThatIsNotAStateWord_IsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => LicenseApproverReadiness.Verdict(Approver("fine", T0), "machine-a", T0));
    }

    // ----- WhyNotReady, over the stores -----

    [Fact]
    public void WhyNotReady_ReadsTheStoredRecord_AndCallsTheMachineWhatThePageCallsIt()
    {
        // Two machines of one name: the approver's is told apart by its user, as on the Licenses page.
        var guest = Report("machine-a", _now, Holding(Alice, true));
        guest.OsUser = "guest";
        _licenses.ApplyReport(One, "dir-guest", ++_sequence, guest, _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, "dir-a", ++_sequence, Report("machine-a", _now), _now, TimeSpan.Zero);
        Assert.True(_approvers.Set(One, Alice, "machine-a", "owner", "work", _now).Accepted);

        Assert.Equal("The approver for alice@example.com (browser profile \"work\" on machine-a (owner)) has never been checked. "
                     + "Make sure the Director on machine-a (owner) is running with license switching on; it checks its approvers once a day.",
            _readiness.WhyNotReady(One, "ALICE@example.com"));
    }

    [Fact]
    public void WhyNotReady_ForAHealthyApprover_IsNull_UntilItsCheckIsOver26HoursOld()
    {
        _approvers.Change(One, _ => ((IReadOnlyList<LicenseApprover>?)new[] { Approver(LicenseApproverStates.CanApprove, T0, Alice) }, 0), T0);

        _now = T0 + LicenseApproverContract.NotCheckedAfter;
        Assert.Null(_readiness.WhyNotReady(One, Alice));

        _now = T0 + LicenseApproverContract.NotCheckedAfter + TimeSpan.FromSeconds(1);
        Assert.StartsWith("The approver for alice@example.com (browser profile \"work\" on machine-a) was last checked", _readiness.WhyNotReady(One, Alice));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void WhyNotReady_ForAMissingOrNonEmailAccount_IsRefused_NeverASentenceWithAHole(string? account)
    {
        var refused = Assert.Throws<ArgumentException>(() => _readiness.WhyNotReady(One, account));

        Assert.Equal("accountEmail", refused.ParamName);
    }

    [Fact]
    public void WhyNotReady_NeverReadsAnotherTenantsApprover()
    {
        _approvers.Change(One, _ => ((IReadOnlyList<LicenseApprover>?)new[] { Approver(LicenseApproverStates.CanApprove, T0, Alice) }, 0), T0);

        Assert.Null(_readiness.WhyNotReady(One, Alice));
        Assert.Equal("No machine is set to approve sign-ins for alice@example.com. Set an approver for alice@example.com on the Licenses page.",
            _readiness.WhyNotReady(Two, Alice));
    }
}
