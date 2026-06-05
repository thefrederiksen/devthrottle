using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="CcDirectorConfigService"/>, the single round-trip-preserving
/// writer for config.json. The whole point is that a partial write must NEVER drop
/// sibling sections (the historical data-loss bug), so most tests assert that an
/// untouched block survives a targeted patch. All methods share an isolated
/// CC_DIRECTOR_ROOT set in the constructor; xUnit runs a class's methods sequentially.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class CcDirectorConfigServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public CcDirectorConfigServiceTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-config-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void MergePatch_preserves_sibling_sections()
    {
        SeedConfig("""
        {
          "gateway": { "url": "http://gw.example:7878", "token": "abc" },
          "screenshots": { "source_directory": "C:/old" }
        }
        """);

        var patch = new JsonObject
        {
            ["screenshots"] = new JsonObject { ["source_directory"] = "/Users/username/Desktop" },
        };
        CcDirectorConfigService.MergePatch(patch);

        var result = CcDirectorConfigService.ReadRaw();
        var screenshots = result["screenshots"];
        Assert.NotNull(screenshots);
        Assert.Equal("/Users/username/Desktop", (string?)screenshots["source_directory"]);
        // The untouched gateway block must survive intact.
        var gateway = result["gateway"];
        Assert.NotNull(gateway);
        Assert.Equal("http://gw.example:7878", (string?)gateway["url"]);
        Assert.Equal("abc", (string?)gateway["token"]);
    }

    [Fact]
    public void MergePatch_merges_nested_keys_without_dropping_peers()
    {
        SeedConfig("""
        { "gateway": { "url": "http://gw.example:7878", "token": "keep-me" } }
        """);

        // Patch only the url; token (a peer inside the same object) must remain.
        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://new.example:7878" },
        };
        CcDirectorConfigService.MergePatch(patch);

        var result = CcDirectorConfigService.ReadRaw();
        var gateway = result["gateway"];
        Assert.NotNull(gateway);
        Assert.Equal("http://new.example:7878", (string?)gateway["url"]);
        Assert.Equal("keep-me", (string?)gateway["token"]);
    }

    [Fact]
    public void MergePatch_creates_section_when_absent()
    {
        SeedConfig("""{ "llm": { "default_provider": "claude_code" } }""");

        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        };
        CcDirectorConfigService.MergePatch(patch);

        var result = CcDirectorConfigService.ReadRaw();
        var gateway = result["gateway"];
        Assert.NotNull(gateway);
        Assert.Equal("http://gw.example:7878", (string?)gateway["url"]);
        var llm = result["llm"];
        Assert.NotNull(llm);
        Assert.Equal("claude_code", (string?)llm["default_provider"]);
    }

    [Fact]
    public void MergePatch_on_missing_file_creates_it()
    {
        // No SeedConfig: file does not exist yet.
        var patch = new JsonObject
        {
            ["screenshots"] = new JsonObject { ["source_directory"] = "/tmp/shots" },
        };
        CcDirectorConfigService.MergePatch(patch);

        Assert.True(File.Exists(CcStorage.ConfigJson()));
        var result = CcDirectorConfigService.ReadRaw();
        var screenshots = result["screenshots"];
        Assert.NotNull(screenshots);
        Assert.Equal("/tmp/shots", (string?)screenshots["source_directory"]);
    }

    [Fact]
    public void ReadRaw_throws_on_malformed_json_and_leaves_file_untouched()
    {
        SeedConfig("{ this is not valid json ");
        var before = File.ReadAllText(CcStorage.ConfigJson());

        Assert.ThrowsAny<Exception>(() => CcDirectorConfigService.ReadRaw());
        // The unparseable file must NOT have been reset or truncated.
        Assert.Equal(before, File.ReadAllText(CcStorage.ConfigJson()));
    }

    [Fact]
    public void MergePatch_refuses_to_clobber_a_malformed_file()
    {
        SeedConfig("{ broken ");
        var patch = new JsonObject { ["gateway"] = new JsonObject { ["url"] = "x" } };

        // MergePatch reads first; a malformed read throws before any write happens.
        Assert.ThrowsAny<Exception>(() => CcDirectorConfigService.MergePatch(patch));
        Assert.Equal("{ broken ", File.ReadAllText(CcStorage.ConfigJson()));
    }
}
