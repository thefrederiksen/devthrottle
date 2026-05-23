namespace CcDirector.Core.Recording;

/// <summary>
/// What gets filed into the vault for one finished recording: a markdown
/// transcript document plus the original audio, both already written to the
/// transcripts collection folder on disk.
/// </summary>
public sealed record VaultFilingRequest(
    string Title,
    string TranscriptMarkdownPath,
    IReadOnlyList<string> AudioFilePaths,
    string RecordedDateUtc);

/// <summary>
/// Files a finished transcript into the vault. Abstracted so the ingest
/// service can be unit-tested without invoking the cc-vault CLI.
/// </summary>
public interface IVaultFiler
{
    /// <summary>
    /// File the transcript and return an identifier for the created vault
    /// entry (the cc-vault output, or a synthetic id). Throws on failure -
    /// callers surface the error rather than silently dropping the transcript.
    /// </summary>
    Task<string> FileTranscriptAsync(VaultFilingRequest request, CancellationToken ct = default);
}
