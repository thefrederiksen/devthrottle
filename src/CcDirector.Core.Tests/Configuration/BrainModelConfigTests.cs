using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="BrainModelConfig"/> (issue #204), the Gateway brain's model pin.
/// The contract: missing file/key = the smartest-tier default ("opus"), a set value is
/// returned trimmed, and a present-but-invalid value THROWS (no-fallback - the brain
/// must never silently run on a model nobody chose).
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class BrainModelConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public BrainModelConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-brainmodel-test-" + Guid.NewGuid().ToString("N"));
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
    public void Get_MissingConfig_ReturnsDefault()
    {
        Assert.Equal(BrainModelConfig.Default, BrainModelConfig.Get());
    }

    [Fact]
    public void Get_KeyAbsent_ReturnsDefault()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw.example:7878" } }""");
        Assert.Equal(BrainModelConfig.Default, BrainModelConfig.Get());
    }

    [Fact]
    public void Get_KeySet_ReturnsValueTrimmed()
    {
        SeedConfig("""{ "brain_model": "  claude-opus-4-8  " }""");
        Assert.Equal("claude-opus-4-8", BrainModelConfig.Get());
    }

    [Fact]
    public void Get_BlankValue_Throws()
    {
        SeedConfig("""{ "brain_model": "   " }""");
        var ex = Assert.Throws<InvalidOperationException>(() => BrainModelConfig.Get());
        Assert.Contains("brain_model", ex.Message);
    }

    [Fact]
    public void Get_NonStringValue_Throws()
    {
        SeedConfig("""{ "brain_model": 42 }""");
        var ex = Assert.Throws<InvalidOperationException>(() => BrainModelConfig.Get());
        Assert.Contains("brain_model", ex.Message);
    }
}
