using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Proves the Gateway's first-run consent acknowledgement flag (issue #650) persists to config.json
/// under <c>gateway_consent.acknowledged</c> and reads back, so the Gateway consent screen is shown
/// once and then not again. The per-Director first-run consent flag was removed in issue #651; this
/// gateway acknowledgement is deliberately a SEPARATE config section (<c>gateway_consent</c>) from the
/// legacy per-Director <c>consent</c> section an older build may have left behind.
/// These tests redirect the storage root with the CC_DIRECTOR_ROOT override, so they are not run in
/// parallel with anything else that reads config.json (own collection, no parallelism).
/// </summary>
[Collection("StorageRootOverride")]
public sealed class GatewayConsentAckTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousRoot;

    public GatewayConsentAckTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cc-gw-consent-ack-" + Guid.NewGuid().ToString("N"));
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

    // A fresh Gateway has never shown the consent screen, so it defaults to false (shown).
    [Fact]
    public void HasAcknowledged_NoPersistedValue_DefaultsToFalse()
    {
        Assert.False(GatewayConsentAck.HasAcknowledged());
    }

    // Acknowledging persists under gateway_consent.acknowledged and reads back true (the next-launch case).
    [Fact]
    public void MarkAcknowledged_PersistsAndReadsBackTrue()
    {
        GatewayConsentAck.MarkAcknowledged();

        // A fresh read (the next launch would do exactly this) returns the persisted value.
        Assert.True(GatewayConsentAck.HasAcknowledged());

        // And the value is under the documented config.json key.
        var root = CcDirectorConfigService.ReadRaw();
        var section = root[GatewayConsentAck.Section] as JsonObject;
        Assert.NotNull(section);
        var value = section[GatewayConsentAck.AcknowledgedKey] as JsonValue;
        Assert.NotNull(value);
        Assert.True(value.GetValue<bool>());
    }

    // The gateway acknowledgement is a SEPARATE fact from the centralized consent value: acknowledging
    // does not drop the centralized consent the same screen writes (non-lossy merge), and the two keys
    // are independent.
    [Fact]
    public void MarkAcknowledged_PreservesCentralizedConsent()
    {
        TelemetryConsentConfig.Set(false);

        GatewayConsentAck.MarkAcknowledged();

        Assert.False(TelemetryConsentConfig.Get());
        Assert.True(GatewayConsentAck.HasAcknowledged());
    }

    // The gateway acknowledgement writes ONLY its own gateway_consent section: it never writes the
    // legacy per-Director consent section (the per-Director consent surface was removed in issue #651,
    // but a co-located dev machine sharing one config.json must still never have that section fabricated).
    [Fact]
    public void MarkAcknowledged_DoesNotWriteLegacyPerDirectorConsentSection()
    {
        GatewayConsentAck.MarkAcknowledged();

        // The gateway acknowledgement is recorded, and no legacy per-Director "consent" section exists.
        Assert.True(GatewayConsentAck.HasAcknowledged());
        var root = CcDirectorConfigService.ReadRaw();
        Assert.False(root["consent"] is JsonObject, "MarkAcknowledged must not write the legacy per-Director consent section");
    }
}
