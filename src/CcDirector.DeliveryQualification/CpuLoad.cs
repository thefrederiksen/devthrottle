namespace CcDirector.DeliveryQualification;

/// <summary>
/// Keeps the machine's processors busy for the length of a run, at NORMAL priority, so the agents compete for the CPU
/// the way they do when a build, a test suite and a dozen other sessions are running. The owner's rule is that text
/// delivery works on a machine that is fully loaded; a run on an idle machine does not show that.
/// </summary>
public sealed class CpuLoad : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = new();

    public CpuLoad(int threads)
    {
        for (var i = 0; i < threads; i++)
        {
            var t = new Thread(() =>
            {
                var x = 0UL;
                while (!_stop.IsCancellationRequested)
                    for (var j = 0; j < 1_000_000; j++) x = x * 6364136223846793005UL + 1442695040888963407UL;
                GC.KeepAlive(x);
            }) { IsBackground = true, Priority = ThreadPriority.Normal, Name = $"cpu-load-{i}" };
            t.Start();
            _threads.Add(t);
        }
        Console.WriteLine($"[rig] CPU load: {threads} busy threads at normal priority on {Environment.ProcessorCount} processors");
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var t in _threads) t.Join();
        Console.WriteLine("[rig] CPU load stopped");
    }
}
