using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the first-run consent flag persists to config.json and reads back, so the consent step is
/// shown once and then not again. These tests redirect the storage root with the CC_DIRECTOR_ROOT
/// environment override, so they are not run in parallel with anything else that reads config.json
/// (own collection, no parallelism).
/// </summary>
[Collection("StorageRootOverride")]
public sealed class FirstRunConsentTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousRoot;

    public FirstRunConsentTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cc-dt-consent-" + Guid.NewGuid().ToString("N"));
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

    // A fresh install has never acknowledged the consent step, so it defaults to false (shown).
    [Fact]
    public void HasAcknowledged_NoPersistedValue_DefaultsToFalse()
    {
        Assert.False(FirstRunConsent.HasAcknowledged());
    }

    // Acknowledging persists under consent.acknowledged and reads back true (the next-start case).
    [Fact]
    public void MarkAcknowledged_PersistsAndReadsBackTrue()
    {
        FirstRunConsent.MarkAcknowledged();

        // A fresh read (the next start would do exactly this) returns the persisted value.
        Assert.True(FirstRunConsent.HasAcknowledged());

        // And the value is under the documented config.json key.
        var root = CcDirectorConfigService.ReadRaw();
        var section = root[FirstRunConsent.Section] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(section);
        var value = section[FirstRunConsent.AcknowledgedKey] as System.Text.Json.Nodes.JsonValue;
        Assert.NotNull(value);
        Assert.True(value.GetValue<bool>());
    }

    // Acknowledging does not drop other config.json sections (non-lossy merge), including the
    // telemetry choice the same consent step writes.
    [Fact]
    public void MarkAcknowledged_PreservesOtherSections()
    {
        TelemetrySettings.SetEnabled(false);

        FirstRunConsent.MarkAcknowledged();

        Assert.False(TelemetrySettings.IsEnabled());
        Assert.True(FirstRunConsent.HasAcknowledged());
    }
}
