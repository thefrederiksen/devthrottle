using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class ReleaseManifestTests
{
    private const string PerAssetManifest = """
    {
      "version": "0.4.0",
      "assets": {
        "cc-director-win-x64.exe": { "version": "0.4.0", "sha256": "AABB", "platform": "windows", "size": 100 },
        "cc-pdf-win-x64.exe": { "version": "1.2.0", "sha256": "CCDD", "platform": "windows", "size": 50 }
      }
    }
    """;

    private const string LegacyManifest = """
    {
      "version": "0.3.5",
      "assets": {
        "cc-director-win-x64.exe": { "sha256": "EEFF", "platform": "windows", "size": 100 }
      }
    }
    """;

    [Fact]
    public void Parse_ReadsPerAssetVersions()
    {
        var m = ReleaseManifest.Parse(PerAssetManifest);
        Assert.Equal("0.4.0", m.Version);

        var director = m.TryGetAsset("cc-director-win-x64.exe");
        Assert.NotNull(director);
        Assert.Equal("0.4.0", director!.Version);
        Assert.Equal("AABB", director.Sha256);

        var pdf = m.TryGetAsset("cc-pdf-win-x64.exe");
        Assert.NotNull(pdf);
        Assert.Equal("1.2.0", pdf!.Version);   // independent of the release version
        Assert.Equal(50, pdf.Size);
    }

    [Fact]
    public void Parse_LegacyAssetInheritsReleaseVersion()
    {
        var m = ReleaseManifest.Parse(LegacyManifest);
        var asset = m.TryGetAsset("cc-director-win-x64.exe");
        Assert.NotNull(asset);
        Assert.Equal("0.3.5", asset!.Version); // inherited from top-level version
    }

    [Fact]
    public void TryGetAsset_ReturnsNullForUnknown()
    {
        var m = ReleaseManifest.Parse(PerAssetManifest);
        Assert.Null(m.TryGetAsset("nonexistent.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{ \"version\": \"1.0\" }")]
    [InlineData("{ \"assets\": {} }")]
    public void Parse_ThrowsOnInvalidManifest(string json)
    {
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(json));
    }
}
