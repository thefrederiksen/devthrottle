namespace CcDirector.Core.Machine;

/// <summary>
/// Which resource programs are stalled waiting on. Flags, because a machine can be slow for more than
/// one reason at once - short of memory AND thrashing the disk with swap is the classic pair.
///
/// There is deliberately no "unknown" value here. "We could not tell" is not a cause that can combine
/// with the others (Disk and Unknown together means nothing), so it is carried separately, on
/// <see cref="SlownessDiagnosis.CouldDiagnose"/>, in the same way every reading carries
/// <c>CouldRead</c>.
/// </summary>
[Flags]
public enum SlownessCause
{
    /// <summary>No cause - the machine is not slow, or the diagnosis could not be made.</summary>
    None = 0,

    /// <summary>Programs are waiting for a processor.</summary>
    Cpu = 1,

    /// <summary>Programs are waiting on memory.</summary>
    Memory = 2,

    /// <summary>Programs are waiting on the disk.</summary>
    Disk = 4,
}

/// <summary>
/// Who or what is behind the slowness. Every field is optional because the diagnosis names only what it
/// can actually show: a disk cause names a physical disk, a processor cause names a process, and either
/// names the session the process belongs to when it belongs to one.
/// </summary>
/// <param name="ProcessName">The busiest process's name.</param>
/// <param name="Pid">The busiest process's id.</param>
/// <param name="SessionId">The session that process belongs to, when it belongs to one.</param>
/// <param name="SessionName">That session's name, for the banner.</param>
/// <param name="DiskName">The physical disk to blame, for a disk cause, for example <c>sdc</c>.</param>
/// <param name="DiskKind">What that disk is - the reason a 75%-busy disk matters.</param>
public sealed record SlownessCulprit(
    string? ProcessName,
    int? Pid,
    string? SessionId,
    string? SessionName,
    string? DiskName,
    DiskKind? DiskKind);

/// <summary>
/// The answer to "why is it slow?" - shown in the banner, at the top of the detail screen, in the
/// Diagnosis tab, and returned by the diagnose command to an agent.
///
/// This is only the RESULT. It is produced by a pure function of the stall readings, in a class named
/// <c>SlownessDiagnoser</c>, written separately.
/// </summary>
/// <param name="CouldDiagnose">
/// False when the readings needed to decide were unreadable. Then <see cref="IsSlow"/> is false and
/// <see cref="Cause"/> is <see cref="SlownessCause.None"/>, and they mean "not known", not "fine" - read
/// <see cref="Summary"/> for why. Never treat an undiagnosable machine as a healthy one.
/// </param>
/// <param name="IsSlow">True when programs are measurably stalled waiting on a resource.</param>
/// <param name="Cause">Which resources, combined. None when not slow or not diagnosable.</param>
/// <param name="Evidence">The figures behind the verdict, one plain-English line each.</param>
/// <param name="Culprit">Who or what to blame. Null when nobody can be named.</param>
/// <param name="NextStep">What the owner can do about it, in plain English.</param>
/// <param name="Summary">
/// One line, for example "Slow because of the disk: the hard drive sdc is 75% busy, 26 ms per write".
/// </param>
public sealed record SlownessDiagnosis(
    bool CouldDiagnose,
    bool IsSlow,
    SlownessCause Cause,
    IReadOnlyList<string> Evidence,
    SlownessCulprit? Culprit,
    string NextStep,
    string Summary)
{
    /// <summary>
    /// The diagnosis that could not be made, carrying why. Not slow and no cause - but "not known", which
    /// <see cref="CouldDiagnose"/> false says.
    /// </summary>
    public static SlownessDiagnosis CannotDiagnose(string reason) =>
        new(CouldDiagnose: false, IsSlow: false, SlownessCause.None, Array.Empty<string>(), Culprit: null,
            NextStep: "Check the Director log for why the machine could not be read.",
            Summary: $"Cannot tell whether the machine is slow: {ResourceReadingGuard.Reason(reason)}");

    /// <summary>The one line, for a log.</summary>
    public override string ToString() => Summary;
}

/// <summary>
/// What one session costs the machine: its root process and every process descended from it, summed.
/// Filled later by a pure function over the process list; this is only its shape.
///
/// THE TOTALS CAN BE A LOWER BOUND, AND THE SHAPE SAYS SO. A process whose figures the platform refused
/// is still in the process list, named, with null figures - so a tree can contain processes that cannot
/// be added up. Summing them as zero would report a session as cheaper than it is, which is the made-up
/// zero this mission exists to avoid. So each sum covers only the processes whose figure could be read,
/// and a count travels beside it saying how many were left out. A screen can then say "at least 2.1 GB"
/// rather than "2.1 GB". Zero means that total is complete.
///
/// THERE ARE TWO COUNTS, NOT ONE, BECAUSE THE TWO FIGURES GO UNKNOWN FOR DIFFERENT REASONS. A process
/// that started during the interval has no earlier reading to compare against, so its processor share is
/// null while its memory is perfectly readable. One shared count would mark the memory total as a floor
/// when it is exact, and a screen would hedge a figure it could have stated. One count per total, each
/// true about its own sum.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="RootPid">The process the session started - the agent itself.</param>
/// <param name="CpuPercent">
/// Processor used by the whole tree, as a share of the WHOLE machine, 0 to 100 - so a session holding
/// one core of ten reads 10, not 100. Summed over the processes whose figures could be read.
/// </param>
/// <param name="MemoryBytes">
/// Resident memory (or working set) of the whole tree, summed over the processes whose figures could be
/// read.
/// </param>
/// <param name="Pids">Every process counted, the root included.</param>
/// <param name="ProcessesWithUnknownProcessor">
/// How many processes in the tree were running but whose processor share could not be known - refused by
/// the platform, or started during the interval. Non-zero means <paramref name="CpuPercent"/> is a floor
/// rather than a measurement of the whole tree.
/// </param>
/// <param name="ProcessesWithUnknownMemory">
/// How many processes in the tree were running but whose memory the platform refused. Non-zero means
/// <paramref name="MemoryBytes"/> is a floor. It is counted separately from
/// <paramref name="ProcessesWithUnknownProcessor"/> because the two go unknown independently.
/// </param>
public sealed record SessionUsage(
    string SessionId,
    int RootPid,
    double CpuPercent,
    ulong MemoryBytes,
    IReadOnlyList<int> Pids,
    int ProcessesWithUnknownProcessor,
    int ProcessesWithUnknownMemory);
