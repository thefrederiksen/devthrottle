using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #649: the GATEWAY-OWNED, fleet-wide richer-usage-telemetry consent persisted in config.json
/// under <c>telemetry_consent</c>. Proves it defaults ON when never set, persists OFF (and survives a
/// fresh read from disk, the across-restart guarantee), persists ON again, and rejects a non-boolean
/// value rather than silently treating it as a default. Redirects CC_DIRECTOR_ROOT to a temp dir so the
/// tests read/write an isolated config.json, never the user's real one.
/// </summary>
public sealed class TelemetryConsentConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TelemetryConsentConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-consent-cfg-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_WhenNeverSet_DefaultsOn()
    {
        Assert.True(TelemetryConsentConfig.Get());
        Assert.Equal(TelemetryConsentConfig.Default, TelemetryConsentConfig.Get());
    }

    [Fact]
    public void Set_Off_PersistsAndSurvivesFreshReadFromDisk()
    {
        TelemetryConsentConfig.Set(false);
        Assert.False(TelemetryConsentConfig.Get());

        // The across-restart guarantee: the value is durable on disk, so a fresh read (a restarted
        // Gateway re-reading config.json) sees OFF.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.False((bool)onDisk[TelemetryConsentConfig.Key]!);
    }

    [Fact]
    public void Set_PreservesOtherConfigSections()
    {
        // Seed an unrelated section, then toggle consent - the merge must not drop it.
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        });

        TelemetryConsentConfig.Set(false);

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.False((bool)onDisk[TelemetryConsentConfig.Key]!);
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
    }

    [Fact]
    public void Set_OnAgain_PersistsOn()
    {
        TelemetryConsentConfig.Set(false);
        TelemetryConsentConfig.Set(true);
        Assert.True(TelemetryConsentConfig.Get());
    }

    [Fact]
    public void Get_WhenValueIsNotBoolean_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject { [TelemetryConsentConfig.Key] = "yes" });
        Assert.Throws<InvalidOperationException>(() => TelemetryConsentConfig.Get());
    }
}
