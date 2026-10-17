using System.Text.Json;
using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.Tests.Instances;

/// <summary>
/// Tests for <see cref="NamedInstanceRegistry"/> - the cross-instance registry of named
/// Director profiles. Each test runs against an isolated shared root (temp dir), captured
/// by <see cref="InstanceContext.Initialize"/> after CC_DIRECTOR_ROOT is set. xUnit runs a
/// class's methods sequentially; the collection serializes classes that mutate the
/// process-wide CC_DIRECTOR_ROOT / InstanceContext.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class NamedInstanceRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public NamedInstanceRegistryTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-instances-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // Capture the temp dir as the shared root; run as the default instance.
        InstanceContext.Initialize(null, wasExplicit: false);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnsureDefault_seeds_default_with_hostname()
    {
        NamedInstanceRegistry.EnsureDefault("MY-BOX");

        var def = NamedInstanceRegistry.Get("default");
        Assert.NotNull(def);
        Assert.True(def!.IsDefault);
        Assert.Equal("default", def.Name);
        Assert.Equal("MY-BOX", def.DisplayName);
    }

    [Fact]
    public void List_always_includes_default_first()
    {
        NamedInstanceRegistry.Create("Company B", "https://gw-b.example", "tok");

        var all = NamedInstanceRegistry.List();
        Assert.True(all.Count >= 2);
        Assert.True(all[0].IsDefault); // default sorts first
    }

    [Fact]
    public void Create_assigns_slug_and_scaffolds_gateway_config()
    {
        var inst = NamedInstanceRegistry.Create("Company B", "https://gw-b.example", "secret-token");

        Assert.Equal("company-b", inst.Name);
        Assert.False(string.IsNullOrEmpty(inst.Id));
        Assert.Equal("Company B", inst.DisplayName);

        // The instance's own config.json must carry the gateway block.
        var configPath = Path.Combine(_root, "instances", "company-b", "config", "config.json");
        Assert.True(File.Exists(configPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var gw = doc.RootElement.GetProperty("gateway");
        Assert.Equal("https://gw-b.example", gw.GetProperty("url").GetString());
        Assert.Equal("secret-token", gw.GetProperty("token").GetString());
    }

    [Fact]
    public void Create_duplicate_display_name_gets_distinct_slug()
    {
        var a = NamedInstanceRegistry.Create("Company B", "", "");
        var b = NamedInstanceRegistry.Create("Company B", "", "");

        Assert.Equal("company-b", a.Name);
        Assert.Equal("company-b-2", b.Name);
    }

    [Fact]
    public void Create_never_takes_the_reserved_default_slug()
    {
        var inst = NamedInstanceRegistry.Create("Default", "", "");
        Assert.NotEqual("default", inst.Name);
        // The seeded default instance still exists and is unaffected.
        Assert.NotNull(NamedInstanceRegistry.Get("default"));
    }

    [Fact]
    public void Rename_changes_display_name_only()
    {
        var inst = NamedInstanceRegistry.Create("Company B", "https://gw-b.example", "tok");
        var originalSlug = inst.Name;
        var originalId = inst.Id;

        NamedInstanceRegistry.Rename(originalSlug, "Client B");

        var after = NamedInstanceRegistry.Get(originalSlug);
        Assert.NotNull(after);
        Assert.Equal("Client B", after!.DisplayName);
        Assert.Equal(originalSlug, after.Name);   // slug unchanged
        Assert.Equal(originalId, after.Id);       // id unchanged
    }

    [Fact]
    public void Get_returns_null_for_unknown_slug()
    {
        Assert.Null(NamedInstanceRegistry.Get("does-not-exist"));
    }

    [Fact]
    public void Default_instance_home_is_isolated_not_the_shared_root()
    {
        // No migration / no shared-root fallback: even the default runs in instances\default.
        InstanceContext.Initialize(null, wasExplicit: false);
        Assert.True(InstanceContext.IsDefault);
        Assert.Equal(Path.Combine(_root, "instances", "default"), InstanceContext.InstanceHome);
        Assert.NotEqual(InstanceContext.SharedRoot, InstanceContext.InstanceHome);
    }

    [Fact]
    public void Named_instance_home_is_under_its_slug()
    {
        InstanceContext.Initialize("company-b", wasExplicit: true);
        Assert.Equal(Path.Combine(_root, "instances", "company-b"), InstanceContext.InstanceHome);
    }

    [Fact]
    public void Malformed_registry_throws_rather_than_reset()
    {
        var path = NamedInstanceRegistry.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json ");

        Assert.ThrowsAny<Exception>(() => NamedInstanceRegistry.List());
        // The unparseable file must NOT have been reset.
        Assert.Equal("{ not valid json ", File.ReadAllText(path));
    }

    // ---- Clone (owner request, 2026-10-17, with issue devthrottle_internal#2489) ----

    [Fact]
    public void Clone_RegistersANewInstanceWithTheSourceGatewayAndSettings()
    {
        // Arrange: a source instance with a gateway and an extra setting in its own home.
        var source = NamedInstanceRegistry.Create("Company B", "https://gw-b.example", "secret-token");
        var sourceConfig = Path.Combine(_root, "instances", source.Name, "config", "config.json");
        var withSetting = File.ReadAllText(sourceConfig).TrimEnd().TrimEnd('}') + ",\"screenshots\":{\"source_directory\":\"/shots\"}}";
        File.WriteAllText(sourceConfig, withSetting);

        // Act
        var clone = NamedInstanceRegistry.Clone(source.Name, "Company B test");

        // Assert
        Assert.Equal("company-b-test", clone.Name);
        Assert.Equal("https://gw-b.example", clone.GatewayUrl);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.NotNull(NamedInstanceRegistry.Get("company-b-test"));
        var cloneConfig = File.ReadAllText(Path.Combine(_root, "instances", "company-b-test", "config", "config.json"));
        Assert.Contains("secret-token", cloneConfig);
        Assert.Contains("/shots", cloneConfig);
    }

    [Fact]
    public void Clone_SourceWithNoConfig_StillGetsAGatewayBlock()
    {
        NamedInstanceRegistry.EnsureDefault("MY-BOX");

        var clone = NamedInstanceRegistry.Clone("default", "Fresh copy");

        Assert.True(File.Exists(Path.Combine(_root, "instances", clone.Name, "config", "config.json")));
    }

    [Fact]
    public void Clone_SourceWithNoRegistryGatewayUrl_TakesTheOneInItsConfig()
    {
        NamedInstanceRegistry.EnsureDefault("MY-BOX");
        var config = Path.Combine(_root, "instances", "default", "config", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"gateway\":{\"url\":\"https://gw.example\",\"token\":\"k\"}}");

        var clone = NamedInstanceRegistry.Clone("default", "Copy of default");

        Assert.Equal("https://gw.example", clone.GatewayUrl);
    }

    [Fact]
    public void Clone_UnknownSource_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => NamedInstanceRegistry.Clone("ghost", "Copy"));
    }
}
