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
public sealed record ResourceRecordingRead(IReadOnlyList<RecordedLine> Lines, int UnreadableLines, int FileCount);

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
        var newestFirst = new List<RecordedLine>();
        var unreadable = 0;

        for (var i = files.Count - 1; i >= 0 && newestFirst.Count < maxLines; i--)
        {
            var lines = File.ReadAllLines(files[i].Path);
            for (var j = lines.Length - 1; j >= 0 && newestFirst.Count < maxLines; j--)
            {
                if (lines[j].Length == 0) continue;
                if (ResourceRecorder.TryReadPosition(lines[j], out var seq, out var recordingSeconds, out var takenAt))
                    newestFirst.Add(new RecordedLine(seq, recordingSeconds, takenAt, lines[j]));
                else
                    unreadable++;
            }
        }

        // Seq, not the file order and never the wall clock, is the order. Within a file it already is; this
        // makes it true across files whatever the folder listing did.
        var ordered = newestFirst.OrderBy(l => l.Seq).ToList();
        FileLog.Write($"[ResourceRecordingReader] ReadNewest: lines={ordered.Count}, unreadable={unreadable}, files={files.Count}");
        return new ResourceRecordingRead(ordered, unreadable, files.Count);
    }
}
