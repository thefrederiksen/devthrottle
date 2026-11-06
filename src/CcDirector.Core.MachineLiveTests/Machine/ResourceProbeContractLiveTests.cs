using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE SHARED CONTRACT TEST ALL THREE PLATFORM PROBES RUN.
///
/// Each platform's Developer derives from this and implements <see cref="CreateProbe"/>. The tests then
/// run against that platform's real probe on a real machine of that kind, which is the only place these
/// rules can be checked: a fake probe keeps whatever rules the fake was written to keep.
///
/// It is the answer to a cross-platform failure that would otherwise be found in the Cockpit. Three
/// people writing three probes against one interface will not read its doc comments the same way, and
/// the figure most likely to diverge is a process's processor share - per core on one operating system
/// and per machine on another. Summed into a per-session total, or placed beside the toolbar's own
/// processor bar, a per-core figure is not slightly wrong; it is nonsense, and nothing downstream can
/// detect it. So it is proved here, under a known load, per platform.
///
/// EVERY TEST HERE READS THE REAL MACHINE, AND IS LOCKED TWICE. This project is not in any of the gate's
/// project lists in <c>scripts\test-local.ps1</c>, so the gate never runs it; and every test carries
/// <see cref="LiveMachineFactAttribute"/>, so even run directly it SKIPS, visibly and with a reason,
/// unless <c>CC_DIRECTOR_LIVE_MACHINE_TESTS=1</c> is set. They are run deliberately: by the Developer on
/// the platform whose probe is being proved, and by the cross-platform quality assurance run.
///
/// WHERE A DERIVED CLASS GOES: in THIS project, beside this file - never in a gate suite. A real wait on
/// a real machine is exactly what a gate test may not contain.
/// </summary>
public abstract class ResourceProbeContractLiveTests
{
    /// <summary>The probe under test - this platform's real one, reading this real machine.</summary>
    protected abstract IResourceProbe CreateProbe();

    /// <summary>
    /// How this platform's probe is named in a failure message, for example "the Linux probe".
    /// </summary>
    protected abstract string ProbeName { get; }

    /// <summary>
    /// Reads twice, far enough apart that the rates have an interval. The first read after start-up has
    /// no interval by design, so a contract check against it would be judging the wrong sample.
    /// </summary>
    private ResourceSample ReadWithRates(IResourceProbe probe)
    {
        probe.Read();
        Thread.Sleep(MachineMemoryProbe.CacheFor + TimeSpan.FromMilliseconds(250));
        return probe.Read();
    }

    [LiveMachineFact("the whole machine, twice")]
    public void ASampleFromThisMachine_KeepsEveryRuleTheInterfaceStates()
    {
        var sample = ReadWithRates(CreateProbe());

        var broken = ResourceSampleContract.Violations(sample);

        Assert.True(broken.Count == 0,
            $"{ProbeName} produced a sample that breaks the shared contract:{Environment.NewLine}"
            + string.Join(Environment.NewLine, broken.Select(one => "  - " + one)));
    }

    [LiveMachineFact("the processor and the process list, under a known one-core load", MinimumLogicalCores = 4)]
    public void PerProcessProcessor_IsAShareOfTheWholeMachine_NotOfOneCore()
    {
        var cores = Environment.ProcessorCount;
        var probe = CreateProbe();
        probe.Read();

        // ONE core's worth of load, in this very process, for long enough to cross a sampling interval.
        using var load = new OneCoreLoad();
        Thread.Sleep(TimeSpan.FromSeconds(3));
        var sample = probe.Read();
        load.Stop();

        Assert.True(sample.Processes.CouldRead,
            $"{ProbeName} could not read the process list under load: {sample.Processes.UnreadableReason}");

        var mine = sample.Processes.Processes.SingleOrDefault(one => one.Pid == Environment.ProcessId);
        Assert.True(mine is not null,
            $"{ProbeName} left this test's own process ({Environment.ProcessId}) out of the process list, "
            + "so the probe cannot see the process that is burning a core.");

        Assert.True(mine!.CpuPercent is not null,
            $"{ProbeName} listed this test's own process but gave it no processor figure, while it was "
            + "holding a core flat out.");

        var measured = mine.CpuPercent!.Value;
        var expected = 100.0 / cores;

        // The lower bound is what stops this passing vacuously: the probe must actually have SEEN the
        // load. The upper bound is what catches a per-core figure, which would read near 100 on any
        // machine with four or more cores rather than near 100 divided by the core count.
        Assert.True(measured >= expected * 0.4,
            $"{ProbeName} measured this process at {measured:F1}% of the machine while it held one core "
            + $"of {cores} flat out; one core is {expected:F1}% of this machine, so the probe did not see "
            + "the load and the figure proves nothing. READ THIS BOUND CAREFULLY BEFORE BLAMING THE PROBE: "
            + "it is the only part of this test that the MACHINE can fail rather than the code. On a "
            + "saturated machine - this one running a full fleet - the operating system may not give one "
            + "spinning thread anything like a whole core, and the figure falls honestly. Re-run it on a "
            + "quiet machine before treating it as a defect; the upper bound below is the one that "
            + "catches a real convention bug.");

        Assert.True(measured <= expected * 2.5,
            $"{ProbeName} measured this process at {measured:F1}% of the machine while it held ONE core of "
            + $"{cores}. A share of the whole machine is about {expected:F1}%. This looks like a share of "
            + "one core, which cannot be summed into a session total or compared between machines.");
    }

    [LiveMachineFact("the process list, looking for processes the platform refuses")]
    public void AProcessWhoseFiguresAreRefused_IsStillListedAndNamed()
    {
        var sample = ReadWithRates(CreateProbe());

        Assert.True(sample.Processes.CouldRead,
            $"{ProbeName} could not read the process list: {sample.Processes.UnreadableReason}");

        // Every process in the list is named, whether or not its figures were handed over. A process the
        // platform refuses belongs in the list with null figures; on a Mac that is roughly three hundred
        // of seven hundred, and the busiest process on the machine is routinely one of them.
        var nameless = sample.Processes.Processes.Where(one => string.IsNullOrWhiteSpace(one.Name)).ToList();

        // The message is built only when it is needed; building it eagerly would read a process out of an
        // empty list on every passing run.
        if (nameless.Count != 0)
            Assert.Fail($"{ProbeName} listed {nameless.Count} process(es) with no name, the first being "
                + $"process {nameless[0].Pid}. A diagnosis cannot name an unnamed culprit.");

        // Stated as a presence, not an absence: an empty or unreadable list must not read as a clean run.
        Assert.Contains(sample.Processes.Processes, one => one.Pid == Environment.ProcessId);
    }

    /// <summary>
    /// One core's worth of work, and no more. A background thread spinning on arithmetic: it needs no
    /// subprocess, it stops when told, and one thread cannot take more than one core however busy the
    /// machine is - which is what makes the measurement above mean what it says.
    /// </summary>
    private sealed class OneCoreLoad : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public OneCoreLoad()
        {
            _thread = new Thread(() =>
            {
                var spin = 1L;
                while (!_stop.IsCancellationRequested) spin = unchecked(spin * 6364136223846793005L + 1442695040888963407L);
                GC.KeepAlive(spin);
            })
            { IsBackground = true, Name = "resource probe contract load" };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }
    }
}
