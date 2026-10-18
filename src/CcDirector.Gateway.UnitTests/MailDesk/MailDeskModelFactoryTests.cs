using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// The Mail Desk model's factory: built from the key vault and the Wingman endpoint the way the dictionary-suggestion
/// brain is, and - when the Gateway cannot ask - a model whose every question fails with a plain sentence and makes no
/// call. The key reader is a fake; no vault, no network.
/// </summary>
[Trait("Category", "MailDesk")]
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MailDeskModelFactoryTests : IDisposable
{
    private const string Key = "dt_live_mail_desk_factory_key";
    private const string ServiceToken = "gateway-service-token-for-the-tests-0123456789";
    private static readonly TenantId Account = new("6f1d2c3b-0000-4000-8000-00000000abcd");
    private static readonly MailDeskEmail Email = new("Sender Name", "someone@example.com", "A subject", "A preview");
    private static readonly IReadOnlyList<MailDeskRouteChoice> OneRoute = new[] { new MailDeskRouteChoice("support", "support") };

    private readonly string? _priorToken = Environment.GetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, _priorToken);

    private static string Completion(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 100, completion_tokens = 1 },
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_NoKeyInTheVault_EveryQuestionFailsWithTheSentenceAndNoCallIsMade(string? storedKey)
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A"));
        var model = MailDeskModelFactory.Create(TenantId.Local, TenantAiGate.SelfHost, _ => storedKey, TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { });

        // Act
        var spam = await model.AskPersonOrSpamAsync(Email);
        var route = await model.AskRouteAsync(Email, OneRoute);

        // Assert
        foreach (var result in new[] { spam, route })
        {
            Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
            Assert.Equal(MailDeskModelErrorKinds.NoKey, result.ErrorKind);
            Assert.Equal("the Gateway holds no key for the hosted model", result.Error);
            Assert.Null(result.CostUsd);
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_AKey_AsksTheWingmanEndpointWithTheFastModelAndTheMailDeskTag()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("B"));
        string? askedFor = null;
        var model = MailDeskModelFactory.Create(TenantId.Local, TenantAiGate.SelfHost, name => { askedFor = name; return Key; },
            TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { });

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal("spam", result.Label);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleKeyName, askedFor);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl + "/chat/completions", req.Uri);
        Assert.Equal("Bearer " + Key, req.Header("Authorization"));
        Assert.Equal(AiFeature.MailDesk, req.Header(AiCallTag.FeatureHeader));
        Assert.Null(req.Header(AiCallTag.AccountHeader)); // the self-host single tenant names no account
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(IncludedModelId.WingmanFast.Value, doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Create_AHostedAccountTenant_NamesTheAccountOnTheCall()
    {
        // Arrange
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, ServiceToken);
        var handler = FakeHandler.Answering(Completion("A"));
        var model = MailDeskModelFactory.Create(Account, TenantAiGate.SelfHost, _ => Key, TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { });

        // Act
        await model.AskPersonOrSpamAsync(Email);

        // Assert
        var req = Assert.Single(handler.Requests);
        Assert.Equal("mail-desk", req.Header(AiCallTag.FeatureHeader));
        Assert.Equal(Account.Value, req.Header(AiCallTag.AccountHeader));
        Assert.Equal(ServiceToken, req.Header(AiCallTag.ServiceTokenHeader));
    }

    [Fact]
    public async Task Create_AHostedAccountTenantWithoutTheServiceCredential_EveryQuestionFailsNamingTheSetting()
    {
        // Arrange
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, null);
        var handler = FakeHandler.Answering(Completion("A"));
        var model = MailDeskModelFactory.Create(Account, TenantAiGate.SelfHost, _ => Key, TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { });

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.NoAccountCredential, result.ErrorKind);
        Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_TheAiGateRefusesTheAccount_EveryQuestionFailsWithTheGatesSentenceAndNoKeyIsRead()
    {
        // Arrange: a hosted gate (entitlements wired) refuses a call with no account tenant to check, before any read.
        using var harness = new GatewayDbTestHarness();
        var db = harness.Open();
        var gate = new TenantAiGate(new EntitlementRegistry(db, requireLivemode: true), new TenantRegistry(db), log: _ => { });
        var handler = FakeHandler.Answering(Completion("A"));
        var keyRead = false;
        var model = MailDeskModelFactory.Create(TenantId.System, gate, _ => { keyRead = true; return Key; },
            TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { });

        // Act
        var spam = await model.AskPersonOrSpamAsync(Email);
        var route = await model.AskRouteAsync(Email, OneRoute);

        // Assert
        foreach (var result in new[] { spam, route })
        {
            Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
            Assert.Equal(MailDeskModelErrorKinds.PlanRefused, result.ErrorKind);
            Assert.Contains("scope=wingman", result.Error, StringComparison.Ordinal);
            Assert.Contains("no account tenant to check the plan of", result.Error, StringComparison.Ordinal);
        }
        Assert.False(keyRead);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Create_AnyOutcome_NeverLogsTheKey()
    {
        // Arrange
        var log = new List<string>();

        // Act
        MailDeskModelFactory.Create(TenantId.Local, TenantAiGate.SelfHost, _ => Key, TranscriptionMode.DevThrottle, new HttpClient(FakeHandler.Answering("")), log.Add);
        MailDeskModelFactory.Create(TenantId.Local, TenantAiGate.SelfHost, _ => null, TranscriptionMode.DevThrottle, new HttpClient(FakeHandler.Answering("")), log.Add);

        // Assert
        Assert.Equal(2, log.Count);
        Assert.DoesNotContain(log, line => line.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public void MailDeskFeatureName_IsMailDeskAndAValidTag()
    {
        // Act
        var tag = new AiCallTag(AiFeature.MailDesk);

        // Assert
        Assert.Equal("mail-desk", tag.Feature);
    }
}
