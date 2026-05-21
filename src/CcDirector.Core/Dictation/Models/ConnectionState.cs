namespace CcDirector.Core.Dictation.Models;

/// <summary>
/// State of the dictation session's connection to its speech provider.
/// Consumers wire this to UI ("recording", "buffering", "reconnecting") so
/// the user understands what is happening when the network is misbehaving.
///
/// The batch <c>OpenAiTranscriptionProvider</c> only ever transitions
/// Idle to Connected to Idle, because push calls never touch the network.
/// The streaming <c>OpenAiRealtimeProvider</c> arriving in a later phase
/// will exercise the full set.
/// </summary>
public enum ConnectionState
{
    /// <summary>No session in progress.</summary>
    Idle,
    /// <summary>Session active and chunks are flowing to the provider.</summary>
    Connected,
    /// <summary>Provider failed mid-stream; audio is being captured into the local buffer.</summary>
    Buffering,
    /// <summary>An attempt to re-establish the provider is in flight.</summary>
    Reconnecting,
    /// <summary>The session terminated in a failed state.</summary>
    Failed,
}
