using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Tests the Gateway first-run consent screen's decision-and-persistence library (issue #650, Gateway
/// Centralization Phase 3): the screen is shown ONCE at the Gateway's first launch, acknowledging it
/// records the acknowledgement on the Gateway AND writes the usage-sharing choice to the Gateway's
/// CENTRALIZED consent setting (<see cref="TelemetryConsentConfig"/>, issue #649), and a subsequent
/// launch does not re-show it.
///
/// All three acceptance criteria are exercised against <see cref="GatewayConsentSurface"/> (the pure
/// library the Avalonia window is a thin surface over), so they are provable without an Avalonia UI
/// thread. Redirects CC_DIRECTOR_ROOT to a temp dir so the tests read/write an isolated config.json,
/// never the real one; the [Collection] forces the env-var-mutating tests to run sequentially.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayConsentSurfaceTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public GatewayConsentSurfaceTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-gw-consent-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Acceptance criterion 1 (first launch shows): a fresh Gateway has not acknowledged the screen, so
    // it is shown on launch. The usage-sharing pre-selection reads the centralized default (ON).
    [Fact]
    public void FreshGateway_ShouldShowConsentOnLaunch_AndPreSelectsOn()
    {
        Assert.True(GatewayConsentSurface.ShouldShowConsentOnLaunch());
        Assert.True(GatewayConsentSurface.CurrentUsageSharingChoice()); // default ON (issue #649)
    }

    // Acceptance criterion 1 (records ack + sets consent): acknowledging with "share" records the
    // gateway acknowledgement AND writes the centralized consent value.
    [Fact]
    public void Acknowledge_ShareUsageTrue_RecordsAckAndSetsCentralizedConsentOn()
    {
        GatewayConsentSurface.Acknowledge(shareUsage: true);

        Assert.True(GatewayConsentAck.HasAcknowledged());
        Assert.True(TelemetryConsentConfig.Get());
    }

    // Acceptance criterion 3 (choice maps to the #649 setting): acknowledging with the box UNCHECKED
    // writes the centralized consent as OFF - the choice is reflected in the gateway's centralized
    // setting, not a per-Director one.
    [Fact]
    public void Acknowledge_ShareUsageFalse_SetsCentralizedConsentOff()
    {
        GatewayConsentSurface.Acknowledge(shareUsage: false);

        Assert.True(GatewayConsentAck.HasAcknowledged());
        Assert.False(TelemetryConsentConfig.Get());

        // The across-restart guarantee: the centralized value is durable on disk under the #649 key, so
        // a restarted Gateway re-reading config.json sees the user's choice.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.False((bool)onDisk[TelemetryConsentConfig.Key]!);
    }

    // Acceptance criterion 2 (subsequent launch does NOT re-show): once acknowledged, the screen is not
    // shown again, and the persisted acknowledgement survives a fresh read from disk.
    [Fact]
    public void AfterAcknowledge_SubsequentLaunch_DoesNotReShow()
    {
        Assert.True(GatewayConsentSurface.ShouldShowConsentOnLaunch()); // first launch: shown

        GatewayConsentSurface.Acknowledge(shareUsage: true);

        // Second launch: not shown again.
        Assert.False(GatewayConsentSurface.ShouldShowConsentOnLaunch());

        // The acknowledgement is durable on disk (a restarted Gateway re-reading config.json sees it).
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.True((bool)onDisk[GatewayConsentAck.Section]![GatewayConsentAck.AcknowledgedKey]!);
    }

    // The consent acknowledgement and the centralized usage choice are SEPARATE facts: changing the
    // usage choice later (e.g. via the Cockpit toggle) does not un-acknowledge the screen, so it is
    // never re-shown just because the usage choice changed.
    [Fact]
    public void TogglingCentralizedConsentLater_DoesNotReShowTheScreen()
    {
        GatewayConsentSurface.Acknowledge(shareUsage: true);
        Assert.False(GatewayConsentSurface.ShouldShowConsentOnLaunch());

        // The user later turns the centralized consent OFF from the Cockpit (#649), independent of the
        // consent screen.
        TelemetryConsentConfig.Set(false);

        Assert.False(GatewayConsentSurface.ShouldShowConsentOnLaunch()); // still acknowledged, still not re-shown
        Assert.False(TelemetryConsentConfig.Get());
    }

    // The pre-selection reflects the real current centralized value, not a hardcoded default: after the
    // fleet has opted out, re-reading the choice (e.g. an upgrade re-showing the screen) shows OFF.
    [Fact]
    public void CurrentUsageSharingChoice_ReflectsPersistedCentralizedValue()
    {
        TelemetryConsentConfig.Set(false);
        Assert.False(GatewayConsentSurface.CurrentUsageSharingChoice());

        TelemetryConsentConfig.Set(true);
        Assert.True(GatewayConsentSurface.CurrentUsageSharingChoice());
    }
}
