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
/// a real machine is exactly what a gate test may not contain. It MUST carry
/// <see cref="LiveMachinePlatformAttribute"/> naming its platform, so the whole project can be run on
/// every machine: on the other platforms its tests skip with the reason, and a class that declares no
/// platform is reported as an error rather than run - and failed - everywhere.
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
    /// HOW LONG THE PROBE UNDER TEST REUSES ONE SAMPLE. Override this in a derived class whose probe
    /// caches for longer than <see cref="MachineMemoryProbe.CacheFor"/>.
    ///
    /// The default is the memory probe's interval because that is what the Linux probe is DEFINED as
    /// (<c>LinuxResourceProbe.CacheFor = MachineMemoryProbe.CacheFor</c>, so it moves with it), while the
    /// macOS probe declares its own one second independently and merely happens to agree today. A
    /// platform whose probe is slower to read - a Windows performance counter needing a longer interval
    /// is the obvious candidate - must say so here, or <see cref="ReadWithRates"/> would wait out the
    /// wrong cache.
    /// </summary>
    protected virtual TimeSpan ProbeCacheFor => MachineMemoryProbe.CacheFor;

    /// <summary>
    /// Reads twice, far enough apart that the rates have an interval, which is the state most of these
    /// checks are about. The first sample is a legitimate sample in its own right - see
    /// <see cref="TheFirstSample_StillNamesEveryProcess_AndIsUnreadableOnlyForTheRates"/> - it simply has
    /// no rates yet.
    ///
    /// IT PROVES IT GOT TWO READINGS AND DOES NOT TRUST THE SLEEP TO HAVE BEEN LONG ENOUGH. Every probe
    /// reuses one sample for <see cref="ProbeCacheFor"/> and hands back the identical record while it
    /// does, so a wait shorter than that cache makes the second <c>Read</c> return the FIRST sample.
    /// Nothing fails when that happens: every rate is the first sample's, every contract check still
    /// passes, and the suite reports green on a run that read the machine exactly once - in the one test
    /// written to catch a cross-platform disagreement no other test can see. A check whose pass
    /// condition is the ABSENCE of a stale sample certifies a run that never happened, so the condition
    /// is restated here as a PRESENCE - a second, distinct reading - and asserted.
    ///
    /// The wait is the longer of the probe's own cache and the memory probe's, because a probe may read
    /// memory through that one, and a sample is past both only once the longer has elapsed.
    /// </summary>
    private ResourceSample ReadWithRates(IResourceProbe probe)
    {
        var first = probe.Read();

        var cache = ProbeCacheFor > MachineMemoryProbe.CacheFor ? ProbeCacheFor : MachineMemoryProbe.CacheFor;
        var waited = cache + TimeSpan.FromMilliseconds(250);
        Thread.Sleep(waited);

        var second = probe.Read();

        Assert.True(second.TakenAtUtc > first.TakenAtUtc,
            $"{ProbeName} returned the SAME sample twice, so every rate the checks below read came from a "
            + "single reading of the machine and those checks would pass without measuring anything. The "
            + "second Read came out of the probe's cache because the wait was shorter than that cache: "
            + $"waited {waited}, first sample stamped {first.TakenAtUtc:O}, second {second.TakenAtUtc:O}. "
            + $"Override {nameof(ProbeCacheFor)} in this derived class with the cache interval of the "
            + "probe it tests.");

        return second;
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
            + "saturated machine the operating system may not give one spinning thread anything like a "
            + "whole core, and the figure falls honestly. Re-run it on a quiet machine before treating it "
            + "as a defect; the upper bound below is the one that catches a real convention bug.");

        Assert.True(measured <= expected * 2.5,
            $"{ProbeName} measured this process at {measured:F1}% of the machine while it held ONE core of "
            + $"{cores}. A share of the whole machine is about {expected:F1}%. This looks like a share of "
            + "one core, which cannot be summed into a session total or compared between machines.");
    }

    [LiveMachineFact("the very first reading this probe takes of the machine")]
    public void TheFirstSample_StillNamesEveryProcess_AndIsUnreadableOnlyForTheRates()
    {
        // THE ONE RULE TWO PLATFORM BRANCHES HAD ALREADY SPLIT ON: one returned an unreadable process set
        // on the first read, the other a readable list. Nothing else checks it, and the other tests here
        // cannot, because they deliberately read twice and throw the first sample away. So it is checked
        // on a probe that has never been read before.
        var probe = CreateProbe();

        var first = probe.Read();

        Assert.True(first.Processes.CouldRead,
            $"{ProbeName} returned an unreadable process list on its first read. A process's SHARE is a "
            + "rate and has no interval yet, but its name, its parent and its memory are readable now - "
            + $"so the list is returned with CpuPercent null. Reason given: {first.Processes.UnreadableReason}");

        Assert.NotEmpty(first.Processes.Processes);
        Assert.Contains(first.Processes.Processes, one => one.Pid == Environment.ProcessId);

        // Stated as a presence: every process is named, whatever is unknown about it.
        Assert.All(first.Processes.Processes, one => Assert.False(string.IsNullOrWhiteSpace(one.Name)));

        // The core count is always answerable, so it must survive even when the busy figure cannot.
        Assert.True(first.Cpu.LogicalCoreCount is > 0,
            $"{ProbeName} gave no logical core count on its first read. Every platform can answer it, "
            + "and without it no process figure can be expressed as a share of the machine.");

        // Whatever else the first sample says, it must not break the shared rules.
        var broken = ResourceSampleContract.Violations(first);
        Assert.True(broken.Count == 0,
            $"{ProbeName}'s FIRST sample breaks the shared contract:{Environment.NewLine}"
            + string.Join(Environment.NewLine, broken.Select(one => "  - " + one)));
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
