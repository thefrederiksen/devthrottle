using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>What one check report came to under the check contract: a count, or the reason it has none.</summary>
/// <param name="Count">The integer the check printed, or null when the check failed.</param>
/// <param name="FailureReason">Why the check failed, in words, or null when it kept the contract.</param>
/// <param name="Context">What a definition check said about the work, for the prompt's <c>{context}</c>; null when it
/// said nothing, and always null on a command trigger.</param>
public readonly record struct TriggerCheckReading(int? Count, string? FailureReason, string? Context = null)
{
    public bool Failed => FailureReason is not null;
}

/// <summary>
/// THE CHECK CONTRACT, read in one place (the Website Business Factory mission, product track): a check is any
/// command that exits 0 and prints JSON with an integer <c>count</c>. The product knows nothing about any
/// particular business tool - this is all it asks of one.
///
/// Anything else is a FAILED check, with the reason in words: it could not start, it timed out, it exited
/// non-zero, what it printed is not JSON, or the JSON has no integer <c>count</c>. A negative count is also a
/// failure: a check that counts less than nothing is broken, and reading it as "nothing to do" would hide that.
///
/// The Director sends what the process produced, raw, and never interprets it; the Gateway reads it here, so
/// there is exactly one reading of the contract (CLAUDE.md rule 7).
/// </summary>
public static class TriggerCheckContract
{
    /// <summary>How much of a check's own output a failure reason quotes, so a reason stays one line.</summary>
    public const int QuotedOutputLimit = 200;

    /// <summary>The most a definition check's <c>context</c> may hold: 8 KB of UTF-8.</summary>
    public const int MaxContextBytes = 8 * 1024;

    /// <summary>The failure of a definition check whose state file outgrew what the Gateway keeps.</summary>
    public const string StateOversizeReason = "the state file is larger than 64 KB";

    /// <summary>Read a COMMAND trigger's report: the contract exactly as it has always been.</summary>
    public static TriggerCheckReading Read(TriggerCheckReport report) => Read(report, definitionCheck: false);

    /// <summary>
    /// Read one report. A DEFINITION check (Factory Definition, phase 5b, design 3.1) is held to two more rules: its
    /// optional <c>context</c> must be a string of at most 8 KB, and its state file must not have outgrown 64 KB. A
    /// command trigger's <c>context</c> is ignored and it has no state - its reading is unchanged.
    /// </summary>
    public static TriggerCheckReading Read(TriggerCheckReport report, bool definitionCheck)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!string.IsNullOrWhiteSpace(report.StartError))
            return Fail($"the check could not start: {OneLine(report.StartError)}");

        if (report.TimedOut)
            return Fail("the check timed out");

        if (report.ExitCode is not { } exitCode)
            return Fail("the check reported no exit code");

        if (exitCode != 0)
        {
            var said = OneLine(report.ErrorOutput);
            if (said.Length == 0) said = OneLine(report.Output);
            return Fail(said.Length == 0 ? $"exit code {exitCode}" : $"exit code {exitCode}: {said}");
        }

        var output = (report.Output ?? "").Trim();
        if (output.Length == 0)
            return Fail("the check printed nothing; it must print JSON with an integer count");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return Fail($"the output is not JSON: {OneLine(output)}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Fail($"the output is JSON but not an object with a count: {OneLine(output)}");

            if (!TryGetCount(doc.RootElement, out var countElement))
                return Fail($"the output has no count: {OneLine(output)}");

            if (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetInt32(out var count))
                return Fail($"the count is not an integer: {OneLine(countElement.GetRawText())}");

            if (count < 0)
                return Fail($"the count is negative: {count}");

            if (!definitionCheck)
                return new TriggerCheckReading(count, null);

            string? context = null;
            if (doc.RootElement.TryGetProperty("context", out var contextElement))
            {
                if (contextElement.ValueKind != JsonValueKind.String)
                    return Fail($"the context is not a string: {OneLine(contextElement.GetRawText())}");
                context = contextElement.GetString()!;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(context);
                if (bytes > MaxContextBytes)
                    return Fail($"the context is {bytes} bytes; it may be at most {MaxContextBytes} bytes (8 KB)");
            }

            // The Director measures the file and says so; a state it sent anyway that is too large is the same failure.
            if (report.StateOversize
                || (report.State is not null && System.Text.Encoding.UTF8.GetByteCount(report.State) > FactoryCheckStateStore.MaxContentBytes))
                return Fail(StateOversizeReason);

            return new TriggerCheckReading(count, null, context);
        }
    }

    // "count", matched exactly. A check that prints "Count" has not kept the contract, and saying so is
    // kinder than guessing what it meant.
    private static bool TryGetCount(JsonElement root, out JsonElement count)
        => root.TryGetProperty("count", out count);

    private static TriggerCheckReading Fail(string reason) => new(null, reason);

    /// <summary>A quoted piece of output on one line, no longer than <see cref="QuotedOutputLimit"/>.</summary>
    internal static string OneLine(string? text)
    {
        var flat = string.Join(' ', (text ?? "").Split(new[] { '\r', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= QuotedOutputLimit ? flat : flat[..QuotedOutputLimit] + "...";
    }
}
