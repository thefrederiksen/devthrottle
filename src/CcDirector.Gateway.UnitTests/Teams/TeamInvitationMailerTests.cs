using System.Net;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The invitation email (devthrottle_internal#2301, Delivery Lead decision D4): the Gateway names the INVITATION and
/// nothing else - never an address - with the Gateway service credential in its own header.
/// </summary>
public sealed class TeamInvitationMailerTests
{
    private const string Token = "test-gateway-service-token";

    [Fact]
    public async Task SendInvitationAsync_SendsOnlyTheInvitationIdWithTheServiceHeader()
    {
        var website = new RecordingWebsite(HttpStatusCode.OK, "{\"data\":{\"sent\":true,\"id\":\"re_1\"}}");
        var client = new TeamInvitationMailClient(new HttpClient(website), "https://website.test");

        var result = await client.SendInvitationAsync(Token, "inv-1", "link-secret");

        Assert.True(result.Sent);
        var call = Assert.Single(website.Calls);
        Assert.Equal("https://website.test/api/v1/teams/invitation-email", call.Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, call.Request.Method);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("inv-1", body["invitation_id"]!.GetValue<string>());
        Assert.Equal("link-secret", body["token"]!.GetValue<string>());
        // No recipient, no subject, no text: the website reads all of it from the row.
        Assert.Equal(new[] { "invitation_id", "token" }, body.Select(p => p.Key).OrderBy(k => k));
        Assert.Equal(Token, call.Request.Headers.GetValues(AccountNotifyByTenantClient.ServiceTokenHeader).Single());
        Assert.Null(call.Request.Headers.Authorization);
    }

    [Fact]
    public async Task SendInvitationAsync_SuccessStatusWithoutSent_IsNotReportedAsSent()
    {
        var website = new RecordingWebsite(HttpStatusCode.OK, "{\"data\":{}}");
        var client = new TeamInvitationMailClient(new HttpClient(website), "https://website.test");

        var result = await client.SendInvitationAsync(Token, "inv-1", "link-secret");

        Assert.False(result.Sent);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SendInvitationAsync_Refused_CarriesTheWebsitesOwnMessageAndCode()
    {
        var website = new RecordingWebsite(HttpStatusCode.Conflict,
            "{\"error\":{\"code\":\"invitation_not_waiting\",\"message\":\"This invitation is no longer waiting.\"}}");
        var client = new TeamInvitationMailClient(new HttpClient(website), "https://website.test");

        var result = await client.SendInvitationAsync(Token, "inv-1", "link-secret");

        Assert.False(result.Sent);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("invitation_not_waiting", result.ErrorCode);
        Assert.Equal("This invitation is no longer waiting.", result.Error);
    }

    [Theory]
    [InlineData("", "inv-1", "link-secret")]
    [InlineData("token", "", "link-secret")]
    [InlineData("token", "inv-1", "")]
    public async Task SendInvitationAsync_MissingCredentialIdOrLinkToken_Throws(string token, string id, string link)
    {
        var client = new TeamInvitationMailClient(new HttpClient(new RecordingWebsite(HttpStatusCode.OK, "{}")), "https://website.test");

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendInvitationAsync(token, id, link));
    }

    [Fact]
    public async Task Mailer_NoServiceCredential_DoesNotCallTheWebsite_AndSaysTheEmailWasNotSent()
    {
        var website = new RecordingWebsite(HttpStatusCode.OK, "{\"data\":{\"sent\":true}}");
        var mailer = new TeamInvitationMailer(new TeamInvitationMailClient(new HttpClient(website), "https://website.test"), () => null);

        var result = await mailer.SendAsync("inv-1", "link-secret");

        Assert.False(result.Sent);
        Assert.Contains("not set up to send email", result.Error);
        Assert.Empty(website.Calls);
    }

    [Fact]
    public async Task Mailer_WebsiteUnreachable_SaysTheEmailWasNotSent()
    {
        var mailer = new TeamInvitationMailer(
            new TeamInvitationMailClient(new HttpClient(new ThrowingWebsite()), "https://website.test"), () => Token);

        var result = await mailer.SendAsync("inv-1", "link-secret");

        Assert.False(result.Sent);
        Assert.Contains("could not be reached", result.Error);
    }

    [Fact]
    public async Task Mailer_WithCredential_PassesTheWebsitesAnswerThrough()
    {
        var website = new RecordingWebsite(HttpStatusCode.OK, "{\"data\":{\"sent\":true}}");
        var mailer = new TeamInvitationMailer(new TeamInvitationMailClient(new HttpClient(website), "https://website.test"), () => Token);

        Assert.True((await mailer.SendAsync("inv-1", "link-secret")).Sent);
        Assert.Single(website.Calls);
    }

    private sealed class RecordingWebsite(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }

    private sealed class ThrowingWebsite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
