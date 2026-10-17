using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// STANDING APPROVERS (License Switching phase 6, Track C): what the Gateway keeps of a Director's approver checks, over
/// the real per-tenant settings and the real Licenses store on a throwaway SQLite file. What these hold down: who is
/// asking comes from the Gateway's own record of which machine a Director reports for; a registration is only for an
/// account the tenant's machines hold, from a machine that offers its profiles; the owner's record is never overwritten
/// and a registration the owner removed stays removed until the owner sets that account again; a healthy registered
/// incumbent is kept; a report or a row that is not what it claims to be stores nothing; a health row updates only the
/// record that names exactly the caller's machine, user and profile; and nothing the Director sent reaches the log
/// unchecked.
/// </summary>
// It captures the process-wide log in one test, so no other test class runs beside it.
[Collection(FileLogCaptureCollection.Name)]
public sealed class LicenseApproverRegistrationTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");

    private const string MachineA = "machine-a";
    private const string MachineB = "machine-b";
    private const string DirectorA = "dir-a";
    private const string DirectorB = "dir-b";
    private const string Work = "work";
    private const string Personal = "personal";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _licenses;
    private readonly TenantSettingsStore _settings;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseApproverRegistration _registration;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseApproverRegistrationTests()
    {
        var db = _harness.Open();
        _licenses = new LicenseStore(db);
        _settings = new TenantSettingsStore(db);
        _approvers = new LicenseApproverStore(_settings);
        _registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now);

        // Tenant one as most tests want it: machine A holds accounts A and B, machine B holds account C.
        Reports(One, MachineA, DirectorA, Holding(A, active: true), Holding(B, active: false));
        Reports(One, MachineB, DirectorB, Holding(C, active: true));
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    private void Reports(TenantId tenant, string machine, string director, params LicenseHolding[] holdings)
        => _licenses.ApplyReport(tenant, director, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero);

    private LicenseApproverCheckRow Row(string profile = Work, string? account = A, string state = LicenseApproverStates.CanApprove,
        string? shown = "same", DateTime? checkedAt = null, string? detail = null) => new()
    {
        BrowserProfile = profile,
        AccountEmail = account,
        ShownAccount = shown == "same" ? account : shown,
        State = state,
        CheckedAtUtc = checkedAt ?? _now,
        Detail = detail,
    };

    private LicenseApproverCheckAnswer Check(string director, params LicenseApproverCheckRow[] rows) => Check(One, director, true, rows);

    private LicenseApproverCheckAnswer Check(TenantId tenant, string director, bool offers, params LicenseApproverCheckRow[] rows)
        => _registration.Report(tenant, director, new LicenseApproverCheckReport { OffersApprovers = offers, Rows = rows.ToList() });

    private static void AssertRow(LicenseApproverCheckAnswer answer, string outcome, string? reason = null)
    {
        var row = Assert.Single(answer.Rows);
        Assert.Equal(outcome, row.Outcome);
        Assert.Equal(reason, row.Reason);
    }

    private void AssertNothingStored(TenantId? tenant = null) => Assert.Null(_settings.Get(tenant ?? One, TenantSettingKeys.LicenseApprovers));

    // ----- registering -----

    [Fact]
    public void Report_CanApproveForAnAccountTheTenantHolds_RegistersTheProfile_OnTheMachineAndUserTheGatewayHasForTheDirector()
    {
        var answer = Check(DirectorA, Row(detail: "The approval page is ready."));

        AssertRow(answer, LicenseApproverCheckOutcomes.Registered);
        var stored = _approvers.Find(One, A);
        Assert.Equal(new LicenseApprover(A, MachineA, "owner", Work, LicenseApproverSources.Registered,
            LicenseApproverStates.CanApprove, _now, A, "The approval page is ready."), stored);
        Assert.Equal(LicenseApproverStates.CanApprove, LicenseApproverHealth.StateOf(stored!, _now));
    }

    [Fact]
    public void Report_FromADirectorThatReportsForNoMachineOfTheTenant_RefusesEveryRow_AndStoresNothing()
    {
        var answer = Check("dir-stranger", Row(), Row(profile: Personal, account: B));

        Assert.Equal(2, answer.Rows.Count);
        Assert.All(answer.Rows, r =>
        {
            Assert.Equal(LicenseApproverCheckOutcomes.Refused, r.Outcome);
            Assert.Equal(LicenseApproverRefusals.NotTheReportingDirector, r.Reason);
        });
        AssertNothingStored();
    }

    [Fact]
    public void Report_FromADirectorThatNoLongerReportsForItsMachine_IsRefused_TheDirectorThatReportsNowIsTheOne()
    {
        // Another Director now reports for machine A: it is the one that may register for it.
        _now = T0.AddMinutes(5);
        Reports(One, MachineA, "dir-a-new", Holding(A, active: true));

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.NotTheReportingDirector);
        AssertNothingStored();
        AssertRow(Check("dir-a-new", Row()), LicenseApproverCheckOutcomes.Registered);
        Assert.Equal(MachineA, _approvers.Find(One, A)!.MachineName);
    }

    [Fact]
    public void Report_FromTheSameDirectorIdInAnotherTenant_ReachesNothingOfThisTenant()
    {
        // Tenant two has no machine reported by dir-a, so the same identifier there registers nothing, anywhere.
        AssertRow(Check(Two, DirectorA, true, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.NotTheReportingDirector);

        AssertNothingStored(One);
        AssertNothingStored(Two);
    }

    [Fact]
    public void Report_ForAnAccountNoMachineOfTheTenantHolds_IsRefused_AndStoresNothing()
    {
        AssertRow(Check(DirectorA, Row(account: "someone-else@example.com")),
            LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.AccountNotHeld);

        AssertNothingStored();
    }

    [Fact]
    public void Report_ForAnAccountHeldOnlyByAnotherMachineOfTheTenant_Registers()
    {
        // Account C is held by machine B; machine A's browser may still be the one that approves it.
        AssertRow(Check(DirectorA, Row(account: C)), LicenseApproverCheckOutcomes.Registered);

        Assert.Equal(MachineA, _approvers.Find(One, C)!.MachineName);
    }

    [Fact]
    public void Report_FromAMachineThatDoesNotOfferItsProfiles_RegistersNothing()
    {
        AssertRow(Check(One, DirectorA, false, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.NotOffered);

        AssertNothingStored();
    }

    [Fact]
    public void Report_FromAMachineThatDoesNotOffer_StillUpdatesTheHealthOfARecordTheOwnerSetOnIt()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", Work, _now).Accepted);

        AssertRow(Check(One, DirectorA, false, Row(state: LicenseApproverStates.NeedsFreshSignIn, shown: null)),
            LicenseApproverCheckOutcomes.HealthUpdated);

        var stored = _approvers.Find(One, A)!;
        Assert.Equal(LicenseApproverSources.Owner, stored.Source);
        Assert.Equal(LicenseApproverStates.NeedsFreshSignIn, LicenseApproverHealth.StateOf(stored, _now));
    }

    // ----- the owner keeps the last word -----

    [Fact]
    public void Report_ForAnAccountWhoseApproverTheOwnerSet_NeverOverwritesIt()
    {
        Assert.True(_approvers.Set(One, A, MachineB, "owner", Personal, _now).Accepted);

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.OwnerSet);

        Assert.Equal(new LicenseApprover(A, MachineB, "owner", Personal), _approvers.Find(One, A));
    }

    [Fact]
    public void Report_ForAnOwnerSetRecordThatHasNotBeenCheckedFor26Hours_StillNeverOverwritesIt()
    {
        Assert.True(_approvers.Set(One, A, MachineB, "owner", Personal, _now).Accepted);
        _now = T0.AddDays(3);

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.OwnerSet);
        Assert.Equal(MachineB, _approvers.Find(One, A)!.MachineName);
    }

    [Fact]
    public void Report_ForARegistrationTheOwnerRemoved_IsRefused_UntilTheOwnerSetsAnApproverForThatAccountAgain()
    {
        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);
        Assert.True(_approvers.Remove(One, A, _now));

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.OwnerRemoved);
        Assert.Null(_approvers.Find(One, A));

        // The owner sets the account's approver, then takes it off again: the removal is gone with the setting.
        Assert.True(_approvers.Set(One, A, MachineB, "owner", Personal, _now).Accepted);
        Assert.True(_approvers.Remove(One, A, _now));
        Assert.Empty(_approvers.ListRemovals(One));

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);
    }

    [Fact]
    public void Report_TheOwnersRemovalBindsOnlyThatProfile_AnotherProfileOfTheMachineMayRegister()
    {
        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);
        Assert.True(_approvers.Remove(One, A, _now));

        AssertRow(Check(DirectorA, Row(profile: Personal)), LicenseApproverCheckOutcomes.Registered);
        Assert.Equal(Personal, _approvers.Find(One, A)!.BrowserProfile);
    }

    // ----- one approver per account -----

    [Fact]
    public void Report_AgainstARegisteredIncumbentThatCanApprove_KeepsTheIncumbent()
    {
        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);

        AssertRow(Check(DirectorB, Row(profile: Personal)), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.HealthyIncumbent);

        Assert.Equal(MachineA, _approvers.Find(One, A)!.MachineName);
    }

    [Fact]
    public void Report_AgainstARegisteredIncumbentWhoseLastCheckSaysItCannotApprove_ReplacesIt()
    {
        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);
        AssertRow(Check(DirectorA, Row(state: LicenseApproverStates.SignedOut, shown: null)), LicenseApproverCheckOutcomes.HealthUpdated);

        AssertRow(Check(DirectorB, Row(profile: Personal)), LicenseApproverCheckOutcomes.Registered);

        var stored = _approvers.Find(One, A)!;
        Assert.Equal((MachineB, Personal, LicenseApproverStates.CanApprove), (stored.MachineName, stored.BrowserProfile, stored.State));
    }

    [Fact]
    public void Report_AgainstARegisteredIncumbentCheckedExactly26HoursAgo_KeepsIt_AndOneMinuteLaterReplacesIt()
    {
        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Registered);

        _now = T0 + LicenseApproverContract.NotCheckedAfter;
        AssertRow(Check(DirectorB, Row(profile: Personal)), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.HealthyIncumbent);

        _now = T0 + LicenseApproverContract.NotCheckedAfter + TimeSpan.FromMinutes(1);
        AssertRow(Check(DirectorB, Row(profile: Personal)), LicenseApproverCheckOutcomes.Registered);
        Assert.Equal(MachineB, _approvers.Find(One, A)!.MachineName);
    }

    [Fact]
    public void Report_ANewRegistrationWhenTheTenantKeepsTheMostApprovers_IsRefused()
    {
        for (var i = 0; i < LicenseApproverStore.MaxApprovers; i++)
            Assert.True(_approvers.Set(One, $"other-{i}@example.com", MachineB, "owner", Work, _now).Accepted);

        AssertRow(Check(DirectorA, Row()), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.TooManyApprovers);

        Assert.Null(_approvers.Find(One, A));
    }

    // ----- health rows -----

    [Fact]
    public void Report_AHealthRowForARecordNamingTheCallersMachineAndProfile_UpdatesItsHealth_WhoeverMadeIt()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", Work, _now).Accepted);
        _now = T0.AddHours(1);

        AssertRow(Check(DirectorA, Row(state: LicenseApproverStates.WrongAccount, shown: "someone-else@example.com",
            detail: "The approval page is signed in as another account.")), LicenseApproverCheckOutcomes.HealthUpdated);

        Assert.Equal(new LicenseApprover(A, MachineA, "owner", Work, LicenseApproverSources.Owner, LicenseApproverStates.WrongAccount, _now,
            "someone-else@example.com", "The approval page is signed in as another account."), _approvers.Find(One, A));
    }

    [Fact]
    public void Report_AHealthRowForARecordNamingAnotherMachine_IsRefused_AndTheRecordIsUnchanged()
    {
        Assert.True(_approvers.Set(One, A, MachineB, "owner", Work, _now).Accepted);

        AssertRow(Check(DirectorA, Row(state: LicenseApproverStates.SignedOut, shown: null)),
            LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.RecordNamesAnotherApprover);

        Assert.Null(_approvers.Find(One, A)!.State);
    }

    [Fact]
    public void Report_AHealthRowForAnotherProfileOfTheSameMachine_IsRefused()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", Work, _now).Accepted);

        AssertRow(Check(DirectorA, Row(profile: Personal, state: LicenseApproverStates.SignedOut, shown: null)),
            LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.RecordNamesAnotherApprover);

        Assert.Null(_approvers.Find(One, A)!.State);
    }

    [Fact]
    public void Report_ARowThatProvesNothingForAnAccountWithNoRecord_StoresNothing()
    {
        AssertRow(Check(DirectorA, Row(state: LicenseApproverStates.SignedOut, shown: null)), LicenseApproverCheckOutcomes.NothingToStore);
        AssertRow(Check(DirectorA, Row(account: null, state: LicenseApproverStates.SignedOut, shown: null)), LicenseApproverCheckOutcomes.NothingToStore);

        AssertNothingStored();
    }

    // ----- what a report may be -----

    [Fact]
    public void Report_WithMoreRowsThanAllowed_IsRefusedWhole_AndStoresNothing()
    {
        var rows = Enumerable.Range(0, LicenseApproverContract.MaxCheckRows + 1).Select(_ => Row()).ToArray();

        var refused = Assert.Throws<ArgumentException>(() => Check(DirectorA, rows));

        Assert.StartsWith($"A report carries at most {LicenseApproverContract.MaxCheckRows} rows.", refused.Message);
        AssertNothingStored();
    }

    [Fact]
    public void Report_WithExactlyTheMostRowsAllowed_IsTaken()
    {
        var rows = Enumerable.Range(0, LicenseApproverContract.MaxCheckRows).Select(_ => Row()).ToArray();

        var answer = Check(DirectorA, rows);

        Assert.Equal(LicenseApproverContract.MaxCheckRows, answer.Rows.Count);
        Assert.Equal(LicenseApproverCheckOutcomes.Registered, answer.Rows[0].Outcome);
        // The same profile again is the record's own check from then on.
        Assert.All(answer.Rows.Skip(1), r => Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, r.Outcome));
    }

    [Fact]
    public void Report_MissingOrWithNoRows_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => _registration.Report(One, DirectorA, null));
        Assert.Throws<ArgumentException>(() => _registration.Report(One, DirectorA, new LicenseApproverCheckReport { Rows = null! }));
        AssertNothingStored();
    }

    public static TheoryData<string> RowsOfTheWrongShape() => new()
    {
        "no profile", "profile of the wrong shape", "profile too long", "profile with a space around it", "account not an email", "account too long",
        "shown account not an email", "unknown state", "not-checked reported", "checked in the future", "checked over 26 hours ago",
        "detail too long", "detail with a line break", "can approve with no account", "can approve showing another account", "wrong account showing the account itself", "null row",
    };

    [Theory]
    [MemberData(nameof(RowsOfTheWrongShape))]
    public void Report_ARowOfTheWrongShape_IsRefusedWhole_AndStoresNothing(string what)
    {
        var row = what switch
        {
            "no profile" => Row(profile: null!),
            "profile of the wrong shape" => Row(profile: "work; rm -rf"),
            "profile too long" => Row(profile: new string('w', 65)),
            "profile with a space around it" => Row(profile: " work "),
            "account not an email" => Row(account: "not-an-email"),
            "account too long" => Row(account: new string('a', 400) + "@example.com"),
            "shown account not an email" => Row(shown: "a@b@c"),
            "unknown state" => Row(state: "fine"),
            "not-checked reported" => Row(state: LicenseApproverStates.NotChecked),
            "checked in the future" => Row(checkedAt: _now + LicenseApproverRegistration.CheckedAtFutureTolerance + TimeSpan.FromSeconds(1)),
            "checked over 26 hours ago" => Row(checkedAt: _now - LicenseApproverContract.NotCheckedAfter - TimeSpan.FromSeconds(1)),
            "detail too long" => Row(detail: new string('d', LicenseApproverContract.MaxDetailLength + 1)),
            "detail with a line break" => Row(detail: "first line\nsecond line"),
            "can approve with no account" => Row(account: null),
            "can approve showing another account" => Row(shown: "someone-else@example.com"),
            "wrong account showing the account itself" => Row(state: LicenseApproverStates.WrongAccount, shown: "same"),
            "null row" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "no such case"),
        };

        var answer = Check(DirectorA, row!);

        AssertRow(answer, LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.InvalidRow);
        Assert.Null(answer.Rows[0].AccountEmail);
        AssertNothingStored();
    }

    [Fact]
    public void Report_ARefusedRow_DoesNotStopTheOtherRowsOfTheSameReport()
    {
        var answer = Check(DirectorA, Row(state: "fine"), Row(account: B, profile: Personal));

        Assert.Equal(LicenseApproverRefusals.InvalidRow, answer.Rows[0].Reason);
        Assert.Equal(LicenseApproverCheckOutcomes.Registered, answer.Rows[1].Outcome);
        Assert.Equal(Personal, _approvers.Find(One, B)!.BrowserProfile);
        Assert.Null(_approvers.Find(One, A));
    }

    [Fact]
    public void Report_AccountsAreComparedLowerCased()
    {
        AssertRow(Check(DirectorA, Row(account: "Account-A@Example.com", shown: "ACCOUNT-A@example.com")), LicenseApproverCheckOutcomes.Registered);

        var stored = _approvers.Find(One, A)!;
        Assert.Equal((A, Work, A), (stored.AccountEmail, stored.BrowserProfile, stored.ShownAccount));
    }

    [Fact]
    public void Report_TheProfileIdentifierIsComparedExactly_ADifferentSpellingIsAnotherProfile()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", "work-chrome", _now).Accepted);

        // "Work-Chrome" is not the identifier the owner set, so it is not this record's check.
        AssertRow(Check(DirectorA, Row(profile: "Work-Chrome", state: LicenseApproverStates.SignedOut, shown: null)),
            LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.RecordNamesAnotherApprover);
        Assert.Null(_approvers.Find(One, A)!.State);
    }

    [Fact]
    public void Report_KeepsTheProfilesDisplayName_AsTheLastNameSeen()
    {
        AssertRow(Check(DirectorA, WithName(Row(profile: "work-chrome"), " Work Chrome ")), LicenseApproverCheckOutcomes.Registered);
        Assert.Equal(("work-chrome", "Work Chrome"), (_approvers.Find(One, A)!.BrowserProfile, _approvers.Find(One, A)!.BrowserProfileName));

        // A later check that names nothing keeps the last name seen; one that names a new name replaces it.
        AssertRow(Check(DirectorA, Row(profile: "work-chrome")), LicenseApproverCheckOutcomes.HealthUpdated);
        Assert.Equal("Work Chrome", _approvers.Find(One, A)!.BrowserProfileName);
        AssertRow(Check(DirectorA, WithName(Row(profile: "work-chrome"), "Office")), LicenseApproverCheckOutcomes.HealthUpdated);
        Assert.Equal("Office", _approvers.Find(One, A)!.BrowserProfileName);
    }

    [Theory]
    [InlineData("a display name that is far too long to be the name of a browser profile at all, past 64")]
    [InlineData("Work; rm -rf")]
    [InlineData(" ")]
    public void Report_ADisplayNameOfTheWrongShape_IsRefusedWhole(string name)
    {
        AssertRow(Check(DirectorA, WithName(Row(), name)), LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.InvalidRow);

        AssertNothingStored();
    }

    private static LicenseApproverCheckRow WithName(LicenseApproverCheckRow row, string name)
    {
        row.BrowserProfileName = name;
        return row;
    }

    // ----- the log -----

    [Fact]
    public void Report_LogsEveryAcceptedAndRefusedRow_WithTheAccountAndTheMachine_AndNeverTextThatWasNotChecked()
    {
        using var capture = new CapturedLog();

        Check(DirectorA, Row(), Row(account: "INJECTED\nnot an email"), Row(account: "someone-else@example.com"));
        var lines = capture.Lines();

        Assert.Contains(lines, l => l.Contains($"director={DirectorA}, machine={MachineA}, account={A}, outcome=registered, reason=-"));
        Assert.Contains(lines, l => l.Contains($"machine={MachineA}, account=-, outcome=refused, reason={LicenseApproverRefusals.InvalidRow}"));
        Assert.Contains(lines, l => l.Contains($"account=someone-else@example.com, outcome=refused, reason={LicenseApproverRefusals.AccountNotHeld}"));
        Assert.DoesNotContain(lines, l => l.Contains("INJECTED"));
    }

    // ----- assignments -----

    [Fact]
    public void Assignments_AreTheRecordsThatNameTheCallersMachineAndUser_OwnerSetAndRegistered_AndNoOthers()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", Work, _now).Accepted);
        AssertRow(Check(DirectorA, Row(account: B, profile: Personal)), LicenseApproverCheckOutcomes.Registered);
        Assert.True(_approvers.Set(One, C, MachineB, "owner", Work, _now).Accepted);

        var rows = _registration.Assignments(One, DirectorA).Rows;

        Assert.Equal(new[] { (A, Work, LicenseApproverSources.Owner), (B, Personal, LicenseApproverSources.Registered) },
            rows.Select(r => (r.AccountEmail, r.BrowserProfile, r.Source)));
    }

    [Fact]
    public void Assignments_ForADirectorThatReportsForNoMachineOfTheTenant_AreEmpty()
    {
        Assert.True(_approvers.Set(One, A, MachineA, "owner", Work, _now).Accepted);

        Assert.Empty(_registration.Assignments(One, "dir-stranger").Rows);
        Assert.Empty(_registration.Assignments(Two, DirectorA).Rows);
    }
}
