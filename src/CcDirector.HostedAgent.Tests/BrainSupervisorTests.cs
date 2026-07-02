using CcDirector.AgentBrain;
using Xunit;

namespace CcDirector.HostedAgent.Tests;

/// <summary>
/// Lifecycle tests for the Gateway's brain owner (issue #184): create on demand,
/// RestartAsync as the recovery verb, and a leak-free dispose. Built on the same fake
/// driver/backend pair as the HostedAgent tests - no claude.exe anywhere.
/// </summary>
public class BrainSupervisorTests : IDisposable
{
    private readonly string _workDir;

    public BrainSupervisorTests()
    {
        // Deliberately NOT created here: the supervisor owns its scratch dir and must
        // create it on first use (the Gateway points it at a dir that may not exist yet).
        _workDir = Path.Combine(Path.GetTempPath(), "brain-supervisor-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private HostedAgentOptions FastOptions() => new()
    {
        WorkingDirectory = _workDir,
        QuietSeconds = 0.0,
        QuietTimeoutSeconds = 2.0,
        StartTimeoutSeconds = 2.0,
        AskTimeoutSeconds = 2.0,
        ClearTimeoutSeconds = 1.0,
        PollIntervalSeconds = 0.01,
        ReplyStableSeconds = 0.05,
        Log = _ => { },
    };

    private static (BrainSupervisor Supervisor, FakeDriver Driver, List<FakeBackend> Backends, List<int> FactoryCalls)
        Build(HostedAgentOptions options)
    {
        var driver = new FakeDriver();
        var backends = new List<FakeBackend>();
        var factoryCalls = new List<int>();
        var supervisor = new BrainSupervisor(options, o =>
        {
            factoryCalls.Add(factoryCalls.Count + 1);
            return new HostedAgent(o, driver, () =>
            {
                var b = new FakeBackend();
                backends.Add(b);
                return b;
            });
        });
        return (supervisor, driver, backends, factoryCalls);
    }

    // -------------------------------------------------------------- creation

    [Fact]
    public void Constructor_MissingWorkingDirectory_Throws()
    {
        var options = FastOptions();
        options.WorkingDirectory = "";
        Assert.Throws<ArgumentException>(() => new BrainSupervisor(options));
    }

    [Fact]
    public void NotStarted_ExposesDormantState()
    {
        var (supervisor, _, _, factoryCalls) = Build(FastOptions());

        Assert.False(supervisor.IsStarted);
        Assert.Null(supervisor.SessionId);
        Assert.Equal(0, supervisor.ProcessId);
        Assert.Empty(factoryCalls); // dormant means NO agent was built
    }

    [Fact]
    public async Task GetAsync_FirstUse_CreatesStartsAndCreatesWorkdir()
    {
        var (supervisor, driver, _, factoryCalls) = Build(FastOptions());

        var brain = await supervisor.GetAsync();

        Assert.True(supervisor.IsStarted);
        Assert.Single(factoryCalls);
        Assert.True(Directory.Exists(_workDir)); // supervisor-owned scratch dir
        Assert.Equal(driver.IssuedSessionIds[0], brain.SessionId);
        Assert.Equal(supervisor.SessionId, brain.SessionId);
        Assert.Equal(4242, supervisor.ProcessId);
    }

    [Fact]
    public async Task GetAsync_SecondCall_ReturnsSameWarmInstance()
    {
        var (supervisor, _, _, factoryCalls) = Build(FastOptions());

        var first = await supervisor.GetAsync();
        var second = await supervisor.GetAsync();

        Assert.Same(first, second);
        Assert.Single(factoryCalls);
    }

    [Fact]
    public async Task GetAsync_ConcurrentFirstUse_SpawnsExactlyOnce()
    {
        var (supervisor, _, backends, factoryCalls) = Build(FastOptions());

        var tasks = Enumerable.Range(0, 8).Select(_ => supervisor.GetAsync()).ToArray();
        var brains = await Task.WhenAll(tasks);

        Assert.Single(factoryCalls);
        Assert.Single(backends);
        Assert.All(brains, b => Assert.Same(brains[0], b));
    }

    // -------------------------------------------------------------- restart

    [Fact]
    public async Task RestartAsync_NotStarted_PerformsFirstStart()
    {
        var (supervisor, _, _, factoryCalls) = Build(FastOptions());

        await supervisor.RestartAsync();

        Assert.True(supervisor.IsStarted);
        Assert.Single(factoryCalls);
        Assert.NotNull(supervisor.SessionId);
    }

    [Fact]
    public async Task RestartAsync_Started_ReplacesTheSessionOnTheSameHandle()
    {
        var (supervisor, driver, backends, _) = Build(FastOptions());
        var brain = await supervisor.GetAsync();
        var oldSession = supervisor.SessionId;

        await supervisor.RestartAsync();

        Assert.Same(brain, await supervisor.GetAsync());       // same handle for callers
        Assert.Equal(2, driver.IssuedSessionIds.Count);        // fresh agent session
        Assert.NotEqual(oldSession, supervisor.SessionId);
        Assert.Equal(2, backends.Count);                       // old backend replaced
        Assert.True(backends[0].HasExited);                    // gracefully shut down
    }

    [Fact]
    public async Task RestartAsync_DeadBrain_Recovers()
    {
        var (supervisor, _, backends, _) = Build(FastOptions());
        await supervisor.GetAsync();
        backends[0].SimulateExit(1); // the crash RestartAsync exists to recover from

        await supervisor.RestartAsync();

        var health = await supervisor.GetHealthAsync();
        Assert.True(health.IsAlive);
        Assert.Equal(2, backends.Count);
    }

    [Fact]
    public async Task GetAsync_DuringRestart_WaitsForTheFreshBrain()
    {
        // The live #185 restart storm: GetAsync must NEVER hand out the agent while a
        // restart holds the gate mid-replacement - the caller's ask would hit a
        // half-torn-down backend, fail, and fire yet another recovery.
        var driver = new FakeDriver();
        var restartEntered = new ManualResetEventSlim(false);
        var releaseStart = new ManualResetEventSlim(false);
        var backends = 0;
        var supervisor = new BrainSupervisor(FastOptions(), o => new HostedAgent(o, driver, () =>
        {
            // The SECOND backend (the restart's spawn) blocks until the test releases it.
            if (Interlocked.Increment(ref backends) == 2)
            {
                restartEntered.Set();
                releaseStart.Wait(TimeSpan.FromSeconds(5));
            }
            return new FakeBackend();
        }));
        await supervisor.GetAsync();

        // Off the test thread: the supervisor runs synchronously up to the blocking factory.
        var restart = Task.Run(() => supervisor.RestartAsync());
        Assert.True(restartEntered.Wait(TimeSpan.FromSeconds(5))); // restart is mid-replacement
        var getDuringRestart = supervisor.GetAsync();
        await Task.Delay(100);

        Assert.False(getDuringRestart.IsCompleted); // blocked behind the gate, not handed a dying brain
        releaseStart.Set();
        await restart;
        var brain = await getDuringRestart;

        var health = await brain.GetHealthAsync();
        Assert.True(health.IsAlive); // what came out the other side is the FRESH brain
        supervisor.Dispose();
    }

    // -------------------------------------------------------------- health

    [Fact]
    public async Task GetHealthAsync_NotStarted_ReportsNotStartedWithoutSpawning()
    {
        var (supervisor, _, _, factoryCalls) = Build(FastOptions());

        var health = await supervisor.GetHealthAsync();

        Assert.False(health.IsAlive);
        Assert.Equal("NotStarted", health.Status);
        Assert.Empty(factoryCalls); // health must NEVER be the thing that spawns claude
    }

    [Fact]
    public async Task GetHealthAsync_Started_DelegatesToTheAgent()
    {
        var (supervisor, _, _, _) = Build(FastOptions());
        await supervisor.GetAsync();

        var health = await supervisor.GetHealthAsync();

        Assert.True(health.IsAlive);
        Assert.Equal("Running", health.Status);
    }

    [Fact]
    public async Task GetHealthAsync_DeadBrain_ReportsDead()
    {
        var (supervisor, _, backends, _) = Build(FastOptions());
        await supervisor.GetAsync();
        backends[0].SimulateExit(1);

        var health = await supervisor.GetHealthAsync();

        Assert.False(health.IsAlive);
    }

    // -------------------------------------------------------------- dispose

    [Fact]
    public async Task Dispose_StopsTheHostedProcess()
    {
        var (supervisor, _, backends, _) = Build(FastOptions());
        await supervisor.GetAsync();

        supervisor.Dispose();

        Assert.True(backends[0].HasExited); // no leaked claude.exe on tray quit
    }

    [Fact]
    public async Task Dispose_ThenUse_Throws()
    {
        var (supervisor, _, _, _) = Build(FastOptions());
        await supervisor.GetAsync();
        supervisor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.GetAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.RestartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.GetHealthAsync());
    }

    [Fact]
    public void Dispose_NeverStarted_IsClean()
    {
        var (supervisor, _, _, _) = Build(FastOptions());
        supervisor.Dispose();
        supervisor.Dispose(); // idempotent
    }

    // ------------------------------------------- dispose is bounded (issue #880)

    // Issue #880: a brain whose graceful stop never finishes must not hold the host's shutdown -
    // Dispose returns within the grace period and takes the force-kill path.
    [Fact]
    public async Task Dispose_HungGracefulStop_ReturnsWithinTheGracePeriod()
    {
        var log = new List<string>();
        var options = FastOptions();
        options.Log = line => { lock (log) log.Add(line); };
        var neverFinishes = new TaskCompletionSource();
        FakeBackend? backend = null;
        var supervisor = new BrainSupervisor(
            options,
            o => new HostedAgent(o, new FakeDriver(), () =>
            {
                backend = new FakeBackend { OnGracefulShutdown = () => neverFinishes.Task };
                return backend;
            }),
            disposeGracePeriod: TimeSpan.FromMilliseconds(200));
        await supervisor.GetAsync();
        // A unit test must never force-kill a real machine process; pid 0 skips the kill call
        // itself while still exercising the timeout path that decides to kill.
        backend!.ProcessId = 0;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        supervisor.Dispose();
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"Dispose took {elapsed.Elapsed.TotalSeconds:0.0}s - the hung stop held it past the grace period");
        lock (log)
            Assert.Contains(log, line => line.Contains("did not finish within"));
        neverFinishes.SetResult(); // release the abandoned kill task
    }

    // Issue #880 regression, the production freeze: the tray disposed the brain ON the Avalonia
    // UI thread, so the kill's await continuations were posted to that thread's synchronization
    // context - which could never run them, because the thread was blocked inside Dispose itself.
    // A context that swallows posts reproduces exactly that; pre-#880 this deadlocked forever.
    [Fact]
    public async Task Dispose_OnAThreadWhoseSynchronizationContextNeverPumps_DoesNotDeadlock()
    {
        var (supervisor, _, backends, _) = Build(FastOptions());
        await supervisor.GetAsync();
        // The await forces the graceful stop to resume on SOME context - pre-#880 that was the
        // caller's captured (dead) context; post-#880 it is the thread pool.
        backends[0].OnGracefulShutdown = async () => await Task.Delay(50);

        var dispose = Task.Run(() =>
        {
            var original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SwallowsPostsContext());
            try { supervisor.Dispose(); }
            finally { SynchronizationContext.SetSynchronizationContext(original); }
        });

        // Throws TimeoutException if Dispose deadlocked against the never-pumping context.
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(backends[0].HasExited); // the graceful stop genuinely completed
    }

    /// <summary>A synchronization context that queues nothing and runs nothing - the shape of a
    /// blocked UI thread, whose dispatcher never gets to pump posted continuations.</summary>
    private sealed class SwallowsPostsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // A blocked UI thread never pumps: the continuation is lost, exactly like production.
        }
    }
}
