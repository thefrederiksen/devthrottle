using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// The rig's way to point a throwaway Gateway's Mail Desk at a fake model: config.json's <c>mailDeskModelBaseUrl</c>,
/// honoured only for a loopback host, refused for anything else, and absent meaning the hosted endpoint. Pure parsing
/// of one config document; no file, no network.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskModelEndpointConfigTests
{
    private static JsonObject? Config(string json) => JsonNode.Parse(json) as JsonObject;

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"factoryAgents\": { \"enabled\": true } }")]
    public void Parse_KeyAbsent_ReturnsNullSoTheHostedEndpointIsUsed(string json)
    {
        // Arrange
        var root = Config(json);

        // Act
        var configured = MailDeskModelEndpointConfig.Parse(root);

        // Assert
        Assert.Null(configured);
        Assert.Null(MailDeskModelEndpointConfig.Parse(null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:7993/v1")]
    [InlineData("http://localhost:7993/v1")]
    [InlineData("http://LOCALHOST:7993")]
    [InlineData("http://[::1]:7993/v1")]
    [InlineData("https://127.0.0.1:7993/v1")]
    public void Parse_LoopbackHost_IsHonoured(string url)
    {
        // Arrange
        var root = new JsonObject { [MailDeskModelEndpointConfig.Key] = url };

        // Act
        var configured = MailDeskModelEndpointConfig.Parse(root);

        // Assert
        Assert.Equal(url, configured);
    }

    [Theory]
    [InlineData("\"https://devthrottle.com/api/v1\"", "its host devthrottle.com is not")]
    [InlineData("\"https://api.example.com/v1\"", "its host api.example.com is not")]
    [InlineData("\"http://127.0.0.2:7993/v1\"", "its host 127.0.0.2 is not")]
    [InlineData("\"http://10.0.0.5:7993/v1\"", "its host 10.0.0.5 is not")]
    [InlineData("\"http://localhost.example.com/v1\"", "its host localhost.example.com is not")]
    [InlineData("\"ftp://127.0.0.1/v1\"", "its scheme is ftp")]
    [InlineData("\"http://user:secret@127.0.0.1:7993/v1\"", "a user name, a query or a fragment")]
    [InlineData("\"http://127.0.0.1:7993/v1?x=1\"", "a user name, a query or a fragment")]
    [InlineData("\"127.0.0.1:7993\"", "")]
    [InlineData("\"\"", "it is empty")]
    [InlineData("\"   \"", "it is empty")]
    [InlineData("42", "it is not a string")]
    [InlineData("null", "it is not a string")]
    [InlineData("{ \"url\": \"http://127.0.0.1\" }", "it is not a string")]
    public void Parse_AnythingButALoopbackAddress_RefusesWithASentenceNamingTheKeyAndTheRule(string value, string why)
    {
        // Arrange
        var root = Config($"{{ \"{MailDeskModelEndpointConfig.Key}\": {value} }}");

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => MailDeskModelEndpointConfig.Parse(root));

        // Assert
        Assert.Contains("The Gateway will not start", ex.Message);
        Assert.Contains(MailDeskModelEndpointConfig.Key, ex.Message);
        Assert.Contains("127.0.0.1, ::1 or localhost", ex.Message);
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public async Task Create_WithTheConfiguredBaseUrl_AsksThatAddressWithTheVaultKey()
    {
        // Arrange
        var handler = FakeHandler.Answering(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"A\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}");
        var model = MailDeskModelFactory.Create(TenantId.Local, TenantAiGate.SelfHost, _ => "rig-only-key",
            TranscriptionMode.DevThrottle, new HttpClient(handler), _ => { }, baseUrl: "http://127.0.0.1:7993/v1");

        // Act
        var result = await model.AskPersonOrSpamAsync(new MailDeskEmail("A Sender", "someone@example.com", "A subject", "A preview"));

        // Assert
        Assert.Equal("person", result.Label);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:7993/v1/chat/completions", req.Uri);
    }
}
