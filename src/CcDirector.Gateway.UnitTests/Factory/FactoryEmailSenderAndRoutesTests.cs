using System.Net;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// Who the factory email goes to, and its switch (Factory Control, step 4). The email names an ACCOUNT, never an address:
/// the owner-email service resolves the address. A team account is told it is not supported yet, and a Gateway that
/// cannot send says why rather than pretending. The switch is off unless set, and a body that does not say on or off is
/// refused rather than guessed.
/// </summary>
public sealed class FactoryEmailSenderAndRoutesTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // Records every request the owner-email clients make, and answers with a canned body.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Answer { get; set; } = "{\"data\":{\"sent\":true,\"id\":\"msg-1\"}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            Requests.Add((request.RequestUri!.AbsolutePath, body, headers));
            return new HttpResponseMessage(Status) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }
    }

    private static FactoryEmailSender Sender(RecordingHandler handler, bool hosted, bool team = false,
        string? serviceToken = "a-service-token-that-is-at-least-thirty-two-characters", string? accountToken = "account-token") =>
        new(hosted,
            isTeam: _ => team,
            subjectOf: _ => "subject-1",
            serviceToken: () => serviceToken,
            accountToken: () => accountToken,
            byTenant: new AccountNotifyByTenantClient(new HttpClient(handler), "https://account.example.test"),
            byToken: new AccountNotifyClient(new HttpClient(handler), "https://account.example.test"));

    [Fact]
    public async Task SendAsync_Hosted_NamesTheTenant_AndNoAddress()
    {
        var handler = new RecordingHandler();

        var result = await Sender(handler, hosted: true).SendAsync(new TenantId("tenant-1"), "Factories: all clear", "text", "<p>html</p>", default);

        Assert.True(result.Sent);
        var (path, body, headers) = Assert.Single(handler.Requests);
        Assert.Equal(AccountNotifyByTenantClient.NotifyOwnerByTenantPath, path);
        Assert.Contains("tenant-1", body);
        Assert.DoesNotContain("\"to\"", body);
        Assert.DoesNotContain("@", body);
        Assert.True(headers.ContainsKey("x-devthrottle-gateway-token"));
    }

    [Fact]
    public async Task SendAsync_SelfHost_UsesTheAccountToken()
    {
        var handler = new RecordingHandler();

        var result = await Sender(handler, hosted: false).SendAsync(TenantId.Local, "s", "t", "h", default);

        Assert.True(result.Sent);
        var (path, _, headers) = Assert.Single(handler.Requests);
        Assert.Equal(AccountNotifyClient.NotifyOwnerPath, path);
        Assert.Equal("Bearer account-token", headers["authorization"]);
    }

    [Fact]
    public async Task SendAsync_ATeamAccount_IsNotSupported_AndNothingIsSent()
    {
        var handler = new RecordingHandler();

        var result = await Sender(handler, hosted: true, team: true).SendAsync(new TenantId("team-1"), "s", "t", "h", default);

        Assert.False(result.Sent);
        Assert.True(result.Final);
        Assert.Equal(FactoryEmailSender.TeamNotSupported, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_HostedWithNoServiceToken_FailsSayingWhich_AndCallsNothing()
    {
        var handler = new RecordingHandler();

        var result = await Sender(handler, hosted: true, serviceToken: null).SendAsync(new TenantId("tenant-1"), "s", "t", "h", default);

        Assert.False(result.Sent);
        Assert.False(result.Final);
        Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_SelfHostNotSignedIn_FailsSayingSo()
    {
        var handler = new RecordingHandler();

        var result = await Sender(handler, hosted: false, accountToken: null).SendAsync(TenantId.Local, "s", "t", "h", default);

        Assert.False(result.Sent);
        Assert.Contains("not signed in", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_TheServiceRefuses_IsAFailureWithItsWords()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.TooManyRequests, Answer = "{\"error\":{\"code\":\"notify_rate_limited\",\"message\":\"Too many emails for this account.\"}}" };

        var result = await Sender(handler, hosted: true).SendAsync(new TenantId("tenant-1"), "s", "t", "h", default);

        Assert.False(result.Sent);
        Assert.Contains("Too many emails for this account.", result.Error);
    }

    // ---- the switch ----------------------------------------------------------------------------------------------

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static DefaultHttpContext Put(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("not json")]
    public async Task PutSwitchAsync_WithoutABooleanEnabled_IsRefused_AndChangesNothing(string body)
    {
        var set = new List<bool>();

        var result = await FactoryEmailEndpoints.PutSwitchAsync(Put(body), _ => TenantId.Local, (_, on) => set.Add(on));

        Assert.Equal(400, Status(result));
        Assert.Empty(set);
    }

    [Fact]
    public async Task PutSwitchAsync_On_SetsIt()
    {
        var set = new List<bool>();

        var result = await FactoryEmailEndpoints.PutSwitchAsync(Put("{\"enabled\":true}"), _ => TenantId.Local, (_, on) => set.Add(on));

        Assert.Equal(200, Status(result));
        Assert.Equal(new[] { true }, set);
    }

    [Fact]
    public void FactoryEmailEnabled_NeverSet_IsOff()
    {
        var db = _h.Open();
        var resolver = new CcDirector.Gateway.Settings.TenantSettingsResolver(new CcDirector.Gateway.Settings.TenantSettingsStore(db));

        Assert.False(resolver.FactoryEmailEnabled(TenantId.Local));
        resolver.SetFactoryEmailEnabled(TenantId.Local, true, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc));
        Assert.True(resolver.FactoryEmailEnabled(TenantId.Local));
    }

    [Theory]
    [InlineData("weekly", null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, 101, null)]
    [InlineData(null, null, "failed")]
    public void ListEmails_ABadKindLimitOrState_IsRefused(string? kind, int? limit, string? state)
    {
        var store = new FactoryEmailStore(_h.Open());

        var result = FactoryEmailEndpoints.ListEmails(new DefaultHttpContext(), null, kind, limit, state, _ => TenantId.Local, store);

        Assert.Equal(400, Status(result));
    }

    [Fact]
    public void ListEmails_NothingKept_IsCountZero()
    {
        var store = new FactoryEmailStore(_h.Open());

        var result = FactoryEmailEndpoints.ListEmails(new DefaultHttpContext(), "outreach", null, null, null, _ => TenantId.Local, store);

        var json = Assert.IsType<JsonHttpResult<Contracts.FactoryEmailsDto>>(result);
        Assert.Equal((0, "outreach"), (json.Value!.Count, json.Value.Factory));
    }

    [Theory]
    [InlineData("not-an-id", 400)]
    [InlineData("4b0f0c6e-0000-4000-8000-000000000001", 404)]
    public void GetEmail_ABadOrUnknownId_SaysWhich(string id, int status)
    {
        var store = new FactoryEmailStore(_h.Open());

        Assert.Equal(status, Status(FactoryEmailEndpoints.GetEmail(new DefaultHttpContext(), id, _ => TenantId.Local, store)));
    }
}
