using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Api;

/// <summary>
/// The staff gate on the Wingman's debug view - the one thing in this product that decides a reader may see raw
/// terminal screens, whole conversations, every prompt and every model answer.
///
/// THE PROPERTY THAT MATTERS IS THE DEFAULT. A gate whose job is to WIDEN what one reader can see must grant
/// nothing when nobody configured it, because the failure it can have is silent: a customer handed a view of
/// their own sessions' prompts would never think to report it. So the first test here is the empty list.
/// </summary>
public sealed class StaffAccessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoListConfigured_NobodyIsStaff(string? configured)
    {
        Assert.False(StaffAccess.IsStaff("soren@centerconsulting.com", configured));
        Assert.False(StaffAccess.IsStaff("anyone@example.com", configured));
        Assert.Equal(0, StaffAccess.ConfiguredCount(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ACallerWithNoAddress_IsNeverStaff(string? email)
    {
        // A hosted tenant records an email on a fresh mint only, so "we do not know who this is" is ORDINARY
        // rather than exceptional - and it must never read as "they are staff".
        Assert.False(StaffAccess.IsStaff(email, "soren@centerconsulting.com"));
    }

    [Fact]
    public void AnAddressOnTheList_IsStaff_WhateverItsCase()
    {
        const string list = "soren@centerconsulting.com, qa@mindzie.com";

        Assert.True(StaffAccess.IsStaff("soren@centerconsulting.com", list));
        Assert.True(StaffAccess.IsStaff("SOREN@CenterConsulting.COM", list));
        Assert.True(StaffAccess.IsStaff("  qa@mindzie.com  ", list));
        Assert.Equal(2, StaffAccess.ConfiguredCount(list));
    }

    [Theory]
    [InlineData("someone@centerconsulting.com")]
    [InlineData("soren@centerconsulting.com.evil.test")]
    [InlineData("centerconsulting.com")]
    [InlineData("soren")]
    public void AnAddressThatIsNotONTheList_IsNotStaff(string email)
    {
        // WHOLE-ADDRESS EQUALITY, never a prefix, a suffix or a domain. A gate that matched a domain would hand
        // the view to every address an attacker can register under a lookalike one.
        Assert.False(StaffAccess.IsStaff(email, "soren@centerconsulting.com, qa@mindzie.com"));
    }

    [Fact]
    public void BothSeparatorsAreAccepted_AndBlanksBetweenThemAreNotEntries()
    {
        const string list = "one@example.com;;two@example.com, ,three@example.com";

        Assert.Equal(3, StaffAccess.ConfiguredCount(list));
        Assert.True(StaffAccess.IsStaff("two@example.com", list));
        Assert.True(StaffAccess.IsStaff("three@example.com", list));
        // The empty stretches between the separators are not entries, so a blank address matches nothing.
        Assert.False(StaffAccess.IsStaff("", list));
    }

    [Fact]
    public void TheEnvironmentVariableIsNamedOnce()
    {
        // The deployment sets this name, the startup line prints it, and the refusal message points at it. A
        // second spelling anywhere would be a gate nobody could turn on.
        Assert.Equal("DEVTHROTTLE_STAFF_EMAILS", StaffAccess.EnvironmentVariable);
    }
}
