using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the usage-telemetry flag persists to config.json and reads back (issue #582 AC3). These
/// tests redirect the storage root with the CC_DIRECTOR_ROOT environment override, so they are not
/// run in parallel with anything else that reads config.json (own collection, no parallelism).
/// </summary>
[Collection("StorageRootOverride")]
public sealed class TelemetrySettingsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousRoot;

    public TelemetrySettingsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cc-dt-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // AC3: the toggle defaults to ON when nothing has been persisted yet.
    [Fact]
    public void IsEnabled_NoPersistedValue_DefaultsToOn()
    {
        Assert.True(TelemetrySettings.IsEnabled());
    }

    // AC3: turning it off persists under telemetry.enabled and reads back as off (the restart case).
    [Fact]
    public void SetEnabled_False_PersistsAndReadsBackOff()
    {
        TelemetrySettings.SetEnabled(false);

        // A fresh read (the next start would do exactly this) returns the persisted value.
        Assert.False(TelemetrySettings.IsEnabled());

        // And the value is under the documented config.json key.
        var root = CcDirectorConfigService.ReadRaw();
        var section = root[TelemetrySettings.Section] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(section);
        var value = section[TelemetrySettings.EnabledKey] as System.Text.Json.Nodes.JsonValue;
        Assert.NotNull(value);
        Assert.False(value.GetValue<bool>());
    }

    // Turning it back on persists true.
    [Fact]
    public void SetEnabled_True_PersistsAndReadsBackOn()
    {
        TelemetrySettings.SetEnabled(false);
        TelemetrySettings.SetEnabled(true);

        Assert.True(TelemetrySettings.IsEnabled());
    }

    // Persisting the flag does not drop other config.json sections (non-lossy merge).
    [Fact]
    public void SetEnabled_PreservesOtherSections()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            ["gateway"] = new System.Text.Json.Nodes.JsonObject { ["url"] = "http://example:7878" },
        });

        TelemetrySettings.SetEnabled(false);

        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(gateway);
        Assert.Equal("http://example:7878", (gateway["url"] as System.Text.Json.Nodes.JsonValue)?.GetValue<string>());
    }
}
