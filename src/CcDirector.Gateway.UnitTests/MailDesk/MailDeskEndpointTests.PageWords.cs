using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE WORDS THE MAIL DESK PAGE READS OFF <c>GET /channels</c> (Mail Desk mission, phase 7 contract, section 2): the
/// envelope's title, summary and empty sentence on an empty list and a full one, and every new field of a row as it
/// reaches a client. Each is additive: the fields the <c>cc-devthrottle maildesk</c> command reads keep their names.
/// </summary>
public sealed partial class MailDeskEndpointTests
{
    private static void AssertPageWords(JsonElement body)
    {
        Assert.Equal("Mail Desk", body.GetProperty("title").GetString());
        Assert.Equal(MailDeskChannelFold.PageSummary, body.GetProperty("summary").GetString());
        Assert.Equal(MailDeskChannelFold.PageEmptyLabel, body.GetProperty("emptyLabel").GetString());
        Assert.StartsWith("No mailbox is on the Mail Desk yet.", body.GetProperty("emptyLabel").GetString());
        Assert.Contains("cc-devthrottle maildesk channel add", body.GetProperty("emptyLabel").GetString());
    }

    [Fact]
    public async Task ListChannels_NoChannels_EnvelopeCarriesTheTitleSummaryAndEmptySentence()
    {
        // Act
        var (status, body) = await Get(P + "/channels");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(0, body.GetProperty("channels").GetArrayLength());
        AssertPageWords(body);
    }

    [Fact]
    public async Task ListChannels_TwoChannels_EnvelopeCarriesTheSameThreeSentences()
    {
        // Arrange
        await AddMailbox();
        await AddMailbox(address: OtherMailbox);

        // Act
        var (_, body) = await Get(P + "/channels");

        // Assert
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        AssertPageWords(body);
    }

    [Fact]
    public async Task ListChannels_ObserveRowNeverChecked_CarriesEveryNewWordAndTone()
    {
        // Arrange - two machines, a start never checked, one decision today
        await Post(P + "/channels", new { address = Mailbox, provider = "outlook", machines = new[] { Machine, "north-box" }, why = "the support mailbox" });
        _triggers.Add(MailDeskTrigger());
        var channel = _channels.List(A).Single();
        Decide(channel, "one", _now.AddHours(-1));

        // Act
        var row = await OnlyRow();

        // Assert - the fields the command reads are still there
        Assert.Equal("outlook", row.GetProperty("provider").GetString());
        Assert.Equal(MailDeskWords.Modes.Observe, row.GetProperty("mode").GetString());
        Assert.Equal(1, row.GetProperty("today").GetProperty("total").GetInt32());
        Assert.Equal(1, row.GetProperty("today").GetProperty("byDecision").GetProperty(MailDeskWords.Decisions.Archive).GetInt32());
        // ... and the new ones beside them
        Assert.Equal("Outlook", row.GetProperty("providerLabel").GetString());
        Assert.Equal("devlinux, then north-box if devlinux is not answering", row.GetProperty("machinesLabel").GetString());
        Assert.Equal(MailDeskChannelFold.Tones.Neutral, row.GetProperty("modeTone").GetString());
        var signedIn = row.GetProperty("signedIn");
        Assert.Equal(MailDeskChannelFold.SignedInToneOf(signedIn.GetProperty("state").GetString()!), signedIn.GetProperty("tone").GetString());
        Assert.Equal(MailDeskChannelFold.Tones.Neutral, row.GetProperty("lastCheck").GetProperty("tone").GetString());
        var today = row.GetProperty("today");
        Assert.Equal("1 email decided today", today.GetProperty("label").GetString());
        var counts = today.GetProperty("counts").EnumerateArray().ToList();
        Assert.Equal(MailDeskWords.Decisions.All, counts.Select(c => c.GetProperty("decision").GetString()));
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 0 }, counts.Select(c => c.GetProperty("count").GetInt32()));
        Assert.Equal("Archive", counts[1].GetProperty("label").GetString());
    }

    [Fact]
    public async Task ListChannels_LiveRowWithAFailedCheck_ModeAsksForAttentionAndLastCheckIsBad()
    {
        // Arrange
        await AddMailbox();
        await Post(P + "/channels/mode", new { address = Mailbox, mode = MailDeskWords.Modes.Live, why = "ready to act" });
        _triggers.Add(MailDeskTrigger(_now.AddMinutes(-5), TriggerRunOutcome.Failed, "email@devthrottle.com is not signed in on devlinux"));

        // Act
        var row = await OnlyRow();

        // Assert
        Assert.Equal(MailDeskWords.Modes.Live, row.GetProperty("mode").GetString());
        Assert.Equal(MailDeskChannelFold.Tones.Attention, row.GetProperty("modeTone").GetString());
        Assert.Equal(Machine, row.GetProperty("machinesLabel").GetString());
        Assert.Equal(MailDeskChannelFold.Tones.Bad, row.GetProperty("lastCheck").GetProperty("tone").GetString());
        Assert.Equal("No emails decided today", row.GetProperty("today").GetProperty("label").GetString());
    }
}
