using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The fire-and-forget desktop dictation send. The Speak dialog handed us the still-capturing recorder
/// the instant Send was pressed and closed itself, releasing the screen; this runs the rest OFF the UI
/// thread while the session shows orange "Transcribing...".
///
/// The behaviour is fail-loud, NO queue and NO readiness pre-check: the clip is transcribed once and
/// submitted straight into the target session through the echo-verified terminal submit. That submit is
/// the only arbiter of whether the session took the text - it types the words and confirms the composer
/// echoed them, throwing when the composer truly refuses (a modal, a picker, still starting up). The old
/// ActivityState pre-check was removed deliberately: it lags real terminal silence by 10 seconds, so it
/// rejected dictations into sessions that were sitting idle at their prompt, and agents accept typed
/// input while working anyway. On ANY failure <paramref name="onFailed"/> fires with the composed text
/// (when one exists) so the caller can put the words back in the compose box - never silent, never lost.
///
/// The recording itself has a DISK SAFETY NET (issue #1130): the WAV is saved to
/// <see cref="DictationRecordingStore"/> before the single transcription attempt and deleted the moment
/// the words are safe (delivered, or restored as text). When transcription fails there is no text to
/// restore - the saved file is then the ONLY copy of what was said, so it is kept and the failure
/// report names its path. This is not a queue and nothing re-drives the file; it just makes "the
/// transcription service was down" a nuisance instead of lost speech.
/// </summary>
public static class BackgroundDictationSend
{
    /// <param name="recorder">The detached, still-capturing recorder. Owned and disposed here.</param>
    /// <param name="prefix">Already-transcribed text from earlier Pause/Resume segments, joined ahead of
    /// this segment's transcript; may be empty.</param>
    /// <param name="target">The session the message is being dictated into (marked orange meanwhile).</param>
    /// <param name="transcriber">Transcribes the recorded clip.</param>
    /// <param name="submit">Submits the fully-composed turn text into the session (the dictation has
    /// already been inserted at the caret inside the typed text) with the origin the turn is stamped
    /// with: DesktopVoice when the transcript is the whole message, DesktopTyped when typed text or an
    /// earlier segment was composed around it (ruling R20, the one rule in <see cref="SpokenTurnRule"/>).</param>
    /// <param name="before">Text the user had typed before the caret when Send was pressed.</param>
    /// <param name="after">Text the user had typed after the caret when Send was pressed.</param>
    /// <param name="onFailed">Called on the UI thread when the dictation could not be delivered. The
    /// first argument is the error; the second is the fully-composed turn text when transcription had
    /// already succeeded (so the caller can restore the words), or null when the failure happened
    /// before a transcript existed. Never silent.</param>
    /// <param name="recordingsDirectory">Where the disk safety net saves the WAV. Null (production)
    /// means <see cref="DictationRecordingStore.DefaultDirectory"/>; tests point it at a scratch
    /// directory.</param>
    /// <param name="corpusConfig">Dictation-corpus settings. Null (production) reads config.json, where
    /// the corpus is OFF unless the user turned it on.</param>
    /// <param name="corpusDirectory">Where kept clip/transcript pairs are written. Null (production)
    /// means <see cref="DictationCorpusStore.DefaultDirectory"/>; tests point it at a scratch
    /// directory.</param>
    public static async Task RunAsync(
        BatchDictationRecorder recorder,
        string prefix,
        Session target,
        IDictationTranscriber transcriber,
        Func<string, InputOrigin, SubmissionProvenance, Task> submit,
        string before = "",
        string after = "",
        Action<string, string?>? onFailed = null,
        string? recordingsDirectory = null,
        DictationCorpusConfig? corpusConfig = null,
        string? corpusDirectory = null)
    {
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}, state={target.ActivityState}");
        target.IsTranscribing = true;
        string? savedPath = null;
        try
        {
            // 1. Stop the mic and get the whole clip as a WAV. An interrupted turn with no audio throws
            //    the completeness gate - there is nothing to send, and nothing was said, so stay silent.
            CapturedAudio captured;
            try
            {
                captured = await recorder.StopAndGetWavAsync();
            }
            catch (NoAudioCapturedException ex)
            {
                FileLog.Write($"[BackgroundDictationSend] no audio captured for session {target.Id}: {ex.Message}");
                return;
            }

            // 1b. Disk safety net (issue #1130): persist the WAV BEFORE the single transcription
            //     attempt, so a failed or slow transcription can never lose the recording. Saving is
            //     best-effort - a full disk must not block the send - and the file is deleted below
            //     the moment the words are safe in some other form.
            savedPath = DictationRecordingStore.TrySave(captured.Wav, recordingsDirectory);

            // 2. Transcribe once. On failure there is no transcript to restore - report loudly, do not
            //    queue or retry. The saved WAV is now the only copy of the spoken words, so it is KEPT
            //    and the report names it.
            DictationTranscript transcribed;
            string transcript;
            try
            {
                transcribed = await transcriber.TranscribeAsync(captured.Wav);
                transcript = transcribed.CleanedTranscript;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDictationSend] transcription FAILED for session {target.Id} ({DiagnosticState(target)}): {ex.Message}; savedRecording={savedPath ?? "none"}");
                onFailed?.Invoke(WithSavedRecording(ex.Message, savedPath), null);
                savedPath = null; // kept for the user; do not delete below
                return;
            }

            // 2b. Dictation corpus (OFF unless the user turned it on): keep this clip together with the
            //     transcript it produced. A transcript carrying a word nobody said looks like a success,
            //     so the safety net below deletes the only evidence of it seconds later. This writes the
            //     pair somewhere else, leaves the safety net's meaning alone, and is fail-open - it can
            //     never cost the user their dictation. See DictationCorpusStore.
            DictationCorpusStore.TryKeep(captured.Wav, transcribed, corpusConfig, corpusDirectory);

            // 3. Compose the turn (dictation dropped at the caret inside any typed text) and submit it
            //    straight through the echo-verified terminal submit. From here on the composed text is
            //    carried into every failure report so the words can be put back in the compose box -
            //    the words survive as text now, so the WAV safety net is no longer needed either way.
            // The composition, and WHICH OF ITS CHARACTERS WERE SPOKEN (source logging, 2026-09-05): the earlier
            // dictated segments and this clip's transcript are each a spoken span, placed where InsertAt and Join
            // put them, then the whole is projected to the wire (newlines to spaces, trimmed) with the spans
            // moved along - the same projection the compose box's Send uses.
            var dictation = DictationText.Join(prefix, transcript);
            var composed = DictationText.InsertAt(before + after, before.Length, dictation);
            var dictationAt = composed.IndexOf(dictation, before.Length, StringComparison.Ordinal);
            var record = new SpokenTurnRule.ComposerProvenance();
            var spans = new List<SpokenTurnRule.SpokenSpan>();
            if (!string.IsNullOrWhiteSpace(prefix))
                spans.Add(new SpokenTurnRule.SpokenSpan(dictationAt, prefix.Length));
            if (!string.IsNullOrWhiteSpace(transcript))
                spans.Add(new SpokenTurnRule.SpokenSpan(dictationAt + dictation.LastIndexOf(transcript, StringComparison.Ordinal), transcript.Length));
            record.Restore(composed, spans);
            var (text, sentSpans) = record.ForSend();
            var provenance = new SubmissionProvenance(SubmissionRoutes.DesktopDictation, SubmissionIdentityKinds.LocalUser, null, sentSpans);
            // WHAT THE TURN IS, decided by the one rule both surfaces use (ruling R20): the transcript alone
            // is spoken; typed text before or after it, or an earlier segment ahead of it, makes the whole
            // message one typed turn - exactly as the phone's durable dictation classifies the same mixture.
            // This used to stamp the whole composition DesktopVoice.
            var origin = SpokenTurnRule.IsSpokenAlone(before, prefix, after) ? InputOrigin.DesktopVoice : InputOrigin.DesktopTyped;
            if (origin.Modality == InputModality.Typed)
                FileLog.Write($"[BackgroundDictationSend] session {target.Id}: typed text or an earlier segment composed around the dictation; delivered as ONE TYPED turn (ruling R20)");
            try
            {
                await submit(text, origin, provenance);
                FileLog.Write($"[BackgroundDictationSend] delivered to session {target.Id}, chars={text.Length}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDictationSend] submit FAILED for session {target.Id} ({DiagnosticState(target)}): {ex.Message}");
                if (onFailed is null)
                {
                    // No failure callback means nobody restored the words as text - the saved WAV is
                    // then the only copy of what was said, so it is kept and the log names it. The
                    // single production caller always passes onFailed; this guards a future caller.
                    FileLog.Write($"[BackgroundDictationSend] no onFailed callback; keeping savedRecording={savedPath ?? "none"}");
                    savedPath = null; // kept; do not delete below
                    return;
                }
                onFailed.Invoke(ex.Message, text);
            }
            DictationRecordingStore.TryDelete(savedPath);
            savedPath = null;
        }
        catch (Exception ex)
        {
            // Root of a detached task: never let it fault unobserved. No transcript text reached the
            // caller, so a saved recording (if any) is kept and named - it may be the only copy.
            FileLog.Write($"[BackgroundDictationSend] unexpected error for session {target.Id} ({DiagnosticState(target)}): {ex.Message}; savedRecording={savedPath ?? "none"}");
            onFailed?.Invoke(WithSavedRecording(ex.Message, savedPath), null);
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Append the saved-recording pointer to a failure report, so the user learns WHERE the audio is
    /// in the same modal that tells them the dictation failed. No file, no extra noise.
    /// </summary>
    private static string WithSavedRecording(string error, string? savedPath)
        => savedPath is null
            ? error
            : $"{error}\n\nYour recording was saved - you can play it back or dictate again from it:\n{savedPath}";

    /// <summary>
    /// One-line diagnostic snapshot for failure logs: the session's activity state and how long its
    /// terminal has been byte-silent. Both incidents that killed the old readiness gate (issue #1308)
    /// took cross-referencing detector log lines by hand to reconstruct; this puts the answer on the
    /// failure line itself.
    /// </summary>
    private static string DiagnosticState(Session target)
    {
        var idle = target.Buffer is { } buffer
            ? $"{(DateTime.UtcNow - buffer.LastWriteAtUtc).TotalSeconds:F1}s"
            : "n/a";
        return $"state={target.ActivityState}, terminalSilentFor={idle}";
    }
}
