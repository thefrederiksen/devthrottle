using System.Net;
using System.Net.Http;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// THE ONE WAY THE GATEWAY EMAILS A PERSON BY ACCOUNT (Team implementation piece 4.4): the existing tenant-addressed owner
/// email, pointed at the person's own tenant. Proved against a recording HTTP handler - no network: the body names the
/// person's own tenant and no recipient, nothing is called without the service credential, and a person with no tenant
/// here is a permanent refusal.
/// </summary>
public sealed class PersonEmailSenderTests : IDisposable
{
    private const string Mike = "sub-mike";
    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantRegistry _tenants;
    private readonly RecordingHandler _handler = new();

    public PersonEmailSenderTests()
    {
        _tenants = new TenantRegistry(_harness.Open());
        _tenants.MintOrLookupBySubject(Mike, "mike@example.com");
    }

    public void Dispose() => _harness.Dispose();

    private PersonEmailSender Sender(string? token) =>
        new(new AccountNotifyByTenantClient(new HttpClient(_handler), "https://website.example.test"), _tenants, () => token);

    private static readonly PersonEmail Email = new("Something is waiting for you on DevThrottle", "text", "<p>html</p>");

    [Fact]
    public async Task SendAsync_ToAContributor_NamesTheirOwnTenantAndNoRecipient()
    {
        // Arrange
        var sender = Sender("service-secret");

        // Act
        var result = await sender.SendAsync(Mike, Email);

        // Assert
        Assert.True(result.Sent);
        var body = JsonDocument.Parse(Assert.Single(_handler.Bodies)).RootElement;
        Assert.Equal(_tenants.LookupBySubject(Mike)!.Value.Value, body.GetProperty("tenant_id").GetString());
        Assert.Equal(Mike, body.GetProperty("account_subject").GetString());
        foreach (var key in new[] { "to", "email", "recipient", "recipients", "cc", "bcc" })
            Assert.False(body.TryGetProperty(key, out _), $"the body must not carry '{key}'");
        Assert.Equal("/api/v1/account/notify-owner-by-tenant", _handler.Paths.Single());
    }

    [Fact]
    public async Task SendAsync_NoServiceCredential_CallsNothingAndSaysSo()
    {
        // Arrange
        var sender = Sender(null);

        // Act
        var result = await sender.SendAsync(Mike, Email);

        // Assert
        Assert.False(result.Sent);
        Assert.False(result.Permanent);
        Assert.Equal(PersonEmailSender.NotSetUp, result.Detail);
        Assert.Empty(_handler.Bodies);
    }

    [Fact]
    public async Task SendAsync_PersonWithNoTenantHere_IsAPermanentRefusalAndCallsNothing()
    {
        // Arrange
        var sender = Sender("service-secret");

        // Act
        var result = await sender.SendAsync("sub-nobody", Email);

        // Assert
        Assert.False(result.Sent);
        Assert.True(result.Permanent);
        Assert.Empty(_handler.Bodies);
    }

    [Fact]
    public async Task SendAsync_EmailServiceRateLimits_ReturnsItsWait()
    {
        // Arrange
        _handler.Answer = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"type\":\"rate_limited\",\"code\":\"notify_rate_limited\",\"message\":\"Too many.\"}}"),
            };
            r.Headers.Add("Retry-After", "120");
            return r;
        };

        // Act
        var result = await Sender("service-secret").SendAsync(Mike, Email);

        // Assert
        Assert.False(result.Sent);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal("Too many.", result.Detail);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string> Paths { get; } = new();
        public Func<HttpResponseMessage> Answer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"sent\":true,\"id\":\"msg-1\"}}"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Answer();
        }
    }
}
