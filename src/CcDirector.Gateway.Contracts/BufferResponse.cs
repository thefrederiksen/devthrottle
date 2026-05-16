namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /sessions/{sid}/buffer response.
/// </summary>
public sealed class BufferResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>Total bytes the buffer has ever held. Same as SessionDto.TotalBufferBytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>The cursor to pass back as ?since= on the next call.</summary>
    public long NewCursor { get; set; }

    /// <summary>Cleaned text (ANSI removed) unless ?raw=true.</summary>
    public string Text { get; set; } = "";
}
