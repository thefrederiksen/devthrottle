using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Proves the Gateway's first-run consent acknowledgement flag (issue #650) persists to config.json
/// under <c>gateway_consent.acknowledged</c> and reads back, so the Gateway consent screen is shown
/// once and then not again. This is the gateway-side relocation of the per-Director
/// <see cref="Account.FirstRunConsent"/> flag and is deliberately a SEPARATE config section from it.
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

    // The gateway acknowledgement is distinct from the per-Director consent flag: recording the gateway
    // one does not touch the per-Director consent section (on a co-located dev machine they share one
    // config.json).
    [Fact]
    public void MarkAcknowledged_DoesNotTouchPerDirectorConsentSection()
    {
        GatewayConsentAck.MarkAcknowledged();

        // The per-Director consent section is untouched: still its default (not acknowledged).
        Assert.False(CcDirector.Core.Account.FirstRunConsent.HasAcknowledged());
        Assert.True(GatewayConsentAck.HasAcknowledged());
    }
}
