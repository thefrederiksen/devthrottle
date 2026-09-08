namespace CcDirector.Gateway.Mentor;

/// <summary>
/// One prompt-log record as the mentor's readers hold it: the port of the dict <c>packet.load_records</c>
/// builds (and <c>metrics.load_prompt_log</c>, the same keys less the name). <see cref="Ts"/> is UTC.
/// <see cref="Pid"/> is the record's place, <c>conversation-yyyyMMdd.jsonl:line</c>, the id the reference
/// sorts ties by and names a record by. The origin fields are written by <see cref="Mentor.Origin.Classify"/>.
/// </summary>
public sealed class MentorRecord
{
    public required DateTime Ts { get; init; }
    public required string Session { get; init; }
    public string? Context { get; init; }
    public required string Role { get; init; }
    public string? Modality { get; init; }
    public string? Surface { get; init; }
    public required int Words { get; set; }
    public required string Text { get; set; }
    public string? Agent { get; init; }
    public string? Name { get; init; }
    public required string Pid { get; init; }

    public string? Origin { get; set; }
    public string? OriginRule { get; set; }
    public string? OriginModality { get; set; }
    public string? OriginSurface { get; set; }
    public string? OriginStrip { get; set; }
    public int OriginStripChars { get; set; }
    public int OriginStripWords { get; set; }

    /// <summary>The ASCII transliteration of <see cref="Text"/>, cached by the surface (<c>_ascii</c>).</summary>
    public string? AsciiText { get; set; }
}

/// <summary>
/// One activity event as <c>metrics.load_events</c> keeps it: the state transitions and the turn-submitted
/// events, with the send source and input origin the ledger joins on. <see cref="Ts"/> is UTC.
/// </summary>
public sealed class MentorEvent
{
    public required DateTime Ts { get; init; }
    public required long Seq { get; init; }
    public required string Session { get; init; }
    public required string Type { get; init; }
    public string? Prev { get; init; }
    public string? New { get; init; }
    public string? SendSource { get; init; }
    public string? InputOrigin { get; init; }
    public required string Where { get; init; }
}
