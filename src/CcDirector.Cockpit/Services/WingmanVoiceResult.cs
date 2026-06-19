namespace CcDirector.Cockpit.Services;

/// <summary>
/// Result of a wingman voice-turn or direct-ask (issue #531). On success <see cref="Spoken"/>
/// carries the wingman's speakable translation (and <see cref="Reply"/> the agent's full reply
/// for the voice-turn shape); on a handled failure <see cref="Error"/> is set instead.
/// </summary>
public sealed class WingmanVoiceResult
{
    /// <summary>The agent's full written reply (voice-turn only; empty for a direct ask).</summary>
    public string? Reply { get; set; }

    /// <summary>The wingman's faithful, speakable translation - what the Voice tab shows and reads aloud.</summary>
    public string? Spoken { get; set; }

    /// <summary>Seconds the wingman brain took.</summary>
    public double ReplySeconds { get; set; }

    /// <summary>A human-readable error when the turn could not complete; null on success.</summary>
    public string? Error { get; set; }
}
