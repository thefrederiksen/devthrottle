using CcDirector.Core.Configuration;
using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The WIRING proof for <see cref="MailDeskModelEndpointConfig"/>: a real <see cref="GatewayHost"/>, built over a
/// scratch storage root whose config.json is written by the test, reads the key once at construction. With no key the
/// Mail Desk asks the hosted endpoint (production unchanged); with a loopback key it asks that address; with any other
/// value the Gateway does not start.
/// </summary>
public sealed class MailDeskModelEndpointWiringTests : IDisposable
{
    private readonly string? _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-maildesk-endpoint-" + Guid.NewGuid().ToString("N"));

    public MailDeskModelEndpointWiringTests()
    {
        Directory.CreateDirectory(Path.Combine(_storageRoot, "config"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task GatewayHost_NoKeyInConfig_AsksTheHostedEndpoint()
    {
        // Arrange
        WriteConfig("{ \"factoryAgents\": { \"enabled\": true } }");

        // Act
        var gateway = NewGateway();
        try
        {
            // Assert
            Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, gateway.MailDeskModelBaseUrl);
        }
        finally
        {
            await gateway.StopAsync();
        }
    }

    [Fact]
    public async Task GatewayHost_LoopbackKeyInConfig_AsksThatAddress()
    {
        // Arrange
        WriteConfig($"{{ \"{MailDeskModelEndpointConfig.Key}\": \"http://127.0.0.1:7993/v1\" }}");

        // Act
        var gateway = NewGateway();
        try
        {
            // Assert
            Assert.Equal("http://127.0.0.1:7993/v1", gateway.MailDeskModelBaseUrl);
        }
        finally
        {
            await gateway.StopAsync();
        }
    }

    [Fact]
    public void GatewayHost_KeyNamingAnotherHost_DoesNotStart()
    {
        // Arrange
        WriteConfig($"{{ \"{MailDeskModelEndpointConfig.Key}\": \"https://api.example.com/v1\" }}");

        // Act
        var ex = Record.Exception(NewGateway);

        // Assert
        var refusal = Assert.IsType<InvalidOperationException>(ex is System.Reflection.TargetInvocationException t ? t.InnerException : ex);
        Assert.Contains(MailDeskModelEndpointConfig.Key, refusal.Message);
    }

    private void WriteConfig(string json)
        => File.WriteAllText(Path.Combine(_storageRoot, "config", "config.json"), json);

    private GatewayHost NewGateway() => new(
        port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: false,
        instancesDirectory: Path.Combine(_storageRoot, "instances"),
        workListsPath: Path.Combine(_storageRoot, "worklists", "worklists.json"),
        keyVaultPath: Path.Combine(_storageRoot, "keyvault.json"));
}
