using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE 24-HOUR LOCAL RECORDING (the Resource Monitor mission, issue #2811): every five-second sample,
/// kept on this machine's disk for 24 hours, so "what made it slow at 08:26?" can be answered after the
/// slowdown has passed and with no Gateway at all. <c>cc-devthrottle machine diagnose</c> reads it.
///
/// IT IS WRITTEN ONCE A MINUTE, IN ONE APPEND. This runs on machines that are already thrashing their
/// disk - devlinux on 2026-10-15 had programs stalled on the disk a third of the time - and a recorder
/// that wrote every five seconds would add twelve small writes a minute to exactly that queue. So samples
/// are held in memory and written together, with one write call, once <see cref="WriteEvery"/> has
/// passed on the monotonic clock. Nothing is forced to the platter (no flush-to-disk): the cost of that on
/// a slow disk is the whole point of batching, and the price is that a power cut can lose the last minute
/// or so. Stopping the Director cleanly writes what is held (<see cref="Dispose"/>).
///
/// THE RECORDING IS FILES OF ABOUT ONE HOUR EACH, named by sequence number, never by the clock. A line's
/// <c>seq</c> is one more than the line before it, across every Director run, and files are ordered and
/// pruned on it. A file starts a new one only at the start of a write, so one write is always one append;
/// a file therefore holds at most <see cref="SamplesPerFile"/> plus one held batch, an hour and a half at
/// the worst, after a long refused write.
///
/// A WRITE THAT FAILS PART WAY NEVER HAS ANOTHER WRITE GLUED ONTO IT. The retry goes to a new file with a
/// part number (<c>samples-000000000060-1.jsonl</c>), because a refused append can leave whole lines and
/// a torn one behind, and the retry carries those same samples again. Both readers keep the first whole
/// line of each <c>seq</c> and count the rest as repeated, so a sample is never counted twice.
///
/// IT IS PRUNED ON SEQUENCE NUMBERS, NEVER BY THE WALL CLOCK. It keeps the newest
/// <see cref="RetainedSamples"/> sequence numbers - 24 hours of sampling at one every five seconds - and
/// deletes whole files of the oldest once a file can go with at least that span left. Samples lost to a
/// refused write fall inside that span, so after a loss it holds fewer than 17,280 lines and still exactly
/// 24 hours of sampling: the promise is 24 hours, and older samples are not kept to make up the number.
/// Pruning by "older than 24 hours
/// on the wall clock" is the defect this design refuses: a clock that jumps a day forward would delete
/// the whole recording, which is the evidence it exists to keep, and one that jumps backwards would keep
/// stale lines forever and file new ones before old ones. A count cannot be moved by any clock. The cost,
/// said plainly: after the Director has been stopped for a while, lines older than 24 hours on the wall
/// clock remain until enough new samples arrive to push them out. That is bounded, and nothing is lost.
///
/// "THE LAST HOUR" IS MEASURED ON THE RECORDING'S OWN TIMELINE, NOT THE WALL CLOCK. Each line carries
/// <c>recordingSeconds</c>: inside one run it advances only with the MONOTONIC clock, so a wall-clock
/// step during a run cannot stretch, shrink or reorder it. A monotonic clock does not survive a restart,
/// so across one the gap is taken from the wall clock - never less than zero, and the line says when the
/// wall clock went backwards across a restart. A wall-clock step DURING a run is recorded on the line
/// where it happened (<c>clockStepSeconds</c>), so a reader looking for 08:26 knows that two different
/// moments may carry that stamp.
///
/// A PROCESS THAT OUTLIVES ITS SESSION DOES NOT VANISH. The machine's own figures - processor, memory,
/// swap, stalls - are the kernel's whole-machine totals and never sums of sessions, so a process whose
/// session has gone is still in them by construction. On top of that the recorder remembers which session
/// each process last belonged to, keyed on its process id AND its start time (so a reused id inherits
/// nothing), and when a process stops belonging to any session the line still names it, with
/// <c>formerSessionId</c> and whether that session has ENDED or the process merely LEFT a session that is
/// still running. A process that outlived its session is always listed, up to
/// <see cref="OutlivedListedAtMost"/>, whatever its rank, and the count of any not listed is written. It
/// is NOT added back to the session's totals: <see cref="SessionUsage"/> is what the process tree holds,
/// and a session that has ended has no totals. Without a start time nothing can be claimed, so a process
/// whose start time the platform does not offer is never marked as having outlived anything.
///
/// THE PROCESS LIST IS SHORTENED, AND THE LINE SAYS BY HOW MUCH. A machine runs hundreds of processes and
/// 17,280 copies of them would be gigabytes a day, so a line names the <see cref="TopProcessesPerFigure"/>
/// busiest by processor, the same number by memory, and the processes that outlived or left a session,
/// and it writes the full count and how many had unknown figures. A process whose figures are both
/// unknown cannot be ranked and is counted, not named.
///
/// THREAD-SAFE: the sampler records from its loop, and the Director stops on another thread, so every
/// public call takes one lock. Every call reads or writes the disk, so none belongs on the user-interface
/// thread.
/// </summary>
public sealed class ResourceRecorder : IDisposable
{
    /// <summary>How often the sampler takes a sample. The retention count is worked out from it.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>How much the recording keeps.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>How often held samples are written, in one append.</summary>
    public static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(1);

    /// <summary>The newest this many samples are kept: 24 hours at one every five seconds, 17,280.</summary>
    public static readonly int RetainedSamples = (int)(Retention / SampleInterval);

    /// <summary>Lines per file: one hour of samples. A file is the unit of pruning.</summary>
    public static readonly int SamplesPerFile = (int)(TimeSpan.FromHours(1) / SampleInterval);

    /// <summary>
    /// How many samples are held while the disk refuses writes before the oldest held one is dropped:
    /// thirty minutes. The next line written then says how many were lost, and the gap in <c>seq</c>
    /// shows where.
    /// </summary>
    public static readonly int HeldAtMost = (int)(TimeSpan.FromMinutes(30) / SampleInterval);

    /// <summary>The busiest processes named per line, by processor and again by memory.</summary>
    public const int TopProcessesPerFigure = 8;

    /// <summary>Processes that outlived or left their session, named per line whatever their rank.</summary>
    public const int OutlivedListedAtMost = 16;

    /// <summary>
    /// A wall-clock move against the monotonic clock smaller than this, between two lines, is drift and
    /// scheduling jitter and is not recorded as a step.
    /// </summary>
    public static readonly TimeSpan ClockStepTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The files' name: a fixed prefix and the first line's sequence number, twelve digits.</summary>
    internal const string FilePrefix = "samples-";
    internal const string FileExtension = ".jsonl";

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Guid _runId;

    // Where the previous run left the recording, read once when this run starts.
    private long _nextSeq;
    private double? _previousRunEndRecordingSeconds;
    private DateTime? _previousRunEndWallUtc;

    // This run's timeline.
    private bool _runStarted;
    private long _runStartTimestamp;
    private DateTime _runStartWallUtc;
    private double _runBaseRecordingSeconds;
    private double? _previousWallOffsetSeconds;

    // Held lines and the write schedule.
    // Held whole, and written out only when the write happens, so the first line written after a loss can
    // carry the count of what was lost just before it.
    private readonly List<(RecordingPosition Position, ResourceMoment Moment, RecordedProcessSummary Processes)> _held = new();
    private long _lastWriteTimestamp;
    private readonly object _gate = new();
    private string? _currentFile;
    private int _currentFileLines;
    // True from the moment an append starts until it has finished. Still true at the next write means the
    // last one failed part way, so the next one must start a new file.
    private bool _appendUnfinished;
    private int _lostUnreported;
    private int _lostTotal;

    // Which session each live process last belonged to, by (process id, start time).
    private readonly Dictionary<(int Pid, DateTime Start), (string SessionId, string? Name)> _lastSession = new();

    private bool _disposed;

    /// <summary>
    /// Opens the recording in <paramref name="directory"/>, creating it if needed, and reads where the
    /// previous run left off so sequence numbers and the timeline carry on.
    /// </summary>
    /// <param name="directory">The recording's folder - <see cref="Storage.CcStorage.ResourceRecording"/> in the Director.</param>
    /// <param name="time">The clock. Injected so a test can step the wall clock and advance the monotonic one independently.</param>
    /// <param name="runId">This run's identifier. A new one when null; a test passes a fixed one for byte-exact output.</param>
    public ResourceRecorder(string directory, TimeProvider? time = null, Guid? runId = null)
    {
        FileLog.Write($"[ResourceRecorder] Open: directory={directory}");
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The recording needs a folder.", nameof(directory));

        _directory = directory;
        _time = time ?? TimeProvider.System;
        _runId = runId ?? Guid.NewGuid();

        Directory.CreateDirectory(_directory);
        ReadWhereThePreviousRunEnded();
        FileLog.Write($"[ResourceRecorder] Open: run={_runId}, nextSeq={_nextSeq}, " +
                      $"previousRunEnd={(_previousRunEndWallUtc is { } end ? ResourceRecordingFormat.Stamp(end) : "none")}");
    }

    /// <summary>Samples taken by this run and not yet written.</summary>
    public int HeldCount
    {
        get { lock (_gate) return _held.Count; }
    }

    /// <summary>Samples this run dropped unwritten because the disk refused the writes.</summary>
    public int LostCount
    {
        get { lock (_gate) return _lostTotal; }
    }

    /// <summary>
    /// Records one sample. Writes the held samples in one append when <see cref="WriteEvery"/> has passed
    /// on the monotonic clock since the last write.
    ///
    /// A WRITE THE DISK REFUSES DOES NOT LOSE THE SAMPLES AND DOES NOT STOP THE SAMPLER: they stay held and
    /// the next minute tries again, and only past <see cref="HeldAtMost"/> is the oldest dropped - counted
    /// and written onto the next line. This is the one place the recorder catches, because it is the
    /// boundary between the sampler's loop and the disk: a full disk must not take the meter down with it.
    /// </summary>
    public void Record(ResourceMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        lock (_gate) RecordLocked(moment);
    }

    private void RecordLocked(ResourceMoment moment)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var timestamp = _time.GetTimestamp();
        var wallUtc = _time.GetUtcNow().UtcDateTime;
        var position = Position(timestamp, wallUtc);
        var processes = SummariseProcesses(moment);

        _held.Add((position, moment, processes));

        if (_held.Count > HeldAtMost)
        {
            var drop = _held.Count - HeldAtMost;
            _held.RemoveRange(0, drop);
            _lostUnreported += drop;
            _lostTotal += drop;
            FileLog.Write($"[ResourceRecorder] Record: {drop} unwritten samples dropped, the disk has refused writes for " +
                          $"{(HeldAtMost * SampleInterval).TotalMinutes:F0} minutes");
        }

        if (_time.GetElapsedTime(_lastWriteTimestamp, timestamp) < WriteEvery) return;

        try
        {
            Write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[ResourceRecorder] Record: write FAILED, {_held.Count} samples held for the next minute: {ex.Message}");
            // Try again in a minute, not on every sample: a refusing disk is not asked twelve times a minute.
            // The retry starts a new file on its own - see _appendUnfinished.
            _lastWriteTimestamp = timestamp;
        }
    }

    /// <summary>
    /// Writes every held sample now, in one append. Called when the Director stops; the sampler does not
    /// need to call it otherwise. Throws when the disk refuses, so a caller stopping the Director can log
    /// what was lost.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FileLog.Write($"[ResourceRecorder] Flush: held={_held.Count}");
            Write();
        }
    }

    /// <summary>Writes what is held and closes the recording. A refused write is logged; the samples are then lost.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FileLog.Write($"[ResourceRecorder] Dispose: held={_held.Count}");
            try
            {
                Write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Write($"[ResourceRecorder] Dispose: final write FAILED, {_held.Count} samples lost: {ex.Message}");
            }
            _disposed = true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The timeline
    // ---------------------------------------------------------------------------------------------

    private RecordingPosition Position(long timestamp, DateTime wallUtc)
    {
        var seq = _nextSeq++;

        if (!_runStarted)
        {
            _runStarted = true;
            _runStartTimestamp = timestamp;
            _runStartWallUtc = wallUtc;
            _lastWriteTimestamp = timestamp;
            _previousWallOffsetSeconds = 0;

            double? gap = null;
            var backwards = false;
            if (_previousRunEndRecordingSeconds is { } previousEnd && _previousRunEndWallUtc is { } previousWall)
            {
                gap = (wallUtc - previousWall).TotalSeconds;
                backwards = gap < 0;
                // A negative gap is a wall clock that went backwards while the Director was stopped. Taking it
                // as zero keeps the timeline moving forward; the line says so, so nothing is hidden.
                _runBaseRecordingSeconds = previousEnd + Math.Max(0, gap.Value);
                if (backwards)
                    FileLog.Write($"[ResourceRecorder] Position: the wall clock is {-gap.Value:F0} seconds BEHIND the previous " +
                                  "run's last sample; the gap is taken as zero");
            }

            return new RecordingPosition(seq, _runId, 0, _runBaseRecordingSeconds, IsRunStart: true,
                RestartGapSeconds: gap, ClockWentBackwardsAtRestart: backwards, ClockStepSeconds: null,
                SamplesLostBefore: 0);
        }

        var elapsed = _time.GetElapsedTime(_runStartTimestamp, timestamp).TotalSeconds;

        // How far the wall clock sits from where the monotonic clock says it should be. A change in that
        // offset between two lines is the wall clock being stepped - time synchronisation, a person, a
        // virtual machine resumed - and it is written on the line where it happened.
        var offset = (wallUtc - _runStartWallUtc).TotalSeconds - elapsed;
        double? step = null;
        if (_previousWallOffsetSeconds is { } previousOffset &&
            Math.Abs(offset - previousOffset) > ClockStepTolerance.TotalSeconds)
        {
            step = offset - previousOffset;
            FileLog.Write($"[ResourceRecorder] Position: the wall clock stepped {step.Value:F1} seconds at seq={seq}");
        }
        _previousWallOffsetSeconds = offset;

        return new RecordingPosition(seq, _runId, elapsed, _runBaseRecordingSeconds + elapsed, IsRunStart: false,
            RestartGapSeconds: null, ClockWentBackwardsAtRestart: false, ClockStepSeconds: step,
            SamplesLostBefore: 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Which processes a line names, and the processes that outlived their session
    // ---------------------------------------------------------------------------------------------

    private RecordedProcessSummary SummariseProcesses(ResourceMoment moment)
    {
        var set = moment.Sample.Processes;
        if (!set.CouldRead)
        {
            // Nothing is known about the processes this time. What was remembered stays remembered: a list
            // that failed to read is not evidence that every process ended.
            return new RecordedProcessSummary(Array.Empty<RecordedProcess>(), 0, 0);
        }

        var sessionOfPid = new Dictionary<int, (string SessionId, string? Name)>();
        var runningSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in moment.Sessions)
        {
            runningSessions.Add(session.Usage.SessionId);
            foreach (var pid in session.Usage.Pids) sessionOfPid[pid] = (session.Usage.SessionId, session.Name);
        }

        var recorded = new List<RecordedProcess>(set.Processes.Count);
        var stillAlive = new HashSet<(int, DateTime)>();
        foreach (var process in set.Processes)
        {
            (int, DateTime)? key = process.StartTimeUtc is { } start ? (process.Pid, start) : null;
            if (key is { } k) stillAlive.Add(k);

            if (sessionOfPid.TryGetValue(process.Pid, out var current))
            {
                if (key is { } k2) _lastSession[k2] = current;
                recorded.Add(new RecordedProcess(process, current.SessionId, null, null, null));
            }
            else if (key is { } k3 && _lastSession.TryGetValue(k3, out var former))
            {
                recorded.Add(new RecordedProcess(process, null, former.SessionId, former.Name,
                    FormerSessionEnded: !runningSessions.Contains(former.SessionId)));
            }
            else
            {
                recorded.Add(new RecordedProcess(process, null, null, null, null));
            }
        }

        // Forget processes that have gone, so a reused process id never inherits a session.
        foreach (var gone in _lastSession.Keys.Where(k => !stillAlive.Contains(k)).ToList())
            _lastSession.Remove(gone);

        var byProcessor = recorded.Where(r => r.Process.CpuPercent is not null)
            .OrderByDescending(r => r.Process.CpuPercent).ThenBy(r => r.Process.Pid).Take(TopProcessesPerFigure);
        var byMemory = recorded.Where(r => r.Process.MemoryBytes is not null)
            .OrderByDescending(r => r.Process.MemoryBytes).ThenBy(r => r.Process.Pid).Take(TopProcessesPerFigure);
        var outlived = recorded.Where(r => r.FormerSessionId is not null).ToList();
        var outlivedListed = outlived
            .OrderByDescending(r => r.Process.MemoryBytes ?? 0).ThenBy(r => r.Process.Pid).Take(OutlivedListedAtMost);

        var listed = byProcessor.Concat(byMemory).Concat(outlivedListed)
            .DistinctBy(r => r.Process.Pid)
            .OrderByDescending(r => r.Process.CpuPercent ?? -1)
            .ThenByDescending(r => r.Process.MemoryBytes ?? 0)
            .ThenBy(r => r.Process.Pid)
            .ToList();

        return new RecordedProcessSummary(listed, outlived.Count, Math.Max(0, outlived.Count - OutlivedListedAtMost));
    }

    // ---------------------------------------------------------------------------------------------
    // The files
    // ---------------------------------------------------------------------------------------------

    private void Write()
    {
        if (_held.Count == 0) return;

        // A run's first write starts a new file, and so does a full one: a file only ever holds lines of one
        // run, so a line torn by a crash at the end of a file is never followed by another run's lines.
        // And so does a write after one that failed part way, so nothing is ever appended after a torn line.
        if (_currentFile is null || _currentFileLines >= SamplesPerFile || _appendUnfinished)
        {
            _currentFile = UnusedFileName(_held[0].Position.Seq);
            _currentFileLines = 0;
        }

        // The samples dropped while the disk refused writes came just before the oldest one still held, so
        // that is the line that says how many - and the gap in seq says the same thing a second way.
        var text = new StringBuilder();
        for (var i = 0; i < _held.Count; i++)
        {
            var (position, moment, processes) = _held[i];
            if (i == 0 && _lostUnreported > 0) position = position with { SamplesLostBefore = _lostUnreported };
            text.Append(ResourceRecordingFormat.Line(position, moment, processes)).Append('\n');
        }
        var bytes = Encoding.UTF8.GetBytes(text.ToString());

        // ONE append. FileShare.Read so a reader can open the file while the Director writes; the readers
        // open it sharing write in turn (see ReadLinesShared), which Windows needs from both sides.
        _appendUnfinished = true;
        using (var stream = new FileStream(_currentFile, FileMode.Append, FileAccess.Write,
                   FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.None))
        {
            stream.Write(bytes, 0, bytes.Length);
        }
        _appendUnfinished = false;

        var written = _held.Count;
        _currentFileLines += written;
        _held.Clear();
        _lostUnreported = 0;
        _lastWriteTimestamp = _time.GetTimestamp();
        FileLog.Write($"[ResourceRecorder] Write: {written} samples, {bytes.Length} bytes, file={Path.GetFileName(_currentFile)}");

        Prune();
    }

    /// <summary>
    /// Deletes whole files of the oldest samples while at least <see cref="RetainedSamples"/> would remain.
    /// Counted on sequence numbers, so no clock can make it delete more.
    /// </summary>
    private void Prune()
    {
        var files = RecordingFiles(_directory);
        var newestSeq = _nextSeq - 1;
        var deleted = 0;
        var kept = 0;
        // A file may go when the file after it still starts within the newest RetainedSamples sequence numbers.
        for (var i = 0; i + 1 < files.Count && newestSeq - files[i + 1].FirstSeq + 1 >= RetainedSamples; i++)
        {
            try
            {
                File.Delete(files[i].Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // On Windows a file cannot be deleted while another program has it open - the diagnose
                // command, an indexer, an editor - and a read-only file refuses as well. The samples were
                // written, so this is not a failed write. Leave this file for the next minute's prune and go
                // on to the next old one, so one stuck file never stops the recording being bounded.
                // not-an-error: the samples are written; this file is tried again a minute from now.
                FileLog.Write($"[ResourceRecorder] Prune: could not delete {Path.GetFileName(files[i].Path)} yet, will retry: {ex.Message}");
                kept++;
            }
        }
        if (deleted > 0 || kept > 0)
            FileLog.Write($"[ResourceRecorder] Prune: deleted {deleted} file(s), {kept} could not be deleted yet; newest seq={newestSeq}");
    }

    private void ReadWhereThePreviousRunEnded()
    {
        var files = RecordingFiles(_directory);
        if (files.Count == 0) return;

        // Read backwards from the newest file until a whole line is found. A line torn by a crash is
        // skipped here, and the reader skips it too and counts it.
        for (var i = files.Count - 1; i >= 0; i--)
        {
            var lines = ReadLinesShared(files[i].Path);
            for (var j = lines.Length - 1; j >= 0; j--)
            {
                if (!TryReadPosition(lines[j], out var seq, out var recordingSeconds, out var takenAtUtc)) continue;
                _nextSeq = seq + 1;
                _previousRunEndRecordingSeconds = recordingSeconds;
                _previousRunEndWallUtc = takenAtUtc;
                return;
            }
        }

        // Files exist but hold no whole line. Carry on after the highest file's number so no seq repeats.
        _nextSeq = files[^1].FirstSeq + SamplesPerFile;
        FileLog.Write($"[ResourceRecorder] ReadWhereThePreviousRunEnded: no readable line in {files.Count} file(s); starting at seq={_nextSeq}");
    }

    internal static bool TryReadPosition(string line, out long seq, out double recordingSeconds, out DateTime takenAtUtc)
    {
        seq = 0;
        recordingSeconds = 0;
        takenAtUtc = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("seq", out var s) || !s.TryGetInt64(out seq)) return false;
            if (!root.TryGetProperty("recordingSeconds", out var r) || !r.TryGetDouble(out recordingSeconds)) return false;
            if (!root.TryGetProperty("takenAtUtc", out var t) || t.ValueKind != JsonValueKind.String) return false;
            return DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out takenAtUtc);
        }
        catch (JsonException)
        {
            // A line torn by a crash or a refused write is expected input here, not a fault: the platform
            // offers no non-throwing parse, so this is how "not a whole line" is answered.
            return false;
        }
    }

    internal static string FileName(long firstSeq, int part = 0) =>
        FilePrefix + firstSeq.ToString("D12", CultureInfo.InvariantCulture) +
        (part == 0 ? "" : "-" + part.ToString(CultureInfo.InvariantCulture)) + FileExtension;

    /// <summary>A file name for a write starting at <paramref name="firstSeq"/> that no file has yet.</summary>
    private string UnusedFileName(long firstSeq)
    {
        for (var part = 0; ; part++)
        {
            var path = Path.Combine(_directory, FileName(firstSeq, part));
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>
    /// The recording's files, oldest first by the sequence number in their name, then by part. A name that
    /// is not the recording's own shape is not the recording's and is left alone.
    /// </summary>
    internal static List<(string Path, long FirstSeq, int Part)> RecordingFiles(string directory)
    {
        var files = new List<(string Path, long FirstSeq, int Part)>();
        if (!Directory.Exists(directory)) return files;
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(path).AsSpan(FilePrefix.Length);
            var dash = name.IndexOf('-');
            var seqText = dash < 0 ? name : name[..dash];
            var part = 0;
            if (seqText.Length != 12) continue;
            if (!long.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var first)) continue;
            if (dash >= 0 && (!int.TryParse(name[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out part) || part < 1))
                continue;
            files.Add((path, first, part));
        }
        files.Sort((a, b) => a.FirstSeq != b.FirstSeq ? a.FirstSeq.CompareTo(b.FirstSeq) : a.Part.CompareTo(b.Part));
        return files;
    }

    /// <summary>
    /// A file's lines, opened so the Director may go on writing it and pruning may delete it meanwhile. On
    /// Windows a reader that shares only reading refuses the writer, and the writer's minute append would
    /// then fail as though the disk had refused it.
    /// </summary>
    internal static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }
}
