using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// WHO APPROVES EACH ACCOUNT'S SIGN-INS (License Switching, contract section 8.3), over the real per-tenant settings on
/// a throwaway SQLite file. What these hold down: one approver per account, kept per DevThrottle account; setting again
/// replaces; removing removes; a value that is not what it claims to be is refused and nothing is stored; one account
/// never sees another's; and what is stored is exactly the four fields of the contract.
/// </summary>
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
    public void WhatIsStored_IsExactlyTheFourFieldsOfTheContract()
    {
        _store.Set(One, A, "the-mac", "owner", "work", T0);

        var raw = _settings.Get(One, TenantSettingKeys.LicenseApprovers)!;
        var stored = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList());
        Assert.Equal(new[] { "accountEmail", "machineName", "osUser", "browserProfile" }, stored.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AStoredValueThatCannotBeRead_IsAnError_NeverAnEmptyList()
    {
        _settings.Set(One, TenantSettingKeys.LicenseApprovers, "{ this is not a list", T0);

        Assert.Throws<JsonException>(() => _store.List(One));
        Assert.Throws<JsonException>(() => _store.Find(One, A));
    }
}
