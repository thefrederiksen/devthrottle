namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions/{sid}/prompt on both the Director Control API and the Gateway.
/// </summary>
public sealed class PromptRequest
{
    /// <summary>The text to send to the session.</summary>
    public string Text { get; set; } = "";

    /// <summary>If true (default), append Enter after the text so the session executes the prompt.</summary>
    public bool AppendEnter { get; set; } = true;

    /// <summary>
    /// If true, the Gateway holds the response open until the session returns to Idle (or timeout).
    /// Ignored by the Director's own Control API (which always returns immediately after queueing).
    /// </summary>
    public bool WaitForIdle { get; set; } = false;

    /// <summary>Wait timeout in milliseconds. Default 120000 (2 min). Only used when WaitForIdle=true.</summary>
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>
/// Response from POST /sessions/{sid}/prompt.
/// </summary>
public sealed class PromptResponse
{
    /// <summary>True if the prompt was accepted and dispatched to the session.</summary>
    public bool Accepted { get; set; }

    /// <summary>UTC timestamp the prompt was accepted by the Director.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>Buffer position (TotalBufferBytes) immediately before the prompt was sent. Use as ?since= cursor.</summary>
    public long BufferCursor { get; set; }

    /// <summary>Activity state after dispatch. Working if accepted; whatever the session was if rejected.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Only set if WaitForIdle=true: cleaned output produced by the session for this prompt.</summary>
    public string? Output { get; set; }

    /// <summary>Only set if WaitForIdle=true: idle | timeout | failed.</summary>
    public string? WaitStatus { get; set; }

    /// <summary>Error message if Accepted == false.</summary>
    public string? Error { get; set; }
}
