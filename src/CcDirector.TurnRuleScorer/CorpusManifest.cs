using System.Text.Json;
using CcDirector.Core.Agents;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// One wake out of the pinned corpus manifest. The manifest is a line-delimited JSON file living in
/// the PRIVATE repository; it is passed in by path and never copied here, because the screens it
/// names are real work carrying client names and file paths and this repository is public.
///
/// THE LABEL IS A BEHAVIOUR CLASS, NOT A CAUSE. <c>short-unexplained</c> means blue lasted eleven
/// seconds or less with no submission to explain it, and <c>long-unexplained</c> means it lasted
/// over two minutes. Nothing in the records says a screen repainted, and nothing says a background
/// task returned. The names are kept exactly as the manifest writes them so nobody re-attaches a
/// cause on the way through.
/// </summary>
internal sealed record WakeRecord(
    string Session,
    string? Driver,
    string Label,
    double? BlueSeconds,
    bool ExplainedBySubmission,
    string? BeforeScreen,
    string? BeforeSha256,
    string? AfterScreen,
    string? AfterSha256)
{
    /// <summary>A wake is scorable only when a screen was saved at both ends of the blue.</summary>
    internal bool HasBothScreens =>
        !string.IsNullOrEmpty(BeforeScreen) && !string.IsNullOrEmpty(AfterScreen);

    /// <summary>
    /// The agent, as the shipped enum. A driver string the enum does not know, and a wake with no
    /// driver recorded at all, both answer null - the scorer counts those rather than guessing a
    /// kind, because the kind chooses the marker list the row rule is given.
    /// </summary>
    internal AgentKind? Kind =>
        !string.IsNullOrEmpty(Driver) && Enum.TryParse<AgentKind>(Driver, ignoreCase: false, out var kind)
            ? kind
            : null;

    /// <summary>What to print for this wake's agent in a per-agent table.</summary>
    internal string AgentLabel => string.IsNullOrEmpty(Driver) ? "(none recorded)" : Driver;
}

internal static class CorpusManifest
{
    /// <summary>
    /// Read every wake in the manifest, in file order. A line that will not parse stops the run:
    /// a manifest that is silently short is exactly the moving corpus this pinning exists to end.
    /// </summary>
    internal static IReadOnlyList<WakeRecord> Read(string path)
    {
        var wakes = new List<WakeRecord>();
        int lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                wakes.Add(Parse(line));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} line {lineNumber} is not readable JSON: {ex.Message}");
            }
        }
        return wakes;
    }

    private static WakeRecord Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var row = document.RootElement;
        return new WakeRecord(
            Session: Text(row, "session") ?? "",
            Driver: Text(row, "driver"),
            Label: Text(row, "label") ?? "",
            BlueSeconds: Number(row, "blueSeconds"),
            ExplainedBySubmission: Flag(row, "explainedBySubmission"),
            BeforeScreen: Text(row, "beforeScreen"),
            BeforeSha256: Text(row, "beforeSha256"),
            AfterScreen: Text(row, "afterScreen"),
            AfterSha256: Text(row, "afterSha256"));
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
