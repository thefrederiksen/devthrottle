using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Core.Utilities;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// WHO APPROVES EACH ACCOUNT'S SIGN-INS (License Switching, contract section 8.3), over the real per-tenant settings on
/// a throwaway SQLite file. What these hold down: one approver per account, kept per DevThrottle account; setting again
/// replaces; removing removes; a value that is not what it claims to be is refused and nothing is stored; one account
/// never sees another's; what is stored is exactly the fields of the contract; a record stored before sources existed
/// reads as owner-set and never checked; and the owner's removal of a registered record is kept until the owner sets
/// that account again.
/// </summary>
// It captures the process-wide log, so no other test class runs beside it.
[Collection(FileLogCaptureCollection.Name)]
public sealed class LicenseApproverStoreTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsStore _settings;
    private readonly LicenseApproverStore _store;

    public LicenseApproverStoreTests()
    {
        _settings = new TenantSettingsStore(_harness.Open());
        _store = new LicenseApproverStore(_settings);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Set_ThenFindAndList_ReturnTheApprover_WithTheAccountLowerCasedAndTheNamesTrimmed()
    {
        var set = _store.Set(One, "  Account-A@Example.com ", " the-mac ", " owner ", " work ", T0);

        Assert.True(set.Accepted);
        var expected = new LicenseApprover(A, "the-mac", "owner", "work");
        Assert.Equal(expected, set.Approver);
        Assert.Equal(expected, _store.Find(One, "ACCOUNT-A@example.com"));
        Assert.Equal(new[] { expected }, _store.List(One));
    }

    [Fact]
    public void Set_ForAnAccountThatHasAnApprover_ReplacesIt()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);

        _store.Set(One, A, "the-windows-machine", "owner", "personal", T0);

        Assert.Equal(new[] { new LicenseApprover(A, "the-windows-machine", "owner", "personal") }, _store.List(One));
    }

    [Fact]
    public void List_IsInAccountEmailOrder()
    {
        _store.Set(One, C, "the-mac", "owner", "work", T0);
        _store.Set(One, A, "the-mac", "owner", "work", T0);
        _store.Set(One, B, "the-mac", "owner", "work", T0);

        Assert.Equal(new[] { A, B, C }, _store.List(One).Select(a => a.AccountEmail));
    }

    [Fact]
    public void Remove_RemovesOnlyThatAccountsApprover_AndSaysWhetherThereWasOne()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);
        _store.Set(One, B, "the-mac", "owner", "work", T0);

        Assert.True(_store.Remove(One, A.ToUpperInvariant(), T0));
        Assert.False(_store.Remove(One, A, T0));

        Assert.Null(_store.Find(One, A));
        Assert.Equal(B, Assert.Single(_store.List(One)).AccountEmail);
    }

    [Fact]
    public void Remove_OfTheLastApprover_LeavesNoStoredValueAtAll()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);

        _store.Remove(One, A, T0);

        Assert.Null(_settings.Get(One, TenantSettingKeys.LicenseApprovers));
        Assert.Empty(_store.List(One));
    }

    [Fact]
    public void AnApprover_SetInOneAccount_IsNeverSeenByAnother()
    {
        _store.Set(One, A, "the-mac", "owner", "profile-of-one", T0);
        _store.Set(Two, A, "the-mac", "owner", "profile-of-two", T0);

        Assert.Equal("profile-of-one", _store.Find(One, A)!.BrowserProfile);
        Assert.Equal("profile-of-two", _store.Find(Two, A)!.BrowserProfile);
        Assert.True(_store.Remove(Two, A, T0));
        Assert.Equal("profile-of-one", Assert.Single(_store.List(One)).BrowserProfile);
        Assert.Empty(_store.List(Two));
    }

    [Theory]
    [InlineData("", "the-mac", "owner", "work", "invalid_account")]
    [InlineData("no-at-sign", "the-mac", "owner", "work", "invalid_account")]
    [InlineData("two@at@signs", "the-mac", "owner", "work", "invalid_account")]
    [InlineData("has space@example.com", "the-mac", "owner", "work", "invalid_account")]
    [InlineData(A, "", "owner", "work", "invalid_machine")]
    [InlineData(A, "the-mac", "  ", "work", "invalid_machine")]
    [InlineData(A, "the-mac", "owner", "", "invalid_browser_profile")]
    [InlineData(A, "the-mac", "owner", "work; rm -rf /", "invalid_browser_profile")]
    [InlineData(A, "the-mac", "owner", "--user-data-dir=x", "invalid_browser_profile")]
    [InlineData(A, "the-mac", "owner", "a\"b", "invalid_browser_profile")]
    public void Set_AValueThatIsNotWhatItClaimsToBe_IsRefusedWithASentence_AndNothingIsStored(
        string account, string machine, string user, string profile, string word)
    {
        var set = _store.Set(One, account, machine, user, profile, T0);

        Assert.False(set.Accepted);
        Assert.Equal(word, set.RefusalWord);
        Assert.False(string.IsNullOrWhiteSpace(set.RefusalText));
        Assert.Empty(_store.List(One));
        Assert.Null(_settings.Get(One, TenantSettingKeys.LicenseApprovers));
    }

    public static TheoryData<string, string, string, string, string> ValuesWithACharacterThatIsNotText()
    {
        var data = new TheoryData<string, string, string, string, string>();
        // A line break, a carriage return, a tab inside, a bell, an escape, a delete, a right-to-left override, a
        // zero-width space, a line separator and a paragraph separator.
        foreach (var bad in new[] { "\n", "\r", "\t", "\u0007", "\u001b", "\u007f", "\u202e", "\u200b", "\u2028", "\u2029" })
        {
            var shown = $"U+{(int)bad[0]:X4}";
            data.Add(shown + " in the machine name", A, $"the{bad}mac INJECTED", "owner", "invalid_machine");
            data.Add(shown + " in the user", A, "the-mac", $"own{bad}er INJECTED", "invalid_machine");
            data.Add(shown + " in the account", $"account{bad}INJECTED@example.com", "the-mac", "owner", "invalid_account");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ValuesWithACharacterThatIsNotText))]
    public void Set_ANameOrAccountHoldingACharacterThatIsNotText_IsRefused_NothingIsStored_AndTheLogDoesNotQuoteIt(
        string what, string account, string machine, string user, string word)
    {
        Assert.NotEmpty(what);
        using var capture = new CapturedLog();

        var set = _store.Set(One, account, machine, user, "work", T0);
        var lines = capture.Lines();

        Assert.False(set.Accepted);
        Assert.Equal(word, set.RefusalWord);
        Assert.DoesNotContain("INJECTED", set.RefusalText);
        Assert.Null(_settings.Get(One, TenantSettingKeys.LicenseApprovers));
        // The refusal is logged, and the line that names what was stored was never written.
        Assert.Contains(lines, l => l.EndsWith($"[LicenseApproverStore] Set: REFUSED {word}", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("[LicenseApproverStore] Set: tenant="));
        Assert.DoesNotContain(lines, l => l.Contains("INJECTED"));
    }

    [Fact]
    public void Set_NamesWithSpacesAndLettersOfAnyAlphabet_AreTaken()
    {
        Assert.True(_store.Set(One, A, "S\u00f8ren's Mac mini", "s\u00f8ren", "work", T0).Accepted);
        Assert.Equal("S\u00f8ren's Mac mini", _store.Find(One, A)!.MachineName);
    }

    [Fact]
    public void Set_ANameLongerThanAMachineNameMayBe_IsRefused()
    {
        var tooLong = new string('m', LicenseApproverStore.MaxNameLength + 1);

        Assert.Equal("invalid_machine", _store.Set(One, A, tooLong, "owner", "work", T0).RefusalWord);
        Assert.Equal("invalid_machine", _store.Set(One, A, "the-mac", tooLong, "work", T0).RefusalWord);
        Assert.True(_store.Set(One, A, new string('m', LicenseApproverStore.MaxNameLength), "owner", "work", T0).Accepted);
    }

    [Fact]
    public void Set_OnePastTheMostApproversAnAccountKeeps_IsRefused_AndReplacingOneIsStillAllowed()
    {
        for (var i = 0; i < LicenseApproverStore.MaxApprovers; i++)
            Assert.True(_store.Set(One, $"account-{i}@example.com", "the-mac", "owner", "work", T0).Accepted);

        Assert.Equal("too_many_approvers", _store.Set(One, "one-more@example.com", "the-mac", "owner", "work", T0).RefusalWord);
        Assert.True(_store.Set(One, "account-0@example.com", "the-mac", "owner", "another", T0).Accepted);
        Assert.Equal(LicenseApproverStore.MaxApprovers, _store.List(One).Count);
    }

    [Fact]
    public void WhatIsStored_IsExactlyTheFieldsOfTheContract_WhoMadeTheRecordAndItsLastCheck()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);

        var raw = _settings.Get(One, TenantSettingKeys.LicenseApprovers)!;
        var stored = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList());
        Assert.Equal(new[] { "accountEmail", "machineName", "osUser", "browserProfile", "source", "state", "checkedAtUtc", "shownAccount", "detail" },
            stored.EnumerateObject().Select(p => p.Name));
        Assert.Equal(LicenseApproverSources.Owner, stored.GetProperty("source").GetString());
    }

    [Fact]
    public void ARecordStoredBeforeSourcesExisted_ReadsAsOwnerSet_AndNeverChecked()
    {
        _settings.Set(One, TenantSettingKeys.LicenseApprovers,
            """[{"accountEmail":"account-a@example.com","machineName":"the-mac","osUser":"owner","browserProfile":"work"}]""", T0);

        var read = _store.Find(One, A)!;

        Assert.Equal(new LicenseApprover(A, "the-mac", "owner", "work", LicenseApproverSources.Owner), read);
        Assert.Equal(LicenseApproverStates.NotChecked, LicenseApproverHealth.StateOf(read, T0));
    }

    [Fact]
    public void ARecordWhoseSourceIsNotASourceWord_IsAnError()
    {
        _settings.Set(One, TenantSettingKeys.LicenseApprovers,
            """[{"accountEmail":"account-a@example.com","machineName":"the-mac","osUser":"owner","browserProfile":"work","source":"someone"}]""", T0);

        Assert.Throws<InvalidOperationException>(() => _store.List(One));
    }

    // A registered record, as LicenseApproverRegistration writes one.
    private void Registered(string account, string machine, string profile, DateTime checkedAt)
        => _store.Change(One, records =>
        {
            var next = records.Approvers.Where(a => a.AccountEmail != account).ToList();
            next.Add(new LicenseApprover(account, machine, "owner", profile, LicenseApproverSources.Registered, LicenseApproverStates.CanApprove,
                checkedAt, account, null));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, checkedAt);

    [Fact]
    public void Remove_OfARegisteredRecord_LeavesARemovalRecordForThatAccountMachineUserAndProfile()
    {
        Registered(A, "the-mac", "work", T0);

        Assert.True(_store.Remove(One, A, T0.AddMinutes(1)));

        Assert.Null(_store.Find(One, A));
        Assert.Equal(new[] { new LicenseApproverRemoval(A, "the-mac", "owner", "work", T0.AddMinutes(1)) }, _store.ListRemovals(One));
    }

    [Fact]
    public void Remove_OfAnOwnerSetRecord_LeavesNoRemovalRecord()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);

        Assert.True(_store.Remove(One, A, T0));

        Assert.Empty(_store.ListRemovals(One));
        Assert.Null(_settings.Get(One, TenantSettingKeys.LicenseApproverRemovals));
    }

    [Fact]
    public void Set_ClearsTheRemovalRecordsOfThatAccount_AndOnlyThatAccount()
    {
        Registered(A, "the-mac", "work", T0);
        Registered(B, "the-mac", "work", T0);
        _store.Remove(One, A, T0);
        _store.Remove(One, B, T0);

        _store.Set(One, A, "the-windows-machine", "owner", "personal", T0);

        Assert.Equal(B, Assert.Single(_store.ListRemovals(One)).AccountEmail);
    }

    [Fact]
    public void Removals_KeepTheNewestUpToTheMost()
    {
        for (var i = 0; i <= LicenseApproverStore.MaxRemovals; i++)
        {
            var account = $"other-{i}@example.com";
            Registered(account, "the-mac", "work", T0.AddMinutes(i));
            _store.Remove(One, account, T0.AddMinutes(i));
        }

        var kept = _store.ListRemovals(One);

        Assert.Equal(LicenseApproverStore.MaxRemovals, kept.Count);
        Assert.Equal("other-1@example.com", kept[0].AccountEmail);
    }

    [Fact]
    public void Set_TheVeryProfileThatWasChecked_KeepsItsLastCheck_AndMakesItOwnerSet()
    {
        Registered(A, "the-mac", "work", T0);

        _store.Set(One, A, "THE-MAC", "owner", "work", T0.AddMinutes(1));

        var read = _store.Find(One, A)!;
        Assert.Equal((LicenseApproverSources.Owner, LicenseApproverStates.CanApprove, (DateTime?)T0), (read.Source, read.State, read.CheckedAtUtc));
    }

    [Fact]
    public void Set_AnotherProfile_StartsNeverChecked()
    {
        Registered(A, "the-mac", "work", T0);

        _store.Set(One, A, "the-mac", "owner", "personal", T0);

        Assert.Equal(LicenseApproverStates.NotChecked, LicenseApproverHealth.StateOf(_store.Find(One, A)!, T0));
    }

    [Fact]
    public void StateOf_ARecordCheckedExactly26HoursAgo_IsItsState_AndOneSecondLaterIsNotChecked()
    {
        var approver = new LicenseApprover(A, "the-mac", "owner", "work", LicenseApproverSources.Registered, LicenseApproverStates.NeedsFreshSignIn, T0);

        Assert.Equal(LicenseApproverStates.NeedsFreshSignIn, LicenseApproverHealth.StateOf(approver, T0 + LicenseApproverContract.NotCheckedAfter));
        Assert.Equal(LicenseApproverStates.NotChecked,
            LicenseApproverHealth.StateOf(approver, T0 + LicenseApproverContract.NotCheckedAfter + TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromHours(26), LicenseApproverContract.NotCheckedAfter);
    }

    [Fact]
    public void Change_ThatLeavesMoreThanTheMostApprovers_IsAnError_AndWritesNothing()
    {
        var tooMany = Enumerable.Range(0, LicenseApproverStore.MaxApprovers + 1)
            .Select(i => new LicenseApprover($"other-{i}@example.com", "the-mac", "owner", "work")).ToList();

        Assert.Throws<InvalidOperationException>(() => _store.Change(One, _ => ((IReadOnlyList<LicenseApprover>?)tooMany, 0), T0));

        Assert.Null(_settings.Get(One, TenantSettingKeys.LicenseApprovers));
    }

    [Fact]
    public void AStoredValueThatCannotBeRead_IsAnError_NeverAnEmptyList()
    {
        _settings.Set(One, TenantSettingKeys.LicenseApprovers, "{ this is not a list", T0);

        Assert.Throws<JsonException>(() => _store.List(One));
        Assert.Throws<JsonException>(() => _store.Find(One, A));
    }
}
