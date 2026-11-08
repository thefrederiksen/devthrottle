using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE ONE LIST OF WHAT IS SUPPORTED (mission "Zoho Mail Skill", section 1). Every provider the Mail Desk accepts has a
/// row; the Mail Desk column follows what the Gateway accepts and nothing else; nothing is listed as working and as not
/// working at once; every feature not proven working says why; and the cells the proof step failed or did not test are
/// pinned, so a hopeful edit cannot list them as working.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskProvidersTests
{
    [Fact]
    public void All_IsTheFourProvidersInReadingOrder()
    {
        Assert.Equal(new[] { "zoho", "imap", "gmail", "outlook" }, MailDeskProviders.All.Select(p => p.Provider));
    }

    [Fact]
    public void All_EveryProviderTheMailDeskAccepts_HasARow()
    {
        Assert.All(MailDeskWords.Providers.All, id => Assert.NotNull(MailDeskProviders.Find(id)));
    }

    [Fact]
    public void MailDesk_IsYesExactlyForTheProvidersTheGatewayAccepts()
    {
        foreach (var p in MailDeskProviders.All)
        {
            var accepted = MailDeskWords.Providers.All.Contains(p.Provider);
            Assert.Equal(accepted ? MailDeskProviders.MailDeskStates.Yes : MailDeskProviders.MailDeskStates.NotYet, p.MailDesk.State);
        }
    }

    [Fact]
    public void MailDesk_AnAcceptedProvider_SaysTheAddCommandForThatProviderOnly()
    {
        var outlook = MailDeskProviders.Find("outlook")!;

        Assert.Equal("Yes. Add a mailbox with: cc-devthrottle maildesk channel add <address> --provider outlook --machine <machine> --why \"<text>\"",
            outlook.MailDesk.Label);
    }

    [Fact]
    public void MailDesk_ZohoAndImap_AreBothOnTheMailDesk()
    {
        Assert.Equal(MailDeskProviders.MailDeskStates.Yes, MailDeskProviders.Find("zoho")!.MailDesk.State);
        Assert.Equal("Yes. Add a mailbox with: cc-devthrottle maildesk channel add <address> --provider zoho --machine <machine> --why \"<text>\"",
            MailDeskProviders.Find("zoho")!.MailDesk.Label);
        Assert.Equal(MailDeskProviders.MailDeskStates.Yes, MailDeskProviders.Find("imap")!.MailDesk.State);
        Assert.Equal("Yes. Add a mailbox with: cc-devthrottle maildesk channel add <address> --provider imap --machine <machine> --why \"<text>\"",
            MailDeskProviders.Find("imap")!.MailDesk.Label);
    }

    [Fact]
    public void Zoho_Needs_TheZohoKeyInCcSecretsAndDtZoho_AndNotImap()
    {
        var needs = MailDeskProviders.Find("zoho")!.Needs;

        Assert.Contains(needs, n => n.StartsWith("The Zoho key in cc-secrets", StringComparison.Ordinal)
            && n.Contains("zoho-api-client-id, zoho-api-client-secret and zoho-api-refresh-token", StringComparison.Ordinal)
            && n.Contains("never stored on the Gateway", StringComparison.Ordinal));
        Assert.Contains(needs, n => n.StartsWith("dt-zoho on that machine", StringComparison.Ordinal));
        Assert.DoesNotContain(needs, n => n.StartsWith("IMAP turned on", StringComparison.Ordinal));
    }

    [Fact]
    public void Imap_Needs_ADtEmailAccountOnEveryMachineThatChecksIt()
    {
        Assert.Contains(MailDeskProviders.Find("imap")!.Needs, n => n.StartsWith("A dt-email account for the mailbox", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_IgnoresCaseAndAnswersNullForAnUnknownProvider()
    {
        Assert.Equal("gmail", MailDeskProviders.Find("GMAIL")!.Provider);
        Assert.Null(MailDeskProviders.Find("fastmail"));
    }

    [Fact]
    public void EveryRow_HasItsWordsAndEveryNotWorkingFeatureSaysWhy()
    {
        foreach (var p in MailDeskProviders.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Tool));
            Assert.False(string.IsNullOrWhiteSpace(p.Admin));
            Assert.False(string.IsNullOrWhiteSpace(p.ProvenOn));
            Assert.NotEmpty(p.Needs);
            foreach (var area in new[] { p.Email, p.Calendar })
            {
                Assert.All(area.NotWorking, f =>
                {
                    Assert.Contains(f.State, new[] { MailDeskProviders.States.Fail, MailDeskProviders.States.NotTested });
                    Assert.False(string.IsNullOrWhiteSpace(f.Why));
                });
                Assert.Empty(area.Works.Intersect(area.NotWorking.Select(f => f.Feature)));
            }
        }
    }

    [Fact]
    public void Zoho_WhatTheProofAndDtZohosRealRunsPassed_IsListedAsWorking()
    {
        var zoho = MailDeskProviders.Find("zoho")!;

        Assert.Contains("List folders and messages, read a message, search", zoho.Email.Works);
        Assert.Contains("Send from an alias", zoho.Email.Works);
        Assert.Contains("Scheduled send", zoho.Email.Works);
        Assert.Contains("Create, change and delete an event", zoho.Calendar.Works);
        Assert.Contains("Zoho Meeting on an event: a join link for guests, and a start link for the owner only", zoho.Calendar.Works);
        Assert.StartsWith("Works through dt-zoho:", zoho.Admin);
        Assert.Contains("Fails: changing the display name", zoho.Admin);
    }

    [Theory]
    [InlineData("zoho", "email", "Forward", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "email", "Filters", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "email", "Unarchive with Zoho's own call", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "email", "Send or change a saved draft", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "email", "List or cancel a scheduled message", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "email", "Mail Desk filing (live mode) on a real Zoho mailbox", MailDeskProviders.States.NotTested)]
    [InlineData("zoho", "calendar", "Invite people; invitation comes from the right address", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "calendar", "Accept or decline an invitation", MailDeskProviders.States.Fail)]
    [InlineData("zoho", "calendar", "Free or busy times", MailDeskProviders.States.NotTested)]
    [InlineData("zoho", "calendar", "Change a whole repeating series", MailDeskProviders.States.NotTested)]
    [InlineData("imap", "email", "Send from an alias", MailDeskProviders.States.NotTested)]
    [InlineData("imap", "calendar", "Invite people; invitation comes from the right address", MailDeskProviders.States.Fail)]
    [InlineData("gmail", "email", "Download attachments", MailDeskProviders.States.Fail)]
    [InlineData("gmail", "email", "Forward", MailDeskProviders.States.Fail)]
    [InlineData("gmail", "email", "Delete permanently", MailDeskProviders.States.Fail)]
    [InlineData("gmail", "calendar", "Accept or decline an invitation", MailDeskProviders.States.Fail)]
    [InlineData("outlook", "email", "Mark unread", MailDeskProviders.States.Fail)]
    [InlineData("outlook", "email", "List or search a folder you made", MailDeskProviders.States.Fail)]
    [InlineData("gmail", "email", "Remove a star, mark read without opening", MailDeskProviders.States.Fail)]
    [InlineData("outlook", "calendar", "Change an event", MailDeskProviders.States.Fail)]
    [InlineData("outlook", "calendar", "Accept or decline an invitation", MailDeskProviders.States.Fail)]
    public void TheProofsFailuresAndGaps_AreListedAsNotWorking(string provider, string area, string feature, string state)
    {
        var p = MailDeskProviders.Find(provider)!;
        var found = (area == "email" ? p.Email : p.Calendar).NotWorking.Single(f => f.Feature == feature);

        Assert.Equal(state, found.State);
    }

    [Fact]
    public void Gmail_MarkUnread_WhichTheProofPassed_IsListedAsWorking()
    {
        Assert.Contains("Mark read, mark unread, star", MailDeskProviders.Find("gmail")!.Email.Works);
    }

    [Fact]
    public void Summary_CountsEachStateAndLeavesOutZeros()
    {
        Assert.Equal("9 work, 1 not tested", MailDeskProviders.Find("imap")!.Email.Summary);
        Assert.Equal("3 work, 1 fail", MailDeskProviders.Find("imap")!.Calendar.Summary);
        Assert.Equal("14 work, 5 fail, 1 not tested", MailDeskProviders.Find("zoho")!.Email.Summary);
        Assert.Equal("7 work, 2 fail, 3 not tested", MailDeskProviders.Find("zoho")!.Calendar.Summary);
    }

    [Fact]
    public void Summary_AStateWithNoWords_Throws()
    {
        var area = new MailDeskProviderArea(new[] { "a" }, new[] { new MailDeskProviderFeature("b", "maybe", "c") });

        Assert.Throws<InvalidOperationException>(() => area.Summary);
    }

    [Fact]
    public void SummaryOf_TheWholeList_CountsTheMailDesk()
    {
        var on = MailDeskWords.Providers.All.Count;
        Assert.Equal($"4 providers: on the Mail Desk {on}, not yet {4 - on}", MailDeskProviders.SummaryOf(MailDeskProviders.All));
    }

    [Fact]
    public void Outlook_IsNeverOfferedThroughImap()
    {
        Assert.Contains(MailDeskProviders.Find("imap")!.Needs, n => n.StartsWith("Not Outlook", StringComparison.Ordinal));
    }
}
