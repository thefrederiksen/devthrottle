using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>The pure invitation rules (devthrottle_internal#2301): who may invite whom, expiry, and what an address is.</summary>
public sealed class TeamInvitationRulesTests
{
    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, false)]
    [InlineData(TeamRole.Owner, TeamRole.Manager, true)]
    [InlineData(TeamRole.Owner, TeamRole.Developer, true)]
    [InlineData(TeamRole.Owner, TeamRole.Collaborator, true)]
    [InlineData(TeamRole.Manager, TeamRole.Owner, false)]
    [InlineData(TeamRole.Manager, TeamRole.Manager, false)]
    [InlineData(TeamRole.Manager, TeamRole.Developer, true)]
    [InlineData(TeamRole.Manager, TeamRole.Collaborator, true)]
    [InlineData(TeamRole.Developer, TeamRole.Owner, false)]
    [InlineData(TeamRole.Developer, TeamRole.Manager, false)]
    [InlineData(TeamRole.Developer, TeamRole.Developer, false)]
    [InlineData(TeamRole.Developer, TeamRole.Collaborator, false)]
    [InlineData(TeamRole.Collaborator, TeamRole.Owner, false)]
    [InlineData(TeamRole.Collaborator, TeamRole.Manager, false)]
    [InlineData(TeamRole.Collaborator, TeamRole.Developer, false)]
    [InlineData(TeamRole.Collaborator, TeamRole.Collaborator, false)]
    public void MayInvite_EveryPairOfRoles_MatchesTheDecidedTable(TeamRole inviter, TeamRole invited, bool expected)
    {
        Assert.Equal(expected, TeamInvitationRules.MayInvite(inviter, invited));
        Assert.Equal(expected, TeamInvitationRules.InviteRefusal(inviter, invited) is null);
    }

    [Fact]
    public void MayInvite_ARoleThatIsNotOneOfTheFour_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamInvitationRules.MayInvite((TeamRole)9, TeamRole.Developer));
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamInvitationRules.MayInvite(TeamRole.Owner, (TeamRole)9));
    }

    [Theory]
    [InlineData(TeamRole.Manager, TeamRole.Manager, TeamInvitationRefusals.OnlyOwnerInvitesManager)]
    [InlineData(TeamRole.Developer, TeamRole.Collaborator, TeamInvitationRefusals.NotAllowedToInvite)]
    [InlineData(TeamRole.Collaborator, TeamRole.Developer, TeamInvitationRefusals.NotAllowedToInvite)]
    [InlineData(TeamRole.Owner, TeamRole.Owner, TeamInvitationRefusals.InviteOwner)]
    [InlineData(TeamRole.Manager, TeamRole.Owner, TeamInvitationRefusals.InviteOwner)]
    public void InviteRefusal_EachRefusedPair_SaysWhyInPlainWords(TeamRole inviter, TeamRole invited, string expected)
    {
        Assert.Equal(expected, TeamInvitationRules.InviteRefusal(inviter, invited));
    }

    [Fact]
    public void EffectiveState_SentInvitation_ExpiresExactlySevenDaysAfterItWasSent()
    {
        var sent = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var expires = sent + TeamInvitationRules.ValidFor;

        Assert.Equal(TeamInvitationStates.Sent, TeamInvitationRules.EffectiveState("sent", expires, sent.AddDays(6)));
        Assert.Equal(TeamInvitationStates.Sent, TeamInvitationRules.EffectiveState("sent", expires, expires.AddTicks(-1)));
        Assert.Equal(TeamInvitationStates.Expired, TeamInvitationRules.EffectiveState("sent", expires, expires));
        Assert.Equal(TeamInvitationStates.Expired, TeamInvitationRules.EffectiveState("sent", expires, sent.AddDays(8)));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("declined")]
    [InlineData("cancelled")]
    public void EffectiveState_AnsweredInvitation_KeepsItsStateAfterExpiry(string state)
    {
        var expires = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(state, TeamInvitationRules.EffectiveState(state, expires, expires.AddDays(30)));
    }

    [Theory]
    [InlineData("anna@gmail.com", "anna@gmail.com")]
    [InlineData("  Rob@Client.CO.UK ", "rob@client.co.uk")]
    [InlineData("first.last+tag@sub.domain.museum", "first.last+tag@sub.domain.museum")]
    [InlineData("x@xn--bcher-kva.example", "x@xn--bcher-kva.example")]
    public void NormalizeEmail_AnyDomain_IsTrimmedAndLowerCased(string input, string expected)
    {
        Assert.Equal(expected, TeamInvitationRules.NormalizeEmail(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anna")]
    [InlineData("anna@")]
    [InlineData("@example.com")]
    [InlineData("anna@localhost")]
    [InlineData("anna@.example.com")]
    [InlineData("anna@example.com.")]
    [InlineData("a b@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("Anna <anna@example.com>")]
    [InlineData("anna@exa\nmple.com")]
    public void NormalizeEmail_NotAnAddress_IsNull(string? input)
    {
        Assert.Null(TeamInvitationRules.NormalizeEmail(input));
    }

    [Fact]
    public void HashAcceptToken_IsLowerCaseHexSha256_TheFormTheWebsiteComputesToo()
    {
        // sha256("abc"), the published test vector - the website's crypto.createHash('sha256') gives the same.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", TeamInvitationRules.HashAcceptToken("abc"));
        Assert.Throws<ArgumentException>(() => TeamInvitationRules.HashAcceptToken(""));
    }

    [Fact]
    public void NormalizeEmail_LongerThanTheLimit_IsNull()
    {
        var tooLong = new string('a', TeamInvitationRules.MaxEmailLength) + "@example.com";

        Assert.Null(TeamInvitationRules.NormalizeEmail(tooLong));
    }
}
