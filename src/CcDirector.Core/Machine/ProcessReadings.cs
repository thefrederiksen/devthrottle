using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One process and what it costs the machine. The raw material for per-session usage
/// (<see cref="SessionUsage"/>) and for naming the culprit in a slowness diagnosis.
///
/// A single process is not given its own <c>CouldRead</c>. A process that has gone is left out of the
/// list, and a list that cannot be read at all is <see cref="ProcessSetReading.Unreadable"/>. A process
/// that is STILL THERE but whose figures the platform refuses to hand over belongs IN the list, named,
/// with those figures null - never left out and never written down as zero. On a Mac an ordinary user is
/// refused the figures for roughly three hundred of the machine's seven hundred processes, and the
/// busiest process on the machine is often one of them, so dropping them would hide the very thing a
/// slowness diagnosis exists to name.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent's process id, which is how a session's child processes are found. Null when not known.</param>
/// <param name="Name">The process's name as the operating system reports it.</param>
/// <param name="StartTimeUtc">
/// When the process started. Together with <paramref name="Pid"/> it identifies a process, because process
/// ids are reused: without it a new process could inherit a dead one's usage. Null when not offered.
/// </param>
/// <param name="CpuPercent">
/// Processor used over the last interval, as a share of the WHOLE machine, 0 to 100 - so a process
/// holding one core of ten reads 10, not 100. Null when it is not known: the platform refused this
/// process, or it started during the interval and has no earlier reading to compare against. Never zero
/// to mean unknown.
/// </param>
/// <param name="MemoryBytes">
/// Resident memory (Linux, macOS) or working set (Windows). Null when the platform refused this process.
/// </param>
/// <param name="DiskReadBytesPerSecond">
/// Bytes this process read per second - FOR THE SLOWNESS DIAGNOSIS ONLY, like
/// <see cref="DiskEvidence"/>, and never a meter. Null when not offered on this platform.
///
/// KEPT DELIBERATELY, and this is the reasoning because mission section 5c-i requires such a decision to
/// be written down. That section drops "disk byte rates" along with volumes, free space and the network,
/// and that list is about the PER-DISK reading: its throughput figures are gone, and so is the disk
/// meter they fed. These are PER-PROCESS, and they are the only way to finish the approved sentence in
/// section 5i - "The hard drive sdc is 75% busy at 26 milliseconds per write ... Mostly dotnet test in
/// session 140". A diagnosis that can name the disk but not the process thrashing it names half the
/// cause, and the owner's whole complaint was having to find that out by hand.
/// </param>
/// <param name="DiskWriteBytesPerSecond">
/// Bytes this process wrote per second. Diagnosis only, on the same terms as
/// <paramref name="DiskReadBytesPerSecond"/>. Null when not offered on this platform.
/// </param>
public sealed record ProcessReading(
    int Pid,
    int? ParentPid,
    string Name,
    DateTime? StartTimeUtc,
    double? CpuPercent,
    ulong? MemoryBytes,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond)
{
    /// <summary>Short, log-safe description.</summary>
    public override string ToString()
    {
        var processor = CpuPercent is { } percent
            ? string.Create(CultureInfo.InvariantCulture, $"{percent:F1}% processor")
            : "processor not readable";
        var memory = MemoryBytes is { } bytes
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F0} MB")
            : "memory not readable";
        return $"{Name} (process {Pid}) {processor}, {memory}";
    }
}

/// <summary>
/// Every process on the machine.
///
/// THE FIRST READ AFTER START-UP STILL RETURNS A LIST. Every process is named, with
/// <see cref="ProcessReading.CpuPercent"/> null, because a processor share is a rate and the first read
/// has no interval to divide by - but a name, a parent and a memory figure are not rates and are
/// readable on the first sample. Nullable figures exist exactly so this case needs no empty list.
///
/// THIS IS ONE RULE AND THERE IS ONLY ONE. An earlier wording had the whole set unreadable with
/// <see cref="ResourceUnreadableReasons.FirstSample"/> on the first read, and the two platform branches
/// written against it had already split: one returned an unreadable set, the other a readable list. The
/// result would have been a detail screen whose process list is empty for one sample on Linux and
/// populated on macOS, which no test downstream could see. <see cref="Unreadable"/> is for a list that
/// genuinely could not be read at all - the platform refused the enumeration - and never for the first
/// sample.
/// </summary>
/// <param name="CouldRead">False when the process list could not be read.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="Processes">Each process. Empty when the read failed.</param>
public sealed record ProcessSetReading(
    bool CouldRead,
    string? UnreadableReason,
    IReadOnlyList<ProcessReading> Processes)
{
    /// <summary>A reading that succeeded.</summary>
    public static ProcessSetReading Read(IReadOnlyList<ProcessReading> processes) =>
        new(CouldRead: true, UnreadableReason: null, processes);

    /// <summary>A reading that failed, carrying why. There are no processes in it.</summary>
    public static ProcessSetReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), Array.Empty<ProcessReading>());

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString() =>
        CouldRead ? $"{Processes.Count} processes" : $"processes unreadable ({UnreadableReason})";
}
