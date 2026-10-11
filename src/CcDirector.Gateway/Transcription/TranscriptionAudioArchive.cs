using System.Linq;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Rolling on-disk archive of the audio behind every transcription the Gateway performs.
///
/// WHY THIS EXISTS. The desktop dictation safety net (<c>DictationRecordingStore</c>) saves the clip
/// before transcribing and DELETES it the moment the words are delivered. That net catches a
/// transcription that FAILS. It does not catch one that LIES: a truncated transcript is a "success", so
/// the audio that would prove it is deleted seconds later. When a turn came back with a fraction of
/// what was said, the recording was already gone and the loss could not be localized - the byte count
/// matching the wall clock proves no samples were DROPPED, but it can never prove the bytes carried
/// speech. Only the audio can settle that. So the audio is now KEPT.
///
/// WHY HERE. This sits beside <see cref="TranscriptionHistoryLog"/> on purpose. That history is the one
/// place that sees EVERY turn from every surface (desktop Send, the Speak dialog, the phone, Car Mode),
/// because they all transcribe through <see cref="GatewayTranscriptionService"/>. The two desktop
/// dictation paths do not agree on this: only the fire-and-forget Send saves a clip at all. Archiving at
/// the choke point covers every surface once instead of per-caller.
///
/// The file is named for the same <c>turnId</c> the local history records, so a suspicious line in
/// transcription-history/transcription-YYYYMMDD.jsonl leads straight to the audio that produced it.
///
/// BOUNDED BY CONSTRUCTION. This is a diagnostic window, not an archive that grows forever: every save
/// prunes clips older than <see cref="MaxAge"/> and, whatever their age, all but the newest
/// <see cref="MaxClips"/>. Both bounds apply, so neither a quiet week nor a busy hour can run the disk up.
///
/// Privacy: LOCAL disk only, exactly like the minimized history it sits next to. Never transmitted.
///
/// Fail-safe: archiving must never break a transcription. Every operation swallows and logs its errors,
/// the same fail-open contract as the local history.
/// </summary>
public sealed class TranscriptionAudioArchive
{
    /// <summary>How long a clip is kept. A problem reported "yesterday" must still have its audio.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Hard ceiling on clip count regardless of age, so a heavy dictation day cannot fill the disk
    /// before <see cref="MaxAge"/> retires anything. At a typical turn size this is a few hundred
    /// megabytes at the very worst.
    /// </summary>
    public const int MaxClips = 500;

    private readonly object _gate = new();

    /// <summary>
    /// An explicit directory override, or null to use <see cref="DefaultDirectory"/>. Only the OVERRIDE
    /// is stored; the default is resolved PER ACCESS by <see cref="ArchiveDirectory"/> and never captured
    /// here, so CC_DIRECTOR_ROOT set after this instance is built still redirects where clips land. An
    /// instance that captured the default in its constructor would bake the path at construction time and
    /// defeat a test that sets CC_DIRECTOR_ROOT afterwards - which is how isolated tests once wrote clips
    /// into the real user's archive.
    /// </summary>
    private readonly string? _directoryOverride;

    // The owning host's deployment signal, fixed at construction (see GatewayHostOptions). On hosted nothing is written.
    private readonly bool _hosted;

    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>). REQUIRED.</param>
    /// <param name="directory">Override the archive directory (tests). Defaults to the per-user location.</param>
    public TranscriptionAudioArchive(bool hosted, string? directory = null)
    {
        _hosted = hosted;
        _directoryOverride = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>
    /// Where clips are kept, resolved per access so CC_DIRECTOR_ROOT redirects it. NOT a get-only
    /// initializer and NOT captured in the constructor - see <see cref="_directoryOverride"/>.
    /// </summary>
    private string ArchiveDirectory => _directoryOverride ?? DefaultDirectory();

    /// <summary>The per-user transcription-audio directory. Resolved per call, never cached.</summary>
    public static string DefaultDirectory() => CcStorage.TranscriptionAudio();

    /// <summary>This tenant's troubleshooting-audio directory (issue #2059). Local keeps the existing flat
    /// directory (self-host unchanged); every other tenant gets its own subdirectory, so one tenant clearing
    /// its history never touches another tenant's archived audio.</summary>
    public static string DirectoryFor(Core.Tenancy.TenantId tenant)
    {
        if (tenant == Core.Tenancy.TenantId.Local) return DefaultDirectory();
        var chars = tenant.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return System.IO.Path.Combine(DefaultDirectory(), new string(chars));
    }

    /// <summary>The file a clip for <paramref name="turnId"/> lands in.</summary>
    public string FileFor(string turnId, string extension)
        => Path.Combine(ArchiveDirectory, $"turn-{SafeName(turnId)}{extension}");

    /// <summary>
    /// Keep a turn id to characters a filename allows. Production ids are GUIDs and pass through
    /// untouched; this only stops a hand-supplied id from escaping the archive directory.
    /// </summary>
    private static string SafeName(string name)
        => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Archive the exact bytes sent for transcription and return the saved path, or null when nothing
    /// was saved. Never throws: a full disk must degrade the diagnostics, never the transcription.
    /// </summary>
    /// <param name="turnId">The local-history turn id; ties the clip to its transcription-history line.</param>
    /// <param name="audio">The clip bytes, exactly as sent to the provider.</param>
    /// <param name="contentType">The clip's MIME type, used to pick a playable file extension.</param>
    public string? TrySave(string turnId, byte[] audio, string contentType)
    {
        // HOSTED WRITE GATE (MTR-10 Gap A). This archive has ONE process/user directory with no tenant in
        // its path or API and a GLOBAL age/count prune, so on a multi-tenant hosted Gateway it would mix
        // every account's raw speech at rest and let one tenant's traffic prune another's clips. It is a
        // LOCAL self-host diagnostic aid - write-only, no read method, no public archive-read route - so
        // there is nothing on hosted to serve it. This is the exact reasoning, and the exact fix, applied
        // to the local history that sits beside it (GatewayTranscriptionService.RecordHistory, issue
        // #1897): stop the write on hosted. Self-host is single-tenant and byte-identical to today.
        if (_hosted)
        {
            FileLog.Write($"[TranscriptionAudioArchive] TrySave SKIPPED on hosted for turn {turnId}: the archive has no tenant partition and no reader on hosted (MTR-10 Gap A; mirrors the local-history guard)");
            return null;
        }

        if (string.IsNullOrWhiteSpace(turnId)) return null;
        if (audio is null || audio.Length == 0) return null;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(ArchiveDirectory);
                var path = FileFor(turnId, ExtensionFor(contentType));
                File.WriteAllBytes(path, audio);
                FileLog.Write($"[TranscriptionAudioArchive] archived {audio.Length} bytes for turn {turnId} to {path}");
                Prune();
                return path;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionAudioArchive] TrySave FAILED for turn {turnId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Deletes every locally retained troubleshooting clip and returns the number removed. This is the
    /// audio half of the owner's Transcription Health clear action. Individual deletion failures are
    /// logged and skipped so a locked file cannot prevent the remaining clips from being removed.
    /// </summary>
    public int Clear()
    {
        try
        {
            lock (_gate)
            {
                if (!Directory.Exists(ArchiveDirectory))
                    return 0;

                var removed = 0;
                foreach (var path in Directory.EnumerateFiles(ArchiveDirectory, "turn-*"))
                {
                    try
                    {
                        File.Delete(path);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[TranscriptionAudioArchive] clear FAILED for {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
                return removed;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionAudioArchive] clear FAILED: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Enforce both bounds: drop anything past <see cref="MaxAge"/>, then drop the oldest until at most
    /// <see cref="MaxClips"/> remain. Caller holds the lock. Never throws - a clip that cannot be
    /// deleted is disk noise, not a functional problem.
    /// </summary>
    private void Prune()
    {
        var files = new DirectoryInfo(ArchiveDirectory)
            .GetFiles("turn-*")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var cutoff = DateTime.UtcNow - MaxAge;
        var doomed = files
            .Where((f, index) => index >= MaxClips || f.LastWriteTimeUtc < cutoff)
            .ToList();

        foreach (var file in doomed)
        {
            try
            {
                file.Delete();
                FileLog.Write($"[TranscriptionAudioArchive] pruned {file.Name}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TranscriptionAudioArchive] prune FAILED for {file.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A playable extension for the clip's MIME type. The archive exists to be LISTENED TO, so the file
    /// must open in a player on a double-click.
    ///
    /// Delegates to <see cref="GatewayTranscriptionService.ExtensionFor"/> - the same mapping that names
    /// the upload sent to the provider, so the archived clip and the transcribed clip can never disagree
    /// about what format they are. A private copy here did disagree: it exact-matched the MIME string, so
    /// the "audio/webm;codecs=opus" the browser and phone actually send missed every arm and real clips
    /// landed as unplayable .bin. The shared one strips the parameter and was already tested for exactly
    /// that case.
    /// </summary>
    private static string ExtensionFor(string contentType)
        => "." + GatewayTranscriptionService.ExtensionFor(contentType);
}
