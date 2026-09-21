using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>What one check report came to under the check contract: a count, or the reason it has none.</summary>
/// <param name="Count">The integer the check printed, or null when the check failed.</param>
/// <param name="FailureReason">Why the check failed, in words, or null when it kept the contract.</param>
public readonly record struct TriggerCheckReading(int? Count, string? FailureReason)
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

    public static TriggerCheckReading Read(TriggerCheckReport report)
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

            return new TriggerCheckReading(count, null);
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
