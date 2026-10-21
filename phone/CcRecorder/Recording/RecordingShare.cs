namespace CcRecorder.Recording;

/// <summary>
/// Plans the files handed to the system share sheet for "Share audio": every segment of a
/// recording, in recording order, under a name a person recognises ("Car pitch - part 2 of 3.m4a")
/// instead of "0001.m4a". The Gateway deletes its copy of the audio once a recording is
/// transcribed, so the phone's copy is the only one left; this is how it reaches OneDrive,
/// email or anywhere else without a cable.
///
/// Kept dependency-free (no MAUI/Android) so the ordering and naming are unit-tested off-device.
/// A segment listed in the manifest but missing on disk is an error, never skipped: sharing a
/// recording with a hole in it would look complete and silently lose minutes.
/// </summary>
public static class RecordingShare
{
    public sealed record ShareFile(string SourcePath, string ShareName);

    public static IReadOnlyList<ShareFile> Plan(LocalManifest manifest, string recordingFolder, Func<string, bool> fileExists)
    {
        var chunks = manifest.Chunks.OrderBy(c => c.Index).ToList();
        if (chunks.Count == 0)
            throw new InvalidOperationException("This recording has no audio on the phone to share.");

        var baseName = SafeName(manifest.Title, manifest.RecordingId);
        var files = new List<ShareFile>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var source = Path.Combine(recordingFolder, chunks[i].File);
            if (!fileExists(source))
                throw new InvalidOperationException(
                    $"Audio segment {i + 1} of {chunks.Count} ({chunks[i].File}) is missing from the phone, so the recording cannot be shared whole.");
            var part = chunks.Count == 1 ? "" : $" - part {i + 1} of {chunks.Count}";
            files.Add(new ShareFile(source, baseName + part + Path.GetExtension(chunks[i].File)));
        }
        return files;
    }

    /// <summary>Longest title kept in a shared file name; leaves room for " - part 10 of 12.m4a".</summary>
    public const int MaxNameLength = 80;

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The recording's title made safe as a file name on the phone AND on the Windows machine it is
    /// shared to: characters invalid on either side and control characters become '-', a Windows
    /// device name ("con", "nul") is refused, and the length is capped. Its id when nothing usable is left.
    /// </summary>
    public static string SafeName(string title, string recordingId)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var cleaned = new string((title ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength];
        cleaned = cleaned.Trim(' ', '.', '-');
        if (cleaned.Length == 0 || WindowsReserved.Contains(cleaned)) return recordingId;
        return cleaned;
    }
}
