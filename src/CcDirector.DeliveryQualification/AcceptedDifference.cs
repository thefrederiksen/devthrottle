using System.Text.Json;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE ONE DIFFERENCE THE PARITY MATRIX ACCEPTS (issue #4378, the mission's 11.2 and 11.5; widened in issue #4379): the
/// agent's own trim of the white space at the edges of a prompt. A person typing or pasting at the agent gets exactly the
/// same, so it is parity, not a Director fault (decision 8).
///
/// Two conditions, both required:
///  - THE SHAPE. Claude Code trims the trailing edge; Codex and Pi trim BOTH edges. The baseline's note (11.2) said Codex
///    and Pi trim the leading spaces only - measured too narrowly: their records of the edge-spaces text, which has spaces
///    at both ends, hold neither (23 October 2026, 124 of 130 characters). Only that trim is accepted; any change inside
///    the text, or a trim at an edge the agent does not trim, still fails.
///  - THE DIRECTOR WROTE THE SPACES. The same text's echo cell - the same route, standing in for the same agent - must show
///    the Director wrote the text exactly, edge spaces included (<see cref="EchoProof"/>). Without that proof the missing
///    spaces may be the Director's, and the cell fails.
/// The accepted trim is named in the result row, so it is never silent.
/// </summary>
public static class AcceptedDifference
{
    /// <summary>
    /// Why <paramref name="got"/> (the agent's record, line endings made canonical) is accepted although it differs from
    /// <paramref name="sent"/>, or null when it is not: the shape is not the agent's own trim, or no echo cell proves the
    /// Director wrote the spaces. Null too when the two are equal - nothing needs accepting.
    /// </summary>
    public static string? AgentTrim(string agent, string sent, string got, EchoProof proof, string route, string textName)
    {
        if (TrimShape(agent, sent, got) is not { } why) return null;
        return proof.WroteExactly(route, agent, textName) is { } where ? $"{why}; {where}" : null;
    }

    /// <summary>
    /// The agent's own trim, by shape alone: the reason when <paramref name="got"/> is <paramref name="sent"/> with exactly
    /// the edge white space this agent trims removed, else null.
    /// </summary>
    public static string? TrimShape(string agent, string sent, string got)
    {
        var want = EchoAnalysis.Canonical(sent);
        if (got == want) return null;
        var (trimmed, why) = agent switch
        {
            "claude" => (want.TrimEnd(' '), "Claude Code trims trailing spaces"),
            "codex" => (want.Trim(), "Codex trims the white space at both edges"),
            "pi" => (want.Trim(), "Pi trims the white space at both edges"),
            _ => (want, ""),
        };
        return trimmed != want && got == trimmed ? why : null;
    }
}

/// <summary>
/// What the echo layer proved the Director WRITES (issue #4379): read from an echo run's <c>echo-results.jsonl</c>
/// (<c>--echo-proof</c>). A text shape on a route, standing in for an agent, is proven written exactly when every echo cell
/// for it was EXACT - the bytes the Director wrote held the whole text, its edge spaces included.
/// </summary>
public sealed class EchoProof
{
    private readonly Dictionary<(string Route, string As, string Text), List<bool>> _cells;
    private readonly string _source;

    private EchoProof(Dictionary<(string, string, string), List<bool>> cells, string source)
    {
        _cells = cells;
        _source = source;
    }

    /// <summary>No echo run given: nothing is proven, so no trim is accepted.</summary>
    public static readonly EchoProof None = new(new(), "");

    /// <summary>The echo results in <paramref name="path"/>. A missing file is an error: a proof that silently found nothing
    /// would fail every trimmed cell for the wrong reason.</summary>
    public static EchoProof Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"[EchoProof] the echo results file does not exist: {path}", path);
        var cells = new Dictionary<(string, string, string), List<bool>>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var r = JsonSerializer.Deserialize<EchoResult>(line) ?? throw new InvalidDataException($"[EchoProof] unreadable line in {path}");
            var key = (r.Route, r.As, r.Text);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<bool>();
            list.Add(r.Verdict.Verdict == "EXACT");
        }
        return new EchoProof(cells, path);
    }

    /// <summary>Where the proof is, when every echo cell of this text on this route as this agent was EXACT; else null.</summary>
    public string? WroteExactly(string route, string agent, string textName) =>
        _cells.TryGetValue((route, agent, textName), out var runs) && runs.Count > 0 && runs.All(x => x)
            ? $"the echo cell {route} as {agent} {textName} shows the Director wrote them ({runs.Count} of {runs.Count} EXACT)"
            : null;

    /// <summary>For the log: where the proof was read from.</summary>
    public override string ToString() => _source.Length == 0 ? "no echo proof (--echo-proof not given)" : _source;
}
