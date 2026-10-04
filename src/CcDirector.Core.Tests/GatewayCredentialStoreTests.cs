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
    public void SaveEnrolledKey_EnablesStreamMode()
    {
        // Connecting to a Gateway makes the stream the connection method (issue #1176): the persisted
        // gateway block must carry streamMode=true so a freshly-enrolled Director joins over the stream.
        GatewayCredentialStore.SaveEnrolledKey("https://gw.example.ts.net", "test-per-device-key-abcdef0123456789");

        var config = GatewayConfig.Load();
        Assert.True(config.StreamMode, "SaveEnrolledKey must enable stream mode so connect uses the stream");
    }

    [Fact]
    public void SaveEnrolledKey_DeepMerge_PreservesExistingGatewayKeys()
    {
        // A hand-set gateway key (here staleAfterSeconds) must survive the enroll write, since the
        // credential store deep-merges rather than replacing the whole gateway block.
        var configPath = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """{ "gateway": { "staleAfterSeconds": 45 } }""");

        GatewayCredentialStore.SaveEnrolledKey("https://gw.example.ts.net", "test-per-device-key-abcdef0123456789");

        var gateway = (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject)?["gateway"] as JsonObject;
        Assert.NotNull(gateway);
        Assert.Equal(45, (int?)gateway["staleAfterSeconds"]);
        Assert.True((bool?)gateway["streamMode"]);
    }

    [Fact]
    public void SaveEnrolledKey_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            GatewayCredentialStore.SaveEnrolledKey("https://gw.example.ts.net", ""));
    }

    [Fact]
    public void ClearConnection_IsTheInverseOfSaveEnrolledKey_ClearsTheSavedFileAndConfig()
    {
        // A GENUINE save -> clear inverse: SAVE writes the credential to the CredentialFile static path (whose
        // root is locked at first assembly access) and records the gateway block; CLEAR must remove the SAME
        // file the save wrote and blank the gateway block. Asserting on GatewayCredentialStore.CredentialFile
        // (the static path) pins the static-path-cache gotcha: if ClearConnection recomputed a fresh-root path
        // instead, it would delete a DIFFERENT file and the saved one would survive - reddening this test.
        const string url = "https://gw.example.ts.net";
        const string key = "test-per-device-key-abcdef0123456789";

        GatewayCredentialStore.SaveEnrolledKey(url, key);
        Assert.True(File.Exists(GatewayCredentialStore.CredentialFile),
            "SaveEnrolledKey must write the per-device key file");
        Assert.True(GatewayConfig.Load().IsEnabled);

        GatewayCredentialStore.ClearConnection();

        Assert.False(File.Exists(GatewayCredentialStore.CredentialFile),
            "ClearConnection must delete the SAME credential file SaveEnrolledKey wrote");
        var config = GatewayConfig.Load();
        Assert.False(config.IsEnabled);
        Assert.Equal("", config.Url);
        Assert.Equal("", config.Token);
        Assert.Empty(config.Urls);
        Assert.False(config.StreamMode);
    }

    [Fact]
    public void ClearConnection_ClearsTheDiscoveredFallbackUrls()
    {
        // A connection discovered from the account also seeds gateway.urls (issue #1233); disconnect must
        // clear that fallback list too, not just the active url.
        var configPath = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath,
            """{ "gateway": { "url": "https://gw.example.ts.net", "token": "k", "urls": ["https://a:7878", "https://b:7878"] } }""");
        Assert.Equal(2, GatewayConfig.Load().Urls.Count);

        GatewayCredentialStore.ClearConnection();

        Assert.Empty(GatewayConfig.Load().Urls);
    }

    [Fact]
    public void ClearConnection_PreservesUnrelatedConfigSections()
    {
        // Disconnect touches ONLY the gateway block; a sibling section (here screenshots) must survive.
        var configPath = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath,
            """{ "gateway": { "url": "https://gw.example.ts.net", "token": "k" }, "screenshots": { "dir": "D:\\shots" } }""");

        GatewayCredentialStore.ClearConnection();

        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
        var screenshots = root?["screenshots"] as JsonObject;
        Assert.NotNull(screenshots);
        Assert.Equal("D:\\shots", (string?)screenshots["dir"]);
    }

    [Fact]
    public void ClearConnection_NoCredentialFile_DoesNotThrow()
    {
        // Disconnecting a never-connected Director (no credential file yet) is a no-op clear, not an error.
        GatewayCredentialStore.ClearConnection();
        Assert.False(GatewayConfig.Load().IsEnabled);
    }

    [Fact]
    public void ClearConnection_ForgetsTheTeamThatCameWithTheKey()
    {
        // devthrottle_internal#2311: the team is the key's team, so with the key gone the chip must go too.
        GatewayCredentialStore.SaveEnrolledKey("https://gateway.example", "team-key");
        CcDirector.Core.Teams.DirectorTeamStore.Save(new CcDirector.Core.Teams.DirectorTeam("t-dev", "DevThrottle"));

        GatewayCredentialStore.ClearConnection();

        Assert.Null(CcDirector.Core.Teams.DirectorTeamStore.Load());
    }
}
