using System.Net;
using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// <c>GET /gateway/mail-desk/providers</c> (mission "Zoho Mail Skill", section 4): the one list of what is supported, by
/// provider, whole or for one provider, with an unknown provider refused naming the ones there are.
/// </summary>
public sealed partial class MailDeskEndpointTests
{
    [Fact]
    public async Task ListProviders_NoFilter_AnswersEveryProviderAndHowManyTheMailDeskReads()
    {
        // Act
        var (status, body) = await Get(P + "/providers");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4, body.GetProperty("count").GetInt32());
        Assert.Equal(4, body.GetProperty("total").GetInt32());
        Assert.Equal(MailDeskWords.Providers.All.Count, body.GetProperty("mailDesk").GetInt32());
        var ids = body.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("provider").GetString()).ToList();
        Assert.Equal(new[] { "zoho", "imap", "gmail", "outlook" }, ids);
    }

    [Fact]
    public async Task ListProviders_OneProvider_AnswersOnlyItWithItsProof()
    {
        // Act
        var (status, body) = await Get(P + "/providers?provider=IMAP");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(4, body.GetProperty("total").GetInt32());
        var imap = body.GetProperty("providers")[0];
        Assert.Equal("imap", imap.GetProperty("provider").GetString());
        Assert.Equal("dt-email, dt-calendar", imap.GetProperty("tool").GetString());
        var notWorking = imap.GetProperty("email").GetProperty("notWorking")[0];
        Assert.Equal("Send from an alias", notWorking.GetProperty("feature").GetString());
        Assert.Equal(MailDeskProviders.States.NotTested, notWorking.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ListProviders_UnknownProvider_IsRefusedNamingTheProviders()
    {
        // Act
        var (status, body) = await Get(P + "/providers?provider=fastmail");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("no_such_provider", body.GetProperty("error").GetString());
        Assert.Contains("zoho, imap, gmail, outlook", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ListProviders_ASession_MayRead()
    {
        // Act
        var (status, _) = await Get(P + "/providers", caller: FactorySession);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
    }
}
