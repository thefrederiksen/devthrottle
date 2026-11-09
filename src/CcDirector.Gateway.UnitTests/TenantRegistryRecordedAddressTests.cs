using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The tenant registry's account lookup and the sign-in address it records (issues #2119 and
/// devthrottle_internal#2816).
///
/// WHY THE ADDRESS IS RECORDED AT ALL. The email was once recorded on a FRESH MINT ONLY, so every tenant minted
/// before the email was captured - or from a token that carried none - was left permanently without one. The
/// morning report looks an account up by email, so a null email turned a real account into "no such account".
///
/// WHY IT IS NOW AN OVERWRITE. It was a backfill that never replaced an address, and that left an account whose
/// sign-in address MOVED still answering to the old one: the team checks (add at once, invitation open, accept and
/// decline) and the morning report all compare against the recorded address. So the token's email claim now
/// replaces what is recorded at every sign-in that carries one. The mapping key remains the subject and only the
/// subject, and a token with no claim keeps what is recorded rather than clearing it.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class TenantRegistryRecordedAddressTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private TenantRegistry NewRegistry() => new(_h.Open());

    private static string Subject() => "sub-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void MintOrLookupBySubject_MintedWithoutAnEmail_RecordsTheEmailWhenTheAccountNextResolves()
    {
        var registry = NewRegistry();
        var subject = Subject();

        var minted = registry.MintOrLookupBySubject(subject, email: null);
        Assert.Null(registry.EmailForTenant(minted));

        // The same account resolves again, this time carrying its email.
        var again = registry.MintOrLookupBySubject(subject, "soren@example.com");

        Assert.Equal(minted, again);   // same tenant - the subject is still the only key
        Assert.Equal("soren@example.com", registry.EmailForTenant(minted));
    }

    [Fact]
    public void MintOrLookupBySubject_SignInAddressChanged_RecordsTheNewAddressInPlaceOfTheOld()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "first@example.com");

        var again = registry.MintOrLookupBySubject(subject, "second@example.com");

        // The address follows the provider; the account is still the same account, keyed by its subject.
        Assert.Equal(tenant, again);
        Assert.Equal("second@example.com", registry.EmailForTenant(tenant));
    }

    [Fact]
    public void LookupByAccount_SignInAddressChanged_FindsTheAccountByItsNewAddressAndNotItsOld()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "old@example.com");

        registry.MintOrLookupBySubject(subject, "new@example.com");

        var byNew = registry.LookupByAccount("new@example.com");
        Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, byNew.Outcome);
        Assert.Equal(tenant, byNew.Tenant);
        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound, registry.LookupByAccount("old@example.com").Outcome);
    }

    [Fact]
    public void LookupByAccount_OldAddressTakenByANewAccountAfterTheOldHolderSignedInAgain_FindsOnlyTheNewHolder()
    {
        // The morning report's own case: the address moved from one account to another. Once the old holder has
        // signed in with its new address, the address names exactly one account - the one that holds it now.
        var registry = NewRegistry();
        var oldHolder = Subject();
        registry.MintOrLookupBySubject(oldHolder, "mailbox@example.com");
        registry.MintOrLookupBySubject(oldHolder, "moved@example.com");
        var newHolder = registry.MintOrLookupBySubject(Subject(), "mailbox@example.com");

        var (outcome, resolved) = registry.LookupByAccount("mailbox@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, outcome);
        Assert.Equal(newHolder, resolved);
    }

    [Fact]
    public void LookupByAccount_OldHolderNotSignedInSinceTheAddressMoved_IsAmbiguousAndPicksNeither()
    {
        // Until the old holder signs in again its row still carries the address beside the new holder's. The
        // report must then name neither: either guess could mail one person the other's day.
        var registry = NewRegistry();
        registry.MintOrLookupBySubject(Subject(), "mailbox@example.com");
        registry.MintOrLookupBySubject(Subject(), "mailbox@example.com");

        var (outcome, resolved) = registry.LookupByAccount("mailbox@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.Ambiguous, outcome);
        Assert.False(resolved.IsValid);
    }

    [Fact]
    public void MintOrLookupBySubject_AddressChanged_LogsTheChangeButNeverTheAddress()
    {
        var registry = NewRegistry();
        var subject = Subject();
        registry.MintOrLookupBySubject(subject, "first@example.com");

        IReadOnlyList<string> lines;
        using (var log = CcDirector.Core.Utilities.FileLog.RedirectForTests())
        {
            registry.MintOrLookupBySubject(subject, "second@example.com");
            lines = log.DrainAndReadLines();
        }

        Assert.Contains(lines, l => l.Contains("the sign-in address changed", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains('@'));
        Assert.DoesNotContain(lines, l => l.Contains(subject, StringComparison.Ordinal));
    }

    [Fact]
    public void MintOrLookupBySubject_SameAddressAgain_WritesNothing()
    {
        var registry = NewRegistry();
        var subject = Subject();
        registry.MintOrLookupBySubject(subject, "same@example.com");

        IReadOnlyList<string> lines;
        using (var log = CcDirector.Core.Utilities.FileLog.RedirectForTests())
        {
            registry.MintOrLookupBySubject(subject, "  same@example.com ");
            lines = log.DrainAndReadLines();
        }

        // Only a change is written: an ordinary sign-in records nothing new and says so by saying nothing.
        Assert.DoesNotContain(lines, l => l.Contains("recorded", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("resolved an existing tenant", StringComparison.Ordinal));
    }

    [Fact]
    public void MintOrLookupBySubject_TokenWithNoEmailClaim_KeepsTheAddressAndLogsThatTheClaimWasMissing()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "kept@example.com");

        IReadOnlyList<string> lines;
        using (var log = CcDirector.Core.Utilities.FileLog.RedirectForTests())
        {
            registry.MintOrLookupBySubject(subject, email: null);
            registry.MintOrLookupBySubject(subject, "   ");
            lines = log.DrainAndReadLines();
        }

        Assert.Equal("kept@example.com", registry.EmailForTenant(tenant));
        Assert.Equal(2, lines.Count(l => l.Contains("no email claim - the recorded address is kept", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains('@'));
    }

    [Fact]
    public void MintOrLookupBySubject_ResolvedWithoutAnEmail_KeepsTheRecordedAddress()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "kept@example.com");

        registry.MintOrLookupBySubject(subject, email: null);

        Assert.Equal("kept@example.com", registry.EmailForTenant(tenant));
    }

    [Fact]
    public void LookupByAccount_EmailRecordedOnALaterResolution_FindsTheAccount()
    {
        // The end-to-end point of the backfill, stated as the property the morning report depends on.
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, email: null);

        var before = registry.LookupByAccount("findable@example.com");
        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound, before.Outcome);

        registry.MintOrLookupBySubject(subject, "findable@example.com");

        var after = registry.LookupByAccount("findable@example.com");
        Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, after.Outcome);
        Assert.Equal(tenant, after.Tenant);
    }

    // ---- the lookup itself -----------------------------------------------------------------------------

    [Fact]
    public void LookupByAccount_TenantIdOrEmailInAnyCasing_FindsTheAccount()
    {
        var registry = NewRegistry();
        var tenant = registry.MintOrLookupBySubject(Subject(), "Mixed.Case@Example.com");

        foreach (var identifier in new[] { tenant.Value, "mixed.case@example.com", "MIXED.CASE@EXAMPLE.COM", "  Mixed.Case@Example.com  " })
        {
            var (outcome, resolved) = registry.LookupByAccount(identifier);
            Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, outcome);
            Assert.Equal(tenant, resolved);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nobody@example.com")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void LookupByAccount_UnknownOrEmptyIdentifier_IsNotFound(string? identifier)
    {
        var registry = NewRegistry();
        registry.MintOrLookupBySubject(Subject(), "somebody@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound,
            registry.LookupByAccount(identifier).Outcome);
    }

    [Fact]
    public void LookupByAccount_TwoAccountsShareAnEmail_IsAmbiguous()
    {
        // Two DIFFERENT accounts (different subjects - the real key) that happen to carry the same display
        // email. Picking either would email one person the other person's day, so there is no answer.
        var registry = NewRegistry();
        registry.MintOrLookupBySubject(Subject(), "shared@example.com");
        registry.MintOrLookupBySubject(Subject(), "shared@example.com");

        var (outcome, resolved) = registry.LookupByAccount("shared@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.Ambiguous, outcome);
        Assert.False(resolved.IsValid);
    }

    [Fact]
    public void LookupByAccount_UnknownAccount_NeverMints()
    {
        var registry = NewRegistry();

        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound,
            registry.LookupByAccount("ghost@example.com").Outcome);
        // Asking about an account must not bring it into existence - otherwise a service token could
        // populate the census one guess at a time.
        Assert.Empty(registry.AllTenantIds());
    }
}
