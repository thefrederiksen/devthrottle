namespace CcDirector.Core.Dictation.Models;

/// <summary>
/// Outcome of a complete dictation cycle from audio to cleaned text.
/// Each layer's output is preserved for inspection and debugging.
/// </summary>
public sealed record TranscriptResult(
    string RawTranscript,
    string CleanedTranscript,
    string ProfileUsed,
    bool CleanupApplied,
    string? CleanupFailureReason);

/// <summary>
/// A streaming partial transcript surfaced while the speaker is still talking.
/// IsFinal=true marks the last partial before the cleanup pass runs.
/// </summary>
public sealed record PartialTranscript(string Text, bool IsFinal);
