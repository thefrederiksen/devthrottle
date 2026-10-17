using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.UnitTests.Instances;

/// <summary>
/// <see cref="NamedInstanceRegistry.Clone"/> (owner request, 2026-10-17, with issue devthrottle_internal#2489):
/// a new instance with the source's settings in a folder of its own. Each test runs against a throwaway shared
/// root; ConfigEnvSerial keeps it from racing the other classes that set the process-wide CC_DIRECTOR_ROOT.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class NamedInstanceCloneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-clone-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");

    public NamedInstanceCloneTests()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        InstanceContext.Initialize(null, wasExplicit: false);
        NamedInstanceRegistry.EnsureDefault("MY-BOX");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        InstanceContext.Initialize(null, wasExplicit: false);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string HomeFile(string slug, params string[] parts)
        => Path.Combine(new[] { _root, "instances", slug }.Concat(parts).ToArray());

    private void WriteDefaultConfig(string json)
    {
        var path = HomeFile("default", "config", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Clone_RegistersANewInstanceWithTheSourceSettingsButNotItsKey()
    {
        WriteDefaultConfig("""{"gateway":{"url":"https://gw.example","token":"device-key"},"screenshots":{"source_directory":"/shots"}}""");

        var clone = NamedInstanceRegistry.Clone("default", "Tool health test");

        Assert.Equal("tool-health-test", clone.Name);
        Assert.Equal("https://gw.example", clone.GatewayUrl);
        Assert.NotNull(NamedInstanceRegistry.Get("tool-health-test"));
        var config = File.ReadAllText(HomeFile("tool-health-test", "config", "config.json"));
        Assert.Contains("/shots", config);
        Assert.DoesNotContain("device-key", config);
    }

    [Fact]
    public void Clone_SourceWithNoConfig_StillGetsAGatewayBlock()
    {
        var clone = NamedInstanceRegistry.Clone("default", "Fresh copy");

        Assert.True(File.Exists(HomeFile(clone.Name, "config", "config.json")));
    }

    [Fact]
    public void Clone_TargetFolderAlreadyHoldsFiles_IsRefusedAndNotRegistered()
    {
        // An old Director's id left in a folder must never become the clone's.
        var leftover = HomeFile("tool-health-test", "config", "director", "director-id-old.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "old-id");

        Assert.Throws<InvalidOperationException>(() => NamedInstanceRegistry.Clone("default", "Tool health test"));

        Assert.Null(NamedInstanceRegistry.Get("tool-health-test"));
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public void Clone_CopyFails_LeavesNoInstanceAndNoFolder()
    {
        // A config.json that is not a JSON object is refused by the copy.
        WriteDefaultConfig("[1,2]");

        Assert.Throws<InvalidOperationException>(() => NamedInstanceRegistry.Clone("default", "Broken copy"));

        Assert.Null(NamedInstanceRegistry.Get("broken-copy"));
        Assert.False(Directory.Exists(HomeFile("broken-copy")));
    }

    [Fact]
    public void Clone_UnknownSource_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => NamedInstanceRegistry.Clone("ghost", "Copy"));
    }
}
