using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The "no account has room" owner email (License Switching): sent by tenant through the account service, the way
/// <c>POST /account/email</c> sends on the hosted Gateway. The account service here is a message handler in memory.
/// What these hold down: the request names the tenant and never an address; only the service's own confirmation counts
/// as sent; a self-hosted Gateway and a Gateway with no service secret send nothing and say which; every failure comes
/// back as "not sent, try again" rather than as an exception.
/// </summary>
public sealed class LicenseOwnerMailerTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly LicenseOwnerEmail Email = new("account-a@example.com", "DevThrottle: no Claude account has room", "Nothing was switched.");

    private sealed class AccountService(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LicenseOwnerMailer Mailer(AccountService service, bool hosted = true, string? serviceToken = "the-service-secret")
        => new(hosted, new AccountNotifyByTenantClient(new HttpClient(service), "https://account.example.com"),
            _ => "subject-of-tenant-1", () => serviceToken);

    [Fact]
    public async Task SendAsync_Hosted_NamesTheTenantNeverAnAddress_AndIsSentOnlyOnTheServicesConfirmation()
    {
        var service = new AccountService(_ => Json(HttpStatusCode.OK, """{"data":{"sent":true,"id":"m-1"}}"""));

        var result = await Mailer(service).SendAsync(Tenant, Email, CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Sent, result.Outcome);
        var (request, body) = Assert.Single(service.Calls);
        Assert.Equal("https://account.example.com" + AccountNotifyByTenantClient.NotifyOwnerByTenantPath, request.RequestUri?.ToString());
        Assert.Equal("the-service-secret", Assert.Single(request.Headers.GetValues(AccountNotifyByTenantClient.ServiceTokenHeader)));
        using var sent = JsonDocument.Parse(body);
        Assert.Equal(new[] { "account_subject", "subject", "tenant_id", "text" },
            sent.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(("tenant-1", "subject-of-tenant-1", Email.Subject, Email.BodyText),
            (sent.RootElement.GetProperty("tenant_id").GetString(), sent.RootElement.GetProperty("account_subject").GetString(),
             sent.RootElement.GetProperty("subject").GetString(), sent.RootElement.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task SendAsync_ASuccessStatusThatDoesNotSaySent_IsNotSent()
    {
        var service = new AccountService(_ => Json(HttpStatusCode.OK, """{"data":{}}"""));

        var result = await Mailer(service).SendAsync(Tenant, Email, CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task SendAsync_RefusedWithAWait_IsFailed_AndCarriesTheWait()
    {
        var service = new AccountService(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"notify_rate_limited","message":"Too many owner emails; try again later."}}""");
            response.Headers.Add("Retry-After", "180");
            return response;
        });

        var result = await Mailer(service).SendAsync(Tenant, Email, CancellationToken.None);

        Assert.Equal((LicenseOwnerEmailOutcome.Failed, "Too many owner emails; try again later.", TimeSpan.FromSeconds(180)),
            (result.Outcome, result.Detail, result.RetryAfter));
    }

    [Fact]
    public async Task SendAsync_TheServiceCannotBeReached_IsFailed_NotAnException()
    {
        var service = new AccountService(_ => throw new HttpRequestException("No such host."));

        var result = await Mailer(service).SendAsync(Tenant, Email, CancellationToken.None);

        Assert.Equal((LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached"), (result.Outcome, result.Detail));
    }

    [Fact]
    public async Task SendAsync_OnASelfHostedGateway_SendsNothing_AndSaysThereIsNoAccount()
    {
        var service = new AccountService(_ => Json(HttpStatusCode.OK, """{"data":{"sent":true}}"""));

        var result = await Mailer(service, hosted: false).SendAsync(TenantId.Local, Email, CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.NoAccount, result.Outcome);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task SendAsync_WithNoServiceSecret_SendsNothing_AndIsFailedSoItIsTriedAgain()
    {
        var service = new AccountService(_ => Json(HttpStatusCode.OK, """{"data":{"sent":true}}"""));

        var result = await Mailer(service, serviceToken: null).SendAsync(Tenant, Email, CancellationToken.None);

        Assert.Equal(LicenseOwnerEmailOutcome.Failed, result.Outcome);
        Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, result.Detail);
        Assert.Empty(service.Calls);
    }
}
