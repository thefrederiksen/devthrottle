namespace CcDirector.Gateway.Receipts;

/// <summary>
/// Where one API-mode session event stands in its session's stream: the Director's GENERATION (assigned each time a
/// Director starts holding the session's event stream - a Director restart is a new one; taking the wheel and handing
/// back inside one Director process is not), when that generation started, and the event's sequence inside it.
///
/// The order is the <see cref="History.SessionTurnStore"/> generation rule: a generation that started LATER is newer
/// (ties broken by the generation id, ordinally, so two Gateways given the same events decide the same way), and
/// inside one generation the higher sequence is newer. A sequence restarts with each generation, so a sequence is
/// never compared across two generations.
/// </summary>
public readonly record struct ApiModeEventPosition
{
    /// <summary>The longest generation id accepted (the column width).</summary>
    public const int MaxGenerationLength = 64;

    public ApiModeEventPosition(string generation, DateTime generationStartedUtc, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        if (generation.Length > MaxGenerationLength)
            throw new ArgumentException($"generation is longer than {MaxGenerationLength} characters", nameof(generation));
        if (generationStartedUtc == default)
            throw new ArgumentException("generationStartedUtc is not set", nameof(generationStartedUtc));
        if (sequence < 0)
            throw new ArgumentException($"sequence is negative ({sequence})", nameof(sequence));
        Generation = generation;
        GenerationStartedUtc = StartPrecision(generationStartedUtc);
        Sequence = sequence;
    }

    public string Generation { get; }

    /// <summary>When the generation started, in UTC at whole milliseconds - the precision it is both compared and stored
    /// at, so a value compared before a write and read back after it decide the same way.</summary>
    public DateTime GenerationStartedUtc { get; }

    public long Sequence { get; }

    /// <summary>Whether this position's generation started strictly later than <paramref name="generation"/> (ties
    /// broken by the generation id); false for the same generation.</summary>
    public bool IsLaterGenerationThan(string generation, DateTime generationStartedUtc)
    {
        var started = StartPrecision(generationStartedUtc);
        return GenerationStartedUtc > started
               || (GenerationStartedUtc == started && string.CompareOrdinal(Generation, generation) > 0);
    }

    /// <summary>Whether this event is newer than the last one applied at (<paramref name="generation"/>,
    /// <paramref name="generationStartedUtc"/>, <paramref name="sequence"/>). A duplicate, a lower sequence of the same
    /// generation, and anything of an older generation are not.</summary>
    public bool IsAfter(string generation, DateTime generationStartedUtc, long sequence)
        => string.Equals(Generation, generation, StringComparison.Ordinal)
            ? Sequence > sequence
            : IsLaterGenerationThan(generation, generationStartedUtc);

    public override string ToString() => $"{Generation}#{Sequence}";

    /// <summary>Whole milliseconds, UTC: Postgres keeps microseconds and .NET keeps hundred-nanosecond ticks, so one
    /// coarser precision on both sides removes any disagreement.</summary>
    internal static DateTime StartPrecision(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
