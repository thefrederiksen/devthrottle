namespace CcDirector.ControlApi;

/// <summary>Body of POST /voice/utterance. Optional client-supplied id (must be a GUID).</summary>
internal sealed record VoiceUtteranceRegisterRequest(string? UtteranceId);

/// <summary>
/// Body of POST /voice/utterance/{id}/complete. TotalChunks is how many chunks the
/// client uploaded (indices 0..TotalChunks-1). SessionId lets the server bias the
/// transcript cleanup to that session's repo dictionary.
/// </summary>
internal sealed record VoiceUtteranceCompleteRequest(int TotalChunks, string? Mime, string? SessionId);
