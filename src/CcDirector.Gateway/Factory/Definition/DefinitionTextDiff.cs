using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A line difference between two texts, for "what changes" (Factory Definition, phase 1). Pure and deterministic.
///
/// Nothing in the repository computes one: <c>CcDirector.Core.Git.DiffParser</c> only parses git's output, and the
/// Gateway has no git to run. So the shortest edit script is found here with Myers' algorithm, after the common first
/// and last lines are set aside.
///
/// BOUNDED, because a file may hold 256 KB: the search stops after <see cref="MaxEditDistance"/> edits, and then the
/// changed middle is shown as removed in full and added in full, with a note saying so - still a true difference,
/// only not the shortest. At most <see cref="MaxLinesShown"/> lines are shown, then a note says how many were left
/// out; a line longer than <see cref="MaxLineChars"/> characters is cut, saying how much was cut.
/// </summary>
public static class DefinitionTextDiff
{
    /// <summary>Unchanged lines shown before and after each change.</summary>
    public const int ContextLines = 3;

    /// <summary>The most lines (unchanged, added or removed) one difference shows.</summary>
    public const int MaxLinesShown = 400;

    /// <summary>The most characters of one line shown.</summary>
    public const int MaxLineChars = 500;

    /// <summary>The most edits the search looks for before it shows the changed part whole.</summary>
    public const int MaxEditDistance = 1000;

    private enum OpKind { Equal, Removed, Added }

    private readonly record struct Op(OpKind Kind, int Old, int New);

    /// <summary>
    /// The difference from <paramref name="oldText"/> to <paramref name="newText"/>; null stands for a file that is
    /// not there (every line of the other is added or removed).
    /// </summary>
    public static FactoryDefinitionTextDiffDto Compare(string? oldText, string? newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var (ops, matched) = EditScript(a, b);

        var dto = new FactoryDefinitionTextDiffDto
        {
            Added = ops.Count(o => o.Kind == OpKind.Added),
            Removed = ops.Count(o => o.Kind == OpKind.Removed),
        };
        Render(ops, a, b, dto);

        var notes = new List<string>();
        if (!matched)
            notes.Add($"The two texts are too different to match line by line (more than {MaxEditDistance} changes), so the changed part is shown removed in full and then added in full.");
        if (dto.LinesNotShown > 0)
            notes.Add($"{dto.LinesNotShown} more {(dto.LinesNotShown == 1 ? "line" : "lines")} of this difference {(dto.LinesNotShown == 1 ? "is" : "are")} not shown; at most {MaxLinesShown} are shown.");
        if (dto.Added == 0 && dto.Removed == 0 && !string.Equals(oldText ?? "", newText ?? "", StringComparison.Ordinal))
            notes.Add("Only the line endings or the final line break change.");
        dto.Note = notes.Count == 0 ? null : string.Join(" ", notes);
        return dto;
    }

    /// <summary>The lines of a text: split on line feeds, a carriage return before one dropped, no empty last line.</summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        if (text.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static (List<Op> Ops, bool Matched) EditScript(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Lines compared as numbers: each distinct text gets one.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s) => ids.TryGetValue(s, out var i) ? i : ids[s] = ids.Count;
        var x = a.Select(Id).ToArray();
        var y = b.Select(Id).ToArray();

        var prefix = 0;
        while (prefix < x.Length && prefix < y.Length && x[prefix] == y[prefix]) prefix++;
        var suffix = 0;
        while (suffix < x.Length - prefix && suffix < y.Length - prefix && x[^(suffix + 1)] == y[^(suffix + 1)]) suffix++;

        var ops = new List<Op>(x.Length + y.Length);
        for (var i = 0; i < prefix; i++) ops.Add(new Op(OpKind.Equal, i, i));

        var middleA = x[prefix..(x.Length - suffix)];
        var middleB = y[prefix..(y.Length - suffix)];
        // With one side empty the script is all added or all removed; the search is not run, so a long new file is
        // never reported as too different to match.
        var middle = middleA.Length == 0 || middleB.Length == 0 ? null : Myers(middleA, middleB);
        var matched = middle is not null || middleA.Length == 0 || middleB.Length == 0;
        if (middle is null)
        {
            middle = new List<Op>();
            for (var i = 0; i < middleA.Length; i++) middle.Add(new Op(OpKind.Removed, i, -1));
            for (var j = 0; j < middleB.Length; j++) middle.Add(new Op(OpKind.Added, -1, j));
        }
        foreach (var op in middle)
            ops.Add(new Op(op.Kind, op.Old < 0 ? -1 : op.Old + prefix, op.New < 0 ? -1 : op.New + prefix));

        for (var i = 0; i < suffix; i++)
            ops.Add(new Op(OpKind.Equal, x.Length - suffix + i, y.Length - suffix + i));
        return (ops, matched);
    }

    /// <summary>
    /// Myers' shortest edit script, or null when it needs more than <see cref="MaxEditDistance"/> edits. Each step's
    /// furthest-reaching points are kept only for the diagonals that step can read back, so memory grows with the
    /// square of the edits, never with the length of the file.
    /// </summary>
    private static List<Op>? Myers(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 && m == 0) return new List<Op>();
        var limit = Math.Min(n + m, MaxEditDistance);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= limit; d++)
        {
            // What step d reads back: diagonals -(d+1) .. d+1.
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                var px = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var py = px - k;
                while (px < n && py < m && a[px] == b[py]) { px++; py++; }
                v[offset + k] = px;
                if (px >= n && py >= m) return Backtrack(trace, n, m);
            }
        }
        return null;
    }

    private static List<Op> Backtrack(List<int[]> trace, int n, int m)
    {
        var ops = new List<Op>();
        int x = n, y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var snap = trace[d];
            int V(int k) => snap[k + d + 1];
            var k0 = x - y;
            var prevK = k0 == -d || (k0 != d && V(k0 - 1) < V(k0 + 1)) ? k0 + 1 : k0 - 1;
            var prevX = V(prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; ops.Add(new Op(OpKind.Equal, x, y)); }
            if (d > 0)
            {
                if (x == prevX) ops.Add(new Op(OpKind.Added, -1, prevY));
                else ops.Add(new Op(OpKind.Removed, prevX, -1));
            }
            x = prevX;
            y = prevY;
        }
        ops.Reverse();
        return ops;
    }

    private static void Render(List<Op> ops, IReadOnlyList<string> a, IReadOnlyList<string> b, FactoryDefinitionTextDiffDto dto)
    {
        if (dto.Added == 0 && dto.Removed == 0) return;

        // Which ops are shown: every change, and ContextLines unchanged lines either side of one.
        var show = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == OpKind.Equal) continue;
            for (var j = Math.Max(0, i - ContextLines); j <= Math.Min(ops.Count - 1, i + ContextLines); j++) show[j] = true;
        }

        var shown = 0;
        var i0 = 0;
        while (i0 < ops.Count)
        {
            if (!show[i0])
            {
                var start = i0;
                while (i0 < ops.Count && !show[i0]) i0++;
                var run = i0 - start;
                if (shown < MaxLinesShown)
                    dto.Lines.Add(new FactoryDefinitionDiffLineDto
                    {
                        Kind = "skipped",
                        Marker = "",
                        Text = $"... {run} unchanged {(run == 1 ? "line" : "lines")} ...",
                    });
                continue;
            }

            var op = ops[i0++];
            if (shown >= MaxLinesShown) { dto.LinesNotShown++; continue; }
            shown++;
            dto.Lines.Add(op.Kind switch
            {
                OpKind.Equal => new FactoryDefinitionDiffLineDto { Kind = "context", Marker = " ", Text = Cut(a[op.Old]), OldLine = op.Old + 1, NewLine = op.New + 1 },
                OpKind.Removed => new FactoryDefinitionDiffLineDto { Kind = "removed", Marker = "-", Text = Cut(a[op.Old]), OldLine = op.Old + 1 },
                _ => new FactoryDefinitionDiffLineDto { Kind = "added", Marker = "+", Text = Cut(b[op.New]), NewLine = op.New + 1 },
            });
        }
    }

    private static string Cut(string line) => line.Length <= MaxLineChars
        ? line
        : $"{line[..MaxLineChars]} ... ({line.Length - MaxLineChars} more characters)";
}
