using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Machine;

/// <summary>
/// Where one recorded line sits on the recording's own timeline. Worked out by
/// <see cref="ResourceRecorder"/>; written by <see cref="ResourceRecordingFormat"/>.
/// </summary>
/// <param name="Seq">The line's place in the recording: one more than the line before, across every Director run.</param>
/// <param name="RunId">Which Director run wrote it. A new run begins each time the recorder starts.</param>
/// <param name="RunElapsedSeconds">Seconds since this run's first sample, off the MONOTONIC clock.</param>
/// <param name="RecordingSeconds">
/// Seconds on the recording's own timeline: monotonic inside a run, and across a restart the previous
/// run's end plus the wall-clock gap (never less than zero). "The last hour" is measured on this.
/// </param>
/// <param name="IsRunStart">True on the first line a run writes.</param>
/// <param name="RestartGapSeconds">On a run's first line after an earlier run: the gap the wall clock showed. Null otherwise.</param>
/// <param name="ClockWentBackwardsAtRestart">True when that gap was negative and was taken as zero.</param>
/// <param name="ClockStepSeconds">
/// How far the wall clock moved against the monotonic clock since the previous line of this run, when it
/// moved by more than <see cref="ResourceRecorder.ClockStepTolerance"/>. Null when it did not.
/// </param>
/// <param name="SamplesLostBefore">Samples dropped unwritten just before this one, because the disk refused the writes.</param>
public readonly record struct RecordingPosition(
    long Seq,
    Guid RunId,
    double RunElapsedSeconds,
    double RecordingSeconds,
    bool IsRunStart,
    double? RestartGapSeconds,
    bool ClockWentBackwardsAtRestart,
    double? ClockStepSeconds,
    int SamplesLostBefore);

/// <summary>
/// A process in the recording, with the session it belongs to now and - when it has outlived or left
/// its session - the session it used to belong to.
/// </summary>
internal sealed record RecordedProcess(
    ProcessReading Process,
    string? SessionId,
    string? FormerSessionId,
    string? FormerSessionName,
    bool? FormerSessionEnded);

/// <summary>
/// What the recorder worked out about the process list for one line: which processes to name, and the
/// counts that say what was left out.
/// </summary>
internal sealed record RecordedProcessSummary(
    IReadOnlyList<RecordedProcess> Listed,
    int OutlivedOrLeftCount,
    int OutlivedOrLeftNotListed);

/// <summary>
/// THE ONE SHAPE OF A RECORDED LINE. A pure function: the same moment at the same position always gives
/// the same bytes, which is what lets a committed recording be both the recorder's golden output and the
/// diagnose command's test input - one file holds the two ends of the contract together.
///
/// EVERY KEY IS ALWAYS WRITTEN, with null where there is nothing, so a reader in another language never
/// has to tell "absent" from "null". A figure that could not be read is WRITTEN AS NULL with its reason
/// beside it, never as the meaningless zero the reading types carry when <c>CouldRead</c> is false - the
/// recording is read hours later by something that never saw the CouldRead flag go by.
///
/// Version 1. A change that renames or removes a key is a new version, because
/// <c>cc-devthrottle machine diagnose --json</c> passes these lines through and other code parses them.
/// </summary>
internal static class ResourceRecordingFormat
{
    public const int Version = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>One recorded line, without its line ending.</summary>
    public static string Line(RecordingPosition position, ResourceMoment moment, RecordedProcessSummary processes)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            var sample = moment.Sample;
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteNumber("seq", position.Seq);
            w.WriteString("run", position.RunId.ToString("D"));
            w.WriteNumber("runElapsedSeconds", Round(position.RunElapsedSeconds));
            w.WriteNumber("recordingSeconds", Round(position.RecordingSeconds));
            w.WriteString("takenAtUtc", Stamp(sample.TakenAtUtc));
            w.WriteBoolean("runStart", position.IsRunStart);
            WriteNumberOrNull(w, "restartGapSeconds", position.RestartGapSeconds is { } gap ? Round(gap) : null);
            w.WriteBoolean("clockWentBackwardsAtRestart", position.ClockWentBackwardsAtRestart);
            WriteNumberOrNull(w, "clockStepSeconds", position.ClockStepSeconds is { } step ? Round(step) : null);
            w.WriteNumber("samplesLostBefore", position.SamplesLostBefore);

            WriteCpu(w, sample.Cpu);
            WriteMemory(w, sample.Memory);
            WriteSwap(w, sample.Swap);
            WriteStall(w, sample.Stall);
            WriteDisks(w, sample.DiskEvidence);
            WriteProcesses(w, sample.Processes, processes);
            WriteSessions(w, moment.Sessions);
            WriteVerdict(w, moment.Verdict);
            WriteDiagnosis(w, moment.Diagnosis);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCpu(Utf8JsonWriter w, CpuReading cpu)
    {
        w.WriteStartObject("cpu");
        WriteReadState(w, cpu.CouldRead, cpu.UnreadableReason);
        WriteNumberOrNull(w, "totalBusyPercent", cpu.CouldRead ? cpu.TotalBusyPercent : null);
        WriteNumberOrNull(w, "busiestCorePercent", cpu.CouldRead ? cpu.BusiestCorePercent : null);
        WriteNumberOrNull(w, "logicalCoreCount", cpu.LogicalCoreCount);
        w.WriteEndObject();
    }

    private static void WriteMemory(Utf8JsonWriter w, MachineMemoryReading memory)
    {
        w.WriteStartObject("memory");
        WriteReadState(w, memory.CouldRead, memory.UnreadableReason);
        WriteNumberOrNull(w, "totalBytes", memory.CouldRead ? memory.TotalBytes : null);
        WriteNumberOrNull(w, "availableBytes", memory.CouldRead ? memory.AvailableBytes : null);
        w.WriteEndObject();
    }

    private static void WriteSwap(Utf8JsonWriter w, SwapReading swap)
    {
        w.WriteStartObject("swap");
        WriteReadState(w, swap.CouldRead, swap.UnreadableReason);
        WriteNumberOrNull(w, "totalBytes", swap.CouldRead ? swap.TotalBytes : null);
        WriteNumberOrNull(w, "usedBytes", swap.CouldRead ? swap.UsedBytes : null);
        w.WriteEndObject();
    }

    private static void WriteStall(Utf8JsonWriter w, StallReading stall)
    {
        w.WriteStartObject("stall");
        WriteReadState(w, stall.CouldRead, stall.UnreadableReason);
        w.WriteString("source", stall.Source.ToString());
        WriteNumberOrNull(w, "cpuStalledPercent", stall.CpuStalledPercent);
        WriteNumberOrNull(w, "memoryStalledPercent", stall.MemoryStalledPercent);
        WriteNumberOrNull(w, "ioStalledPercent", stall.IoStalledPercent);
        WriteNumberOrNull(w, "runQueueLength", stall.RunQueueLength);
        WriteNumberOrNull(w, "loadAverageOneMinute", stall.LoadAverageOneMinute);
        WriteNumberOrNull(w, "hardPageFaultsPerSecond", stall.HardPageFaultsPerSecond);
        WriteNumberOrNull(w, "macOsMemoryPressureLevel", stall.MacOsMemoryPressureLevel);
        WriteNumberOrNull(w, "processesWaitingOnDisk", stall.ProcessesWaitingOnDisk);
        w.WriteEndObject();
    }

    /// <summary>
    /// The disks, FOR THE DIAGNOSIS ONLY - written so "what made it slow at 08:26" can name the disk, and
    /// never rendered as a figure of its own. See <see cref="ResourceSample"/>.
    /// </summary>
    private static void WriteDisks(Utf8JsonWriter w, DiskEvidenceSet disks)
    {
        w.WriteStartObject("diskEvidence");
        WriteReadState(w, disks.CouldRead, disks.UnreadableReason);
        w.WriteStartArray("disks");
        foreach (var disk in disks.Disks)
        {
            w.WriteStartObject();
            w.WriteString("name", disk.Name);
            w.WriteString("kind", disk.Kind.ToString());
            WriteBoolOrNull(w, "isInSystemVolume", disk.IsInSystemVolume);
            WriteReadState(w, disk.CouldRead, disk.UnreadableReason);
            WriteNumberOrNull(w, "busyPercent", disk.BusyPercent);
            WriteNumberOrNull(w, "millisecondsPerRead", disk.MillisecondsPerRead);
            WriteNumberOrNull(w, "millisecondsPerWrite", disk.MillisecondsPerWrite);
            WriteNumberOrNull(w, "averageQueueDepth", disk.AverageQueueDepth);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteProcesses(Utf8JsonWriter w, ProcessSetReading set, RecordedProcessSummary summary)
    {
        w.WriteStartObject("processes");
        WriteReadState(w, set.CouldRead, set.UnreadableReason);
        // When the list could not be read there are no counts: zero processes would be a made-up figure.
        int? Counted(int value) => set.CouldRead ? value : null;
        WriteNumberOrNull(w, "count", Counted(set.Processes.Count));
        WriteNumberOrNull(w, "withUnknownProcessor", Counted(set.Processes.Count(p => p.CpuPercent is null)));
        WriteNumberOrNull(w, "withUnknownMemory", Counted(set.Processes.Count(p => p.MemoryBytes is null)));
        WriteNumberOrNull(w, "outlivedOrLeftSession", Counted(summary.OutlivedOrLeftCount));
        WriteNumberOrNull(w, "outlivedOrLeftSessionNotListed", Counted(summary.OutlivedOrLeftNotListed));
        w.WriteStartArray("listed");
        foreach (var recorded in summary.Listed)
        {
            var p = recorded.Process;
            w.WriteStartObject();
            w.WriteNumber("pid", p.Pid);
            WriteNumberOrNull(w, "parentPid", p.ParentPid);
            w.WriteString("name", p.Name);
            WriteStringOrNull(w, "startTimeUtc", p.StartTimeUtc is { } start ? Stamp(start) : null);
            WriteNumberOrNull(w, "cpuPercent", p.CpuPercent);
            WriteNumberOrNull(w, "memoryBytes", p.MemoryBytes);
            WriteNumberOrNull(w, "diskReadBytesPerSecond", p.DiskReadBytesPerSecond);
            WriteNumberOrNull(w, "diskWriteBytesPerSecond", p.DiskWriteBytesPerSecond);
            WriteStringOrNull(w, "sessionId", recorded.SessionId);
            WriteStringOrNull(w, "formerSessionId", recorded.FormerSessionId);
            WriteStringOrNull(w, "formerSessionName", recorded.FormerSessionName);
            WriteBoolOrNull(w, "formerSessionEnded", recorded.FormerSessionEnded);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSessions(Utf8JsonWriter w, IReadOnlyList<RecordedSession> sessions)
    {
        w.WriteStartArray("sessions");
        foreach (var session in sessions)
        {
            var u = session.Usage;
            w.WriteStartObject();
            w.WriteString("sessionId", u.SessionId);
            WriteStringOrNull(w, "name", session.Name);
            w.WriteNumber("rootPid", u.RootPid);
            w.WriteNumber("processCount", u.Pids.Count);
            // The two sums are LOWER BOUNDS when their count of unknowns is above zero; the counts travel
            // beside them so a reader can say "at least". See SessionUsage. When NO process in the tree could
            // be read, the sum is zero over nothing, and that zero is written as null: it measured nothing.
            var noneReadForProcessor = u.ProcessesWithUnknownProcessor >= u.Pids.Count;
            var noneReadForMemory = u.ProcessesWithUnknownMemory >= u.Pids.Count;
            WriteNumberOrNull(w, "cpuPercent", noneReadForProcessor ? null : u.CpuPercent);
            w.WriteNumber("processesWithUnknownProcessor", u.ProcessesWithUnknownProcessor);
            WriteNumberOrNull(w, "memoryBytes", noneReadForMemory ? null : u.MemoryBytes);
            w.WriteNumber("processesWithUnknownMemory", u.ProcessesWithUnknownMemory);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteVerdict(Utf8JsonWriter w, ResourcePressureVerdict? verdict)
    {
        if (verdict is null)
        {
            w.WriteNull("verdict");
            return;
        }
        w.WriteStartObject("verdict");
        w.WriteString("worst", verdict.Worst.ToString());
        w.WriteStartArray("resources");
        foreach (var resource in verdict.Resources)
        {
            w.WriteStartObject();
            w.WriteString("kind", resource.Kind.ToString());
            w.WriteString("level", resource.Level.ToString());
            w.WriteString("evidence", resource.Evidence);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDiagnosis(Utf8JsonWriter w, SlownessDiagnosis? diagnosis)
    {
        if (diagnosis is null)
        {
            w.WriteNull("diagnosis");
            return;
        }
        w.WriteStartObject("diagnosis");
        w.WriteBoolean("couldDiagnose", diagnosis.CouldDiagnose);
        w.WriteBoolean("isSlow", diagnosis.IsSlow);
        w.WriteStartArray("causes");
        foreach (var cause in new[] { SlownessCause.Cpu, SlownessCause.Memory, SlownessCause.Disk })
        {
            if (diagnosis.Cause.HasFlag(cause)) w.WriteStringValue(cause.ToString());
        }
        w.WriteEndArray();
        w.WriteString("summary", diagnosis.Summary);
        w.WriteStartArray("evidence");
        foreach (var line in diagnosis.Evidence) w.WriteStringValue(line);
        w.WriteEndArray();
        w.WriteString("nextStep", diagnosis.NextStep);
        if (diagnosis.Culprit is { } c)
        {
            w.WriteStartObject("culprit");
            WriteStringOrNull(w, "processName", c.ProcessName);
            WriteNumberOrNull(w, "pid", c.Pid);
            WriteStringOrNull(w, "sessionId", c.SessionId);
            WriteStringOrNull(w, "sessionName", c.SessionName);
            WriteStringOrNull(w, "diskName", c.DiskName);
            WriteStringOrNull(w, "diskKind", c.DiskKind?.ToString());
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("culprit");
        }
        w.WriteEndObject();
    }

    private static void WriteReadState(Utf8JsonWriter w, bool couldRead, string? reason)
    {
        w.WriteBoolean("couldRead", couldRead);
        WriteStringOrNull(w, "unreadableReason", reason);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v)) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter w, string name, ulong? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteNumberOrNull(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void WriteBoolOrNull(Utf8JsonWriter w, string name, bool? value)
    {
        if (value is { } v) w.WriteBoolean(name, v);
        else w.WriteNull(name);
    }

    private static double Round(double seconds) => Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

    /// <summary>An ISO 8601 UTC stamp with whole seconds and a Z, the same on every platform and culture.</summary>
    internal static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
