using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests the enabled-vs-disabled gating decision at the single seam every Director lifecycle trigger
/// point goes through (issue #827): <see cref="ToolAutoUpdateTrigger.RunIfEnabledAsync(InstallLayout,
/// string, Func{CancellationToken, Task{ReconcileResult}}, CancellationToken)"/>. The reconcile is
/// injected as a counting delegate so the test asserts the lifecycle DOES call reconcile when
/// tools.autoUpdate.enabled is true and DOES NOT when it is false - without a real install or network.
/// </summary>
public class ToolAutoUpdateTriggerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public ToolAutoUpdateTriggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-tooltrigger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_layout.ConfigPath) ?? _layout.LocalRoot);
        File.WriteAllText(_layout.ConfigPath, json);
    }

    /// <summary>A reconcile delegate that records how many times it was invoked.</summary>
    private sealed class CountingReconcile
    {
        public int Calls { get; private set; }
        private readonly ReconcileResult _result;
        public CountingReconcile(ReconcileResult result) => _result = result;

        public Task<ReconcileResult> InvokeAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task RunIfEnabledAsync_FlagTrue_CallsReconcile()
    {
        WriteConfig("""{ "tools": { "autoUpdate": { "enabled": true } } }""");
        var reconcile = new CountingReconcile(new ReconcileResult(ReconcileOutcome.InSync, Array.Empty<string>()));

        var result = await ToolAutoUpdateTrigger.RunIfEnabledAsync(_layout, "startup", reconcile.InvokeAsync);

        Assert.Equal(1, reconcile.Calls);
        Assert.NotNull(result);
        Assert.Equal(ReconcileOutcome.InSync, result.Outcome);
    }

    [Fact]
    public async Task RunIfEnabledAsync_FlagAbsent_DefaultsOn_CallsReconcile()
    {
        // No config at all -> tools.autoUpdate.enabled defaults to true -> reconcile runs.
        var reconcile = new CountingReconcile(new ReconcileResult(ReconcileOutcome.Reconciled, new[] { "fixed" }));

        var result = await ToolAutoUpdateTrigger.RunIfEnabledAsync(_layout, "periodic", reconcile.InvokeAsync);

        Assert.Equal(1, reconcile.Calls);
        Assert.NotNull(result);
        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
    }

    [Fact]
    public async Task RunIfEnabledAsync_FlagFalse_DoesNotCallReconcile_ReturnsNull()
    {
        WriteConfig("""{ "tools": { "autoUpdate": { "enabled": false } } }""");
        var reconcile = new CountingReconcile(new ReconcileResult(ReconcileOutcome.InSync, Array.Empty<string>()));

        var result = await ToolAutoUpdateTrigger.RunIfEnabledAsync(_layout, "post-self-update", reconcile.InvokeAsync);

        Assert.Equal(0, reconcile.Calls); // gated off: the only path to fix tools is the manual button
        Assert.Null(result);
    }

    [Fact]
    public async Task RunIfEnabledAsync_ReconcileThrows_IsSwallowed_ReturnsNull()
    {
        // A reconcile failure must never gate or delay the lifecycle - the helper swallows + logs.
        WriteConfig("""{ "tools": { "autoUpdate": { "enabled": true } } }""");

        var result = await ToolAutoUpdateTrigger.RunIfEnabledAsync(
            _layout, "startup", _ => throw new InvalidOperationException("boom"));

        Assert.Null(result);
    }
}
