using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>One whole line of the recording, with the three fields it is ordered and found by.</summary>
/// <param name="Seq">Its place in the recording.</param>
/// <param name="RecordingSeconds">Its place on the recording's own timeline. See <see cref="ResourceRecorder"/>.</param>
/// <param name="TakenAtUtc">What the wall clock said when it was taken. For printing; never for ordering.</param>
/// <param name="Json">The line itself, in <see cref="ResourceRecordingFormat"/>.</param>
public sealed record RecordedLine(long Seq, double RecordingSeconds, DateTime TakenAtUtc, string Json);

/// <summary>What a read of the recording found.</summary>
/// <param name="Lines">Whole lines, oldest first by sequence number.</param>
/// <param name="UnreadableLines">
/// Lines that were not whole - torn by a crash or a refused write. COUNTED, NEVER HIDDEN: a reader that
/// quietly skipped them would present a recording with a hole in it as a complete one.
/// </param>
/// <param name="FileCount">How many files the recording is held in.</param>
/// <param name="RepeatedLines">
/// Whole lines whose <c>seq</c> was already read - the samples a retry wrote again after a write failed
/// part way. Counted and left out, so no sample is counted twice.
/// </param>
public sealed record ResourceRecordingRead(IReadOnlyList<RecordedLine> Lines, int UnreadableLines, int FileCount, int RepeatedLines);

/// <summary>
/// Reads the 24-hour local recording. The diagnose command reads the same files the same way, in Python;
/// this is the Director's side, for its own History tab and for the tests that prove the recording.
/// </summary>
public static class ResourceRecordingReader
{
    /// <summary>
    /// The newest <see cref="ResourceRecorder.RetainedSamples"/> whole lines - the 24 hours the recording
    /// promises, and never the up-to-an-hour more the files can briefly hold before a file is pruned.
    /// Reads the disk; call it off the user-interface thread.
    /// </summary>
    public static ResourceRecordingRead ReadRetained(string directory) => ReadNewest(directory, ResourceRecorder.RetainedSamples);

    /// <summary>The newest <paramref name="maxLines"/> whole lines, oldest first. Reads the disk.</summary>
    public static ResourceRecordingRead ReadNewest(string directory, int maxLines)
    {
        FileLog.Write($"[ResourceRecordingReader] ReadNewest: directory={directory}, maxLines={maxLines}");
        if (maxLines <= 0) throw new ArgumentOutOfRangeException(nameof(maxLines), "Ask for at least one line.");

        var files = ResourceRecorder.RecordingFiles(directory);

        // Gather the newest files until they hold enough lines, then walk them OLDEST FIRST so the copy of a
        // repeated seq that is kept is the one written first - the original, not a retry's second copy.
        var needed = new List<string[]>();
        var gathered = 0;
        // One file more than the count needs, because repeated lines count toward it and are then dropped.
        for (var i = files.Count - 1; i >= 0 && gathered < maxLines + ResourceRecorder.SamplesPerFile; i--)
        {
            var lines = ResourceRecorder.ReadLinesShared(files[i].Path);
            needed.Add(lines);
            gathered += lines.Length;
        }
        needed.Reverse();

        var bySeq = new Dictionary<long, RecordedLine>();
        var unreadable = 0;
        var repeated = 0;
        foreach (var lines in needed)
        {
            foreach (var text in lines)
            {
                if (text.Length == 0) continue;
                if (!ResourceRecorder.TryReadPosition(text, out var seq, out var recordingSeconds, out var takenAt))
                {
                    unreadable++;
                    continue;
                }
                if (!bySeq.TryAdd(seq, new RecordedLine(seq, recordingSeconds, takenAt, text))) repeated++;
            }
        }

        // Seq, not the file order and never the wall clock, is the order.
        var ordered = bySeq.Values.OrderBy(l => l.Seq).ToList();
        if (ordered.Count > maxLines) ordered = ordered.GetRange(ordered.Count - maxLines, maxLines);
        FileLog.Write($"[ResourceRecordingReader] ReadNewest: lines={ordered.Count}, unreadable={unreadable}, " +
                      $"repeated={repeated}, files={files.Count}");
        return new ResourceRecordingRead(ordered, unreadable, files.Count, repeated);
    }
}
