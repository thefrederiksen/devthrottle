using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

public sealed class UsageTelemetryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sinkPath;

    public UsageTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sinkPath = Path.Combine(_tempDir, "usage-events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Issue #582 AC4: with the toggle ON, the richer usage reporting fires.
    [Fact]
    public void Record_TelemetryEnabled_WritesUsageEvent()
    {
        var telemetry = new UsageTelemetry(isEnabled: () => true, sinkPath: _sinkPath);

        var recorded = telemetry.Record("session-created");

        Assert.True(recorded);
        var events = telemetry.ReadAll();
        Assert.Single(events);
        Assert.Equal("session-created", events[0].Name);
    }

    // Issue #582 AC4: with the toggle OFF, the richer usage reporting does NOT fire.
    [Fact]
    public void Record_TelemetryDisabled_DoesNotWriteAndReturnsFalse()
    {
        var telemetry = new UsageTelemetry(isEnabled: () => false, sinkPath: _sinkPath);

        var recorded = telemetry.Record("session-created");

        Assert.False(recorded);
        Assert.False(File.Exists(_sinkPath));
        Assert.Empty(telemetry.ReadAll());
    }

    // The toggle is evaluated at the moment of each record, so flipping it mid-run takes effect.
    [Fact]
    public void Record_ToggleEvaluatedPerCall_OnlyEnabledCallsWrite()
    {
        var enabled = false;
        var telemetry = new UsageTelemetry(isEnabled: () => enabled, sinkPath: _sinkPath);

        Assert.False(telemetry.Record("while-off"));
        enabled = true;
        Assert.True(telemetry.Record("while-on"));

        var events = telemetry.ReadAll();
        Assert.Single(events);
        Assert.Equal("while-on", events[0].Name);
    }

    [Fact]
    public void Record_EmptyEventName_Throws()
    {
        var telemetry = new UsageTelemetry(isEnabled: () => true, sinkPath: _sinkPath);

        Assert.Throws<ArgumentException>(() => telemetry.Record(""));
    }
}
