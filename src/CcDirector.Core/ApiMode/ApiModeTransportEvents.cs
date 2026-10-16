using System.Text.Json;

namespace CcDirector.Core.ApiMode;

/// <summary>Something the transport hands its one consumer, in the order it happened.</summary>
public abstract record ApiModeTransportEvent;

/// <summary>
/// One line of the agent's standard output: a newline-delimited JSON record. Delivered for EVERY line,
/// including a response that also completed a waiting request, so a dialect sees the whole wire in order.
/// </summary>
public sealed record ApiModeRecord : ApiModeTransportEvent
{
    /// <summary>Rising from 1, in wire order.</summary>
    public required long Sequence { get; init; }

    /// <summary>The line exactly as the agent wrote it, decoded as UTF-8, without its line ending.</summary>
    public required string RawLine { get; init; }

    /// <summary>The parsed record, or null when the line is not JSON (then <see cref="ParseError"/> says why).</summary>
    public JsonElement? Json { get; init; }

    /// <summary>Why the line could not be parsed, or null when it was.</summary>
    public string? ParseError { get; init; }

    /// <summary>The record's id as text (a string id as is, a number as written), or null when it has none.</summary>
    public string? Id { get; init; }

    /// <summary>True when this record completed a request that was waiting for it.</summary>
    public bool AnsweredRequest { get; init; }
}

/// <summary>One line of the agent's standard error, numbered so a caller can ask for the lines since a marker.</summary>
public sealed record ApiModeErrorLine(long Sequence, string Text);

/// <summary>
/// The agent process is gone. Always the last event the transport delivers, delivered exactly once.
/// </summary>
public sealed record ApiModeProcessEnded : ApiModeTransportEvent
{
    /// <summary>
    /// The exit code exactly as the operating system reported it, or null only when it could not be read (then
    /// <see cref="TransportFault"/> says why). A kill reports 137 on Linux and macOS (128 plus the signal) and -1 on
    /// Windows (.NET terminates the process with exit code -1), so -1 is an ordinary value, never "unknown". Whether
    /// the transport killed the process is <see cref="KilledByTransport"/>, not a reading of this number.
    /// </summary>
    public required int? ExitCode { get; init; }

    /// <summary>True when the transport killed the process (shutdown grace ran out, disposal, or a transport failure).</summary>
    public bool KilledByTransport { get; init; }

    /// <summary>Every standard error line still held (see <see cref="ApiModeTransport.ErrorLinesSince"/> for the window).</summary>
    public required IReadOnlyList<ApiModeErrorLine> ErrorLines { get; init; }

    /// <summary>Bytes after the last line feed when standard output ended, decoded as UTF-8: a record cut off mid-line. Null when output ended cleanly.</summary>
    public string? UnterminatedOutput { get; init; }

    /// <summary>A failure of the transport itself (a line over the size limit, a broken read), or null when there was none.</summary>
    public string? TransportFault { get; init; }

    /// <summary>One sentence for logs and failure reasons.</summary>
    public string Describe() =>
        (ExitCode is { } code ? $"exit code {code}" : "exit code unknown")
        + (KilledByTransport ? ", killed by the transport" : "")
        + (TransportFault is null ? "" : $"; transport failure: {TransportFault}")
        + (UnterminatedOutput is null ? "" : $"; output ended mid-record ({UnterminatedOutput.Length} characters)");
}

/// <summary>The standard error lines written since a marker.</summary>
/// <param name="Lines">The lines, oldest first.</param>
/// <param name="LinesNoLongerHeld">How many lines after the marker were already discarded because the transport holds only the most recent ones. Zero in the ordinary case.</param>
public sealed record ApiModeErrorLinesSince(IReadOnlyList<ApiModeErrorLine> Lines, long LinesNoLongerHeld);
