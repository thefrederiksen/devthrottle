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
        foreach (var n in names)
            PlaceVenvScript(_layout, n);
    }

    /// <summary>
    /// Write one fake console script where the venv ACTUALLY keeps it on this platform.
    ///
    /// These helpers used to create <c>PyenvScriptsDir</c> - the Windows <c>Scripts\</c> folder - and then
    /// write to whatever <see cref="PythonToolsInstaller.ConsoleScriptPath"/> returns, which on macOS and
    /// Linux is <c>bin/</c>. So off Windows they created one directory and wrote into another that did not
    /// exist, and the write threw. In the foreign-thread helper that throw happened on a background thread
    /// with nothing to catch it, which aborted the whole test run rather than failing one test.
    /// </summary>
    private static void PlaceVenvScript(InstallLayout layout, string name)
    {
        var path = PythonToolsInstaller.ConsoleScriptPath(layout, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake-exe");
    }

    /// <summary>Record the bundle's expected console-script names so the venv-health probe has an expectation.</summary>
    private void RecordExpectedScripts(params string[] names)
        => PythonToolsState.SaveScripts(_layout, names);

    /// <summary>Record an installed Python-tools bundle version so the state does not read as truly-empty.</summary>
    private void RecordBundleInstalled(string version = "1.0.0")
    {
        var manifest = InstalledManifest.Load(_layout);
        manifest.Set(PythonToolsInstaller.ComponentId, version);
        manifest.Save(_layout);
    }

    private string ShimPath(string name) => Path.Combine(_layout.BinDir, name + ".cmd");

    /// <summary>
    /// Place ONLY the venv interpreter (no manifest, no sidecar, no console scripts) - the residue a FAILED
    /// first provision leaves: PythonToolsInstaller creates the interpreter before pip runs, and pip can then
    /// fail, so the post-success records are never written.
    /// </summary>
    private void PlaceVenvInterpreter()
    {
        Directory.CreateDirectory(_layout.PyenvBinDir);
        File.WriteAllText(Path.Combine(_layout.PyenvBinDir, "python.exe"), "fake-python");
    }

    /// <summary>
    /// A heavy-repair delegate that records its calls and, on success, writes exactly what a real successful
    /// provision writes (console script + shim + installed manifest + expected-scripts sidecar) so a later
    /// reconcile can tell a recovered install from a still-empty one.
    /// </summary>
    private sealed class RecordingHeavyRepair
    {
        private readonly InstallLayout _layout;
        private readonly bool _success;
        public int Calls { get; private set; }
        public RecordingHeavyRepair(InstallLayout layout, bool success) { _layout = layout; _success = success; }

        public Task<PythonToolsResult> InvokeAsync(CancellationToken ct)
        {
            Calls++;
            if (_success)
            {
                PlaceVenvScript(_layout, "cc-pdf");
                new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
                var manifest = InstalledManifest.Load(_layout);
                manifest.Set(PythonToolsInstaller.ComponentId, "1.0.0");
                manifest.Save(_layout);
                PythonToolsState.SaveScripts(_layout, new[] { "cc-pdf" });
            }
            return Task.FromResult(new PythonToolsResult(
                _success, _success ? "provisioned" : "provision failed", Array.Empty<string>(), _success ? 1 : 0, _success ? "1.0.0" : null));
        }
    }

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
        // A retired alias shim left by an older install; no other drift. A legacy shim only exists on a
        // machine that already installed the bundle, so record the bundle - otherwise the state reads as a
        // truly-empty fresh install and the from-nothing provision (case d) fires instead of a pure purge.
        RecordBundleInstalled();
        Directory.CreateDirectory(_layout.BinDir);
        var legacy = Path.Combine(_layout.BinDir, "cc-send.cmd");
        // The product's OWN shim body: the purge deletes a launcher only when the file proves this
        // product wrote it, so a stub body would be kept and this test would be asserting on a purge
        // that never happened.
        File.WriteAllText(legacy, PythonToolsInstaller.BuildWindowsShimBody("cc-send"));
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

    // (d) EMPTY tools state -> provision from nothing (the snappy-install first-run trigger) -------------

    // Revert-proof for "first-run provisions from nothing": with a truly-empty tools state (no recorded
    // bundle, no sidecar, no venv, no shims - exactly what the installer now leaves behind), ReconcileAsync
    // must drive the from-scratch provision via the heavy release-backed path. Revert-proof: make
    // ToolsBundleAbsent() return false (gate out the case-(d) escalation) -> heavy.Calls == 0 and this reds.
    [Fact]
    public async Task ReconcileAsync_EmptyToolsState_ProvisionsFromNothing()
    {
        // Nothing on disk at all: no PlaceVenvScripts, no RecordExpectedScripts, no RecordBundleInstalled.
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(1, heavy.Calls); // the empty state escalated to the from-nothing provision
        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Contains(result.Actions, a => a.Contains("bundle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReconcileAsync_EmptyToolsState_ProvisionFails_ReturnsFailed()
    {
        var heavy = new FakeHeavyRepair(success: false);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(1, heavy.Calls);
        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // A recorded bundle version means the machine already provisioned once, so the empty-state provision
    // must NOT fire - the ordinary drift/health checks govern instead (here: a lone healthy install, InSync).
    [Fact]
    public async Task ReconcileAsync_BundleRecorded_DoesNotReprovision()
    {
        RecordBundleInstalled();
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(0, heavy.Calls); // already provisioned -> no from-nothing rebuild
        Assert.Equal(ReconcileOutcome.InSync, result.Outcome);
    }

    // Two first-launch Directors: the second (mutex held by the first) does NOT force a second provision.
    [Fact]
    public async Task ReconcileAsync_EmptyToolsState_AnotherDirectorHoldsMutex_SkipsProvision()
    {
        var heavy = new FakeHeavyRepair(success: true);

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

            Assert.Equal(0, heavy.Calls); // did not force a provision while another first-launch Director holds the lock
            Assert.Contains(result.Actions, a => a.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            release.Set();
            holderThread.Join();
        }
    }

    // Retry-safety: a FAILED first provision leaves a bare venv interpreter but neither post-success record.
    // The reconcile must still provision (retry) - the partial interpreter must NOT be read as "installed".
    // Revert-proof: re-add `&& !PythonToolsInstaller.IsVenvPresent(_layout)` to ToolsBundleAbsent (the exact
    // bug) -> the partial interpreter suppresses provision, heavy.Calls == 0, and this reds.
    [Fact]
    public async Task ReconcileAsync_PartialVenvInterpreterNoRecords_StillProvisions()
    {
        PlaceVenvInterpreter(); // interpreter present, but no manifest and no sidecar (pip failed on first run)
        var heavy = new FakeHeavyRepair(success: true);

        var result = await new ToolReconciler(_layout, heavy.InvokeAsync).ReconcileAsync();

        Assert.Equal(1, heavy.Calls); // the partial interpreter did NOT suppress the retry
        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Contains(result.Actions, a => a.Contains("bundle", StringComparison.OrdinalIgnoreCase));
    }

    // Full lifecycle: a failed first provision leaves an interpreter; the next reconcile retries even though
    // the first still fails; a later succeeding provision recovers (writes the post-success records); once
    // recovered, further reconciles do NOT re-provision.
    [Fact]
    public async Task ReconcileAsync_FailedFirstProvision_RetriesUntilRecovery_ThenStops()
    {
        PlaceVenvInterpreter();

        var stillFailing = new FakeHeavyRepair(success: false);
        var firstRetry = await new ToolReconciler(_layout, stillFailing.InvokeAsync).ReconcileAsync();
        Assert.Equal(1, stillFailing.Calls);                 // retried despite the leftover interpreter
        Assert.Equal(ReconcileOutcome.Failed, firstRetry.Outcome);

        var recovering = new RecordingHeavyRepair(_layout, success: true);
        var recovered = await new ToolReconciler(_layout, recovering.InvokeAsync).ReconcileAsync();
        Assert.Equal(1, recovering.Calls);                   // retried AGAIN and this time succeeded
        Assert.Equal(ReconcileOutcome.Reconciled, recovered.Outcome);

        var afterRecovery = new RecordingHeavyRepair(_layout, success: true);
        var steady = await new ToolReconciler(_layout, afterRecovery.InvokeAsync).ReconcileAsync();
        Assert.Equal(0, afterRecovery.Calls);                // records now present -> no more re-provision
        Assert.Equal(ReconcileOutcome.InSync, steady.Outcome);
    }

    [Fact]
    public void HasDrift_EmptyToolsState_ReturnsTrue()
    {
        // Nothing installed yet -> the indicator must show "Syncing tools..." and drive the provision.
        Assert.True(new ToolReconciler(_layout).HasDrift());
    }

    // HasDrift: the read-only probe the active indicator uses (issue #829) --------------------------------

    [Fact]
    public void HasDrift_NoDrift_ReturnsFalseAndMutatesNothing()
    {
        // Every recorded tool is built AND shimmed - no orphans, no broken venv.
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
        var before = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();

        var drift = new ToolReconciler(_layout).HasDrift();

        Assert.False(drift);
        var after = Directory.GetFileSystemEntries(_layout.BinDir).OrderBy(p => p).ToArray();
        Assert.Equal(before, after); // a probe never mutates the install
    }

    [Fact]
    public void HasDrift_MissingShim_ReturnsTrue()
    {
        PlaceVenvScripts("cc-pdf");
        RecordExpectedScripts("cc-pdf");
        Assert.False(File.Exists(ShimPath("cc-pdf")));

        Assert.True(new ToolReconciler(_layout).HasDrift());
    }

    [Fact]
    public void HasDrift_OrphanedLegacyShim_ReturnsTrue()
    {
        Directory.CreateDirectory(_layout.BinDir);
        File.WriteAllText(Path.Combine(_layout.BinDir, "cc-send.cmd"),
            PythonToolsInstaller.BuildWindowsShimBody("cc-send"));

        Assert.True(new ToolReconciler(_layout).HasDrift());
    }

    [Fact]
    public void HasDrift_BrokenVenv_ReturnsTrue()
    {
        RecordExpectedScripts("cc-pdf", "cc-html");
        PlaceVenvScripts("cc-pdf"); // cc-html missing -> venv unhealthy

        Assert.True(new ToolReconciler(_layout).HasDrift());
    }

    // ---- The heavy repair must survive a real thread hop (issue #1045) ---------------------------------
    //
    // Every other heavy-repair double in this file returns Task.FromResult, so the await inside the
    // reconciler's mutex-guarded block completes SYNCHRONOUSLY and the mutex is released by the same
    // thread that took it. The real heavy repair downloads a release and runs pip - it always yields, and
    // its continuation resumes on an arbitrary thread-pool thread. A Windows named mutex has thread
    // affinity, so releasing it there threw "Object synchronization method was called from an
    // unsynchronized block of code", the reconciler's catch turned that into ReconcileOutcome.Failed, and
    // a clean install reported its own successful first-run provision as a failure. The doubles below
    // yield for real, which is the only thing the older ones do not do.

    /// <summary>
    /// A heavy-repair delegate whose task is completed by a DIFFERENT, explicitly-created thread. The
    /// TaskCompletionSource deliberately does NOT use RunContinuationsAsynchronously, so the awaiting
    /// continuation inside the reconciler runs inline on that foreign thread - which is what makes the
    /// thread hop deterministic rather than a race the test might win by luck. (A plain
    /// <c>await Task.Delay</c> is not enough: it schedules the continuation back through the test
    /// runner's synchronization context and can land on the original thread, so a double built that way
    /// passes against the broken code and detects nothing.)
    /// </summary>
    private sealed class ForeignThreadHeavyRepair
    {
        private readonly InstallLayout _layout;
        public int Calls { get; private set; }
        public int EnteredOnThreadId { get; private set; }
        public int CompletedOnThreadId { get; private set; }
        public ForeignThreadHeavyRepair(InstallLayout layout) => _layout = layout;

        public Task<PythonToolsResult> InvokeAsync(CancellationToken ct)
        {
            Calls++;
            EnteredOnThreadId = Environment.CurrentManagedThreadId;
            var tcs = new TaskCompletionSource<PythonToolsResult>();
            var worker = new Thread(() =>
            {
                CompletedOnThreadId = Environment.CurrentManagedThreadId;
                // Anything that throws here must reach the awaiting test through the task. An escaping
                // exception on a background thread takes the whole test process down with it, which reports
                // as "Test Run Aborted" and hides which test - and which assertion - actually broke.
                try
                {
                    PlaceVenvScript(_layout, "cc-pdf");
                    new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
                    var manifest = InstalledManifest.Load(_layout);
                    manifest.Set(PythonToolsInstaller.ComponentId, "1.0.0");
                    manifest.Save(_layout);
                    PythonToolsState.SaveScripts(_layout, new[] { "cc-pdf" });
                    tcs.SetResult(new PythonToolsResult(true, "provisioned", Array.Empty<string>(), 1, "1.0.0"));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            })
            { IsBackground = true, Name = "foreign-heavy-repair" };
            worker.Start();
            return tcs.Task;
        }
    }

    /// <summary>
    /// Run a reconcile the way the Director does - inside <c>Task.Run</c>, with no synchronization context -
    /// so awaited continuations resume wherever the runtime puts them rather than being marshalled back by
    /// the test runner.
    /// </summary>
    private static Task<ReconcileResult> ReconcileOffContextAsync(ToolReconciler reconciler)
        => Task.Run(() => reconciler.ReconcileAsync());

    [Fact]
    public async Task ReconcileAsync_FirstRunProvision_HeavyRepairResumesOnAnotherThread_ReportsReconciled()
    {
        // Nothing on disk at all - the clean-install state, which escalates to the from-nothing provision.
        var heavy = new ForeignThreadHeavyRepair(_layout);

        var result = await ReconcileOffContextAsync(new ToolReconciler(_layout, heavy.InvokeAsync));

        Assert.Equal(1, heavy.Calls);
        Assert.NotEqual(heavy.EnteredOnThreadId, heavy.CompletedOnThreadId); // the hop really happened
        Assert.Null(result.Error);
        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Contains(result.Actions, a => a.Contains("first run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReconcileAsync_BrokenVenv_HeavyRepairResumesOnAnotherThread_ReportsReconciled()
    {
        // A recorded bundle whose venv lost a console script - the repair escalation, also mutex-guarded.
        RecordBundleInstalled();
        RecordExpectedScripts("cc-pdf");
        var heavy = new ForeignThreadHeavyRepair(_layout);

        var result = await ReconcileOffContextAsync(new ToolReconciler(_layout, heavy.InvokeAsync));

        Assert.Equal(1, heavy.Calls);
        Assert.NotEqual(heavy.EnteredOnThreadId, heavy.CompletedOnThreadId);
        Assert.Null(result.Error);
        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
    }

    [Fact]
    public async Task ReconcileAsync_HeavyRepairResumesOnAnotherThread_MutexIsReleasedForTheNextCaller()
    {
        // The release must actually happen, not merely not-throw: a mutex left held (or abandoned by a
        // failed release) would block or corrupt the next Director's reconcile.
        var heavy = new ForeignThreadHeavyRepair(_layout);
        await ReconcileOffContextAsync(new ToolReconciler(_layout, heavy.InvokeAsync));

        using var mutex = new Mutex(initiallyOwned: false, ToolReconciler.HeavyRepairMutexName, out _);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(1));
            Assert.True(acquired, "the heavy-repair mutex was still held after the reconcile returned");
        }
        catch (AbandonedMutexException)
        {
            Assert.Fail("the heavy-repair mutex was abandoned - it was never released by its owning thread");
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
