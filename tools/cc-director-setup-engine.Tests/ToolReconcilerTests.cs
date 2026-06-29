using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Offline tests for the manifest-driven <see cref="ToolReconciler"/> (issue #826). They drive a temp/fake
/// install layout - never the real machine install or the network. The heavy release-backed repair is
/// injected as a delegate so the venv-broken escalation can be exercised (and asserted NOT to fire for a
/// shim-only drift) without a real release.
///
/// All cases use real manifest tool names (e.g. cc-pdf) because the reconciler enumerates the embedded
/// tools manifest via <see cref="CcDirector.Core.Tools.ToolCatalogService"/>; the venv console-script exe is
/// faked on disk so detection sees a "built" tool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ToolReconcilerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public ToolReconcilerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Place a fake venv console-script exe for each given tool name (so it looks "built").</summary>
    private void PlaceVenvScripts(params string[] names)
    {
        Directory.CreateDirectory(_layout.PyenvScriptsDir);
        foreach (var n in names)
            File.WriteAllText(PythonToolsInstaller.ConsoleScriptPath(_layout, n), "fake-exe");
    }

    /// <summary>Record the bundle's expected console-script names so the venv-health probe has an expectation.</summary>
    private void RecordExpectedScripts(params string[] names)
        => PythonToolsState.SaveScripts(_layout, names);

    private string ShimPath(string name) => Path.Combine(_layout.BinDir, name + ".cmd");

    /// <summary>A heavy-repair delegate that records whether it was called and returns a fixed result.</summary>
    private sealed class FakeHeavyRepair
    {
        public int Calls { get; private set; }
        private readonly bool _success;
        public FakeHeavyRepair(bool success) => _success = success;

        public Task<PythonToolsResult> InvokeAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new PythonToolsResult(
                _success, _success ? "rebuilt" : "rebuild failed", Array.Empty<string>(), 0, _success ? "1.0.0" : null));
        }
    }

    // (a) no-drift -> InSync, zero mutations -------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_NoDrift_ReturnsInSync_NoMutation()
    {
        // Every recorded tool is built (console script present) AND has its shim - no orphans, no broken venv.
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
        Assert.True(File.Exists(ShimPath("cc-pdf")));

        // Snapshot bin so we can prove nothing changed.
        var before = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.InSync, result.Outcome);
        Assert.Empty(result.Actions);
        Assert.Equal(0, heavy.Calls); // no network/release fetch on the happy path
        var after = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();
        Assert.Equal(before, after); // zero filesystem mutation
    }

    // (b) missing shim -> shim created, Reconciled ------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_MissingShim_CreatesShim_ReturnsReconciled()
    {
        // cc-pdf's venv exe exists but its bin shim is absent -> the reconciler must create it.
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        Assert.False(File.Exists(ShimPath("cc-pdf")));
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.True(File.Exists(ShimPath("cc-pdf")), "the missing shim was not created");
        Assert.Contains(result.Actions, a => a.Contains("cc-pdf"));
        Assert.Equal(0, heavy.Calls); // a missing shim must NOT trigger the heavy rebuild
    }

    // (c) orphaned legacy shim present -> purged, Reconciled --------------------------------------------

    [Fact]
    public async Task ReconcileAsync_OrphanedLegacyShim_Purged_ReturnsReconciled()
    {
        // A retired alias shim left by an older install; no other drift.
        Directory.CreateDirectory(_layout.BinDir);
        var legacy = Path.Combine(_layout.BinDir, "cc-send.cmd");
        File.WriteAllText(legacy, "@echo off\r\n");
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.False(File.Exists(legacy), "the orphaned legacy alias shim was not purged");
        Assert.Contains(result.Actions, a => a.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, heavy.Calls); // a legacy shim purge must NOT trigger the heavy rebuild
    }

    // (d) idempotency: second call is InSync ------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_CalledTwice_SecondCallIsInSyncNoOp()
    {
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        var heavy = new FakeHeavyRepair(success: true);
        var reconciler = new ToolReconciler(_layout, heavy.InvokeAsync);

        var first = await reconciler.ReconcileAsync();
        Assert.Equal(ReconcileOutcome.Reconciled, first.Outcome); // first call fixed the missing shim

        var before = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();
        var second = await reconciler.ReconcileAsync();

        Assert.Equal(ReconcileOutcome.InSync, second.Outcome); // nothing left to fix
        Assert.Empty(second.Actions);
        var after = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();
        Assert.Equal(before, after); // the second call mutated nothing
    }

    // (e) the heavy path is NOT entered for a shim-only drift -------------------------------------------

    [Fact]
    public async Task ReconcileAsync_ShimOnlyDrift_DoesNotEnterHeavyPath()
    {
        // Missing shim AND an orphaned legacy shim, but the venv is healthy (every recorded script on disk).
        // Both are light fixes - the heavy release-backed rebuild must NOT be invoked.
        PlaceVenvScripts("cc-pdf", "cc-html");
        RecordExpectedScripts("cc-pdf", "cc-html"); // both present -> venv healthy
        Directory.CreateDirectory(_layout.BinDir);
        File.WriteAllText(Path.Combine(_layout.BinDir, "cc-spawn.cmd"), "@echo off\r\n"); // retired alias
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Equal(0, heavy.Calls); // shim-only drift never escalates to the heavy rebuild
    }

    // venv-broken -> heavy path IS entered (proves the escalation is wired) ------------------------------

    [Fact]
    public async Task ReconcileAsync_BrokenVenv_EscalatesToHeavyRepair_ReturnsReconciled()
    {
        // A recorded script is missing from the venv -> broken -> escalate to the heavy repair delegate.
        RecordExpectedScripts("cc-pdf", "cc-html");
        PlaceVenvScripts("cc-pdf"); // cc-html missing -> venv unhealthy
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Equal(1, heavy.Calls); // the broken venv escalated to the heavy rebuild
        Assert.Contains(result.Actions, a => a.Contains("venv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReconcileAsync_BrokenVenv_HeavyRepairFails_ReturnsFailed()
    {
        RecordExpectedScripts("cc-pdf", "cc-html");
        PlaceVenvScripts("cc-pdf"); // cc-html missing -> venv unhealthy
        var heavy = new FakeHeavyRepair(success: false);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(1, heavy.Calls);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // mutex skip: when another holder owns the heavy mutex, the heavy repair is not forced ---------------

    [Fact]
    public async Task ReconcileAsync_BrokenVenv_AnotherDirectorHoldsMutex_SkipsHeavyRepair()
    {
        RecordExpectedScripts("cc-pdf", "cc-html");
        PlaceVenvScripts("cc-pdf"); // cc-html missing -> venv unhealthy
        var heavy = new FakeHeavyRepair(success: true);

        // Simulate ANOTHER Director (another process/thread) holding the machine-wide heavy-repair mutex.
        // A Windows Mutex is owned per-thread, so the holder MUST run on its own thread - acquiring it on the
        // test thread would let the reconciler's same-thread WaitOne re-acquire it recursively.
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holderThread = new Thread(() =>
        {
            using var holder = new Mutex(initiallyOwned: false, ToolReconciler.HeavyRepairMutexName, out _);
            holder.WaitOne();
            acquired.Set();
            release.Wait();
            holder.ReleaseMutex();
        });
        holderThread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder thread did not acquire the mutex");
        try
        {
            var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

            Assert.Equal(0, heavy.Calls); // did not force the rebuild while another holder owns the lock
            Assert.Contains(result.Actions, a => a.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            release.Set();
            holderThread.Join();
        }
    }
}
