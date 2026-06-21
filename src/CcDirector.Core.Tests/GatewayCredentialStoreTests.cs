using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// After enrollment (issue #469) the Director must write the issued per-device key to the local
/// credential file the Director Control API and local cc-* tools both read, and record the Gateway
/// URL + key in config.json so the running client presents the per-device key. Redirects
/// CC_DIRECTOR_ROOT to a temp dir so the real user's files are never touched.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class GatewayCredentialStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public GatewayCredentialStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"credstore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SaveEnrolledKey_WritesKeyToCredentialFile()
    {
        const string key = "test-per-device-key-abcdef0123456789";

        GatewayCredentialStore.SaveEnrolledKey("https://gw.example.ts.net", key);

        var credentialFile = Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");
        Assert.True(File.Exists(credentialFile), $"credential file not written at {credentialFile}");
        Assert.Equal(key, File.ReadAllText(credentialFile));
    }

    [Fact]
    public void SaveEnrolledKey_RecordsUrlAndKeyInConfigJson()
    {
        const string key = "test-per-device-key-abcdef0123456789";
        const string url = "https://gw.example.ts.net";

        GatewayCredentialStore.SaveEnrolledKey(url, key);

        var configJson = File.ReadAllText(CcStorage.ConfigJson());
        var root = JsonNode.Parse(configJson) as JsonObject;
        Assert.NotNull(root);
        var gateway = root["gateway"] as JsonObject;
        Assert.NotNull(gateway);
        Assert.Equal(url, (string?)gateway["url"]);
        Assert.Equal(key, (string?)gateway["token"]);
    }

    [Fact]
    public void SaveEnrolledKey_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            GatewayCredentialStore.SaveEnrolledKey("https://gw.example.ts.net", ""));
    }
}
