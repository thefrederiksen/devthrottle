using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Keeps a dictation clip together with the transcript it produced, so the transcriber can be measured
/// against real speech.
///
/// WHY THIS IS NOT THE SAFETY NET. <see cref="DictationRecordingStore"/> saves a clip so that a FAILED
/// transcription cannot lose the user's words, and deletes it the instant those words are safe. A file
/// sitting there means something went wrong, and the failure report names its path - that meaning is
/// worth keeping, so this store writes somewhere else entirely and never touches it. The safety net
/// catches a transcription that FAILS; this catches one that LIES, which looks like a success and
/// therefore leaves no evidence at all.
///
/// WHAT A PAIR IS. Two files sharing one stem in <see cref="CcStorage.DictationCorpus"/>:
///   dictation-20260909-182530-a1b2c3d4.wav    the exact bytes that were transcribed
///   dictation-20260909-182530-a1b2c3d4.json   what came back, and when
/// The json holds the RAW transcript and the dictionary-corrected one separately, so the pair proves
/// both halves of the verbatim rule: what the speech model wrote, and what the dictionary then changed.
///
/// OFF UNLESS ASKED. Keeping recorded speech is the user's decision. Nothing is written and no
/// directory is created until <c>dictation_corpus.enabled</c> is true in config.json. LOCAL ONLY -
/// never transmitted, exactly like the safety net beside it.
///
/// BOUNDED BY CONSTRUCTION. Every keep prunes pairs past the configured age and, whatever their age,
/// all but the newest configured count. Both bounds apply.
///
/// FAIL-OPEN. Keeping a clip is a diagnostic, never a gate: a full disk, a locked file or a bad config
/// value is logged and swallowed, so nothing here can cost a user their dictation.
/// </summary>
public static class DictationCorpusStore
{
    /// <summary>Where pairs are kept. Resolved per access so CC_DIRECTOR_ROOT redirects it.</summary>
    public static string DefaultDirectory => CcStorage.DictationCorpus();

    /// <summary>
    /// Keep one clip and its transcript, and return the full path of the saved WAV - or null when
    /// nothing was kept (the corpus is off, there is no audio, or writing failed).
    /// </summary>
    /// <param name="wav">The exact WAV bytes that were sent for transcription.</param>
    /// <param name="transcript">What came back: raw, corrected, and how many dictionary terms changed.</param>
    /// <param name="config">The corpus settings; null (production) reads config.json.</param>
    /// <param name="directory">Where to write; null (production) means <see cref="DefaultDirectory"/>.
    /// Tests point it at a scratch directory.</param>
    public static string? TryKeep(
        byte[] wav,
        DictationTranscript transcript,
        DictationCorpusConfig? config = null,
        string? directory = null)
    {
        try
        {
            var cfg = config ?? DictationCorpusConfig.Get();
            if (!cfg.Enabled) return null;

            if (wav is null || wav.Length == 0)
            {
                FileLog.Write("[DictationCorpusStore] TryKeep skipped: no audio bytes");
                return null;
            }
            if (transcript is null)
            {
                FileLog.Write("[DictationCorpusStore] TryKeep skipped: no transcript");
                return null;
            }

            var dir = directory ?? DefaultDirectory;
            Directory.CreateDirectory(dir);

            var stem = $"dictation-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var wavPath = Path.Combine(dir, stem + ".wav");
            var jsonPath = Path.Combine(dir, stem + ".json");

            File.WriteAllBytes(wavPath, wav);

            var record = new JsonObject
            {
                ["clip"] = stem + ".wav",
                ["recordedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["audioBytes"] = wav.Length,
                ["rawTranscript"] = transcript.RawTranscript,
                ["cleanedTranscript"] = transcript.CleanedTranscript,
                ["dictionaryWordsCorrected"] = transcript.DictionaryWordsCorrected,
            };
            File.WriteAllText(jsonPath, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            FileLog.Write($"[DictationCorpusStore] kept {wav.Length} bytes + transcript as {stem}");
            Prune(dir, cfg);
            return wavPath;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationCorpusStore] TryKeep FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Enforce both bounds: drop any pair older than the configured age, then drop the oldest until at
    /// most the configured count remain. A pair is deleted as a unit - a clip without its transcript is
    /// not evidence of anything, and a transcript without its clip cannot be checked against the audio.
    /// Never throws: a file that cannot be deleted is disk noise, not a functional problem.
    /// </summary>
    private static void Prune(string dir, DictationCorpusConfig cfg)
    {
        try
        {
            var clips = new DirectoryInfo(dir)
                .GetFiles("dictation-*.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow - cfg.MaxAge;
            var doomed = clips
                .Where((f, index) => index >= cfg.MaxClips || f.LastWriteTimeUtc < cutoff)
                .ToList();

            foreach (var clip in doomed)
            {
                TryDeletePair(clip);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationCorpusStore] prune FAILED: {ex.Message}");
        }
    }

    private static void TryDeletePair(FileInfo clip)
    {
        var json = Path.ChangeExtension(clip.FullName, ".json");
        try
        {
            clip.Delete();
            if (File.Exists(json)) File.Delete(json);
            FileLog.Write($"[DictationCorpusStore] pruned {Path.GetFileNameWithoutExtension(clip.Name)}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationCorpusStore] prune FAILED for {clip.Name}: {ex.Message}");
        }
    }
}
