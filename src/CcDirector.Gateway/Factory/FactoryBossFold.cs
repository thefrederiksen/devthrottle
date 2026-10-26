using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// THE BOSS BLOCK ON A FACTORY'S PAGE (the owner, 26 October 2026: "Below the fold: the Boss's limits, the drawing, and
/// the run history"). The Boss holds the owner's why, goal and limits, and the definition holds them as one file,
/// <c>boss.md</c> at its root (the Delivery Lead's ruling of 26 October 2026: no new schema beyond reading that file).
/// This folds that file's text into three parts, in order - why, goal, limits - each with its own heading and either the
/// text written under it or <see cref="NotSetText"/>. Pure.
/// </summary>
public static class FactoryBossFold
{
    /// <summary>The block's heading.</summary>
    public const string Heading = "The Boss";

    /// <summary>What a part says when the file, or its heading, is missing - so the owner sees what is still open.</summary>
    public const string NotSetText = "not set yet";

    /// <summary>The three parts, in the order the page shows them: the key, the heading shown, and the words a
    /// <c>##</c> heading of <c>boss.md</c> starts with to be that part (compared case-insensitively, word by word).</summary>
    private static readonly (string Key, string Heading, string[] Words)[] Parts =
    {
        ("why", "Why", new[] { "the", "why" }),
        ("goal", "Goal", new[] { "the", "goal" }),
        ("limits", "Limits the Boss cannot change", new[] { "limits" }),
    };

    /// <summary>
    /// The block for <paramref name="bossFile"/>, the published definition's <c>boss.md</c> text, or null when the
    /// definition holds none (or the factory is not published yet): then every part is <see cref="NotSetText"/>.
    /// </summary>
    public static FactoryBossBlockDto Block(string? bossFile)
    {
        var split = Split(bossFile);
        return new FactoryBossBlockDto
        {
            Heading = Heading,
            Parts = Parts.Select(p => split.TryGetValue(p.Key, out var text)
                    ? new FactoryBossPartDto { Key = p.Key, Heading = p.Heading, Text = text }
                    : new FactoryBossPartDto { Key = p.Key, Heading = p.Heading, EmptyText = NotSetText })
                .ToList(),
        };
    }

    /// <summary>
    /// The text under each part's <c>##</c> heading, by part key ("why", "goal", "limits"), with the blank lines around
    /// it trimmed. A part whose heading is absent, or has nothing under it, is not in the answer. A section runs to the
    /// next <c>#</c> or <c>##</c> heading; a <c>###</c> heading inside it is its own text. Text under any other heading,
    /// and above the first, is not shown. A heading inside a fenced code block is text. When a part's heading appears
    /// twice, the first one is the part.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Split(string? bossFile)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(bossFile)) return found;

        string? current = null;
        var lines = new List<string>();
        var inFence = false;

        void Close()
        {
            if (current is not null && !found.ContainsKey(current))
            {
                var text = string.Join("\n", lines).Trim('\n', '\r', ' ', '\t');
                if (text.Length > 0) found[current] = text;
            }
            lines.Clear();
        }

        foreach (var raw in bossFile.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence && SectionHeading(trimmed) is { } heading)
            {
                Close();
                // Only a "##" heading is a part; a "#" heading (the file's title) ends one and starts none.
                current = heading.Level == 2 ? PartOf(heading.Text) : null;
                continue;
            }
            if (current is not null) lines.Add(raw);
        }
        Close();
        return found;
    }

    // The level and text of a "# " or "## " heading, or null when the line is not one.
    private static (int Level, string Text)? SectionHeading(string line)
    {
        if (line.StartsWith("## ", StringComparison.Ordinal)) return (2, line[3..]);
        if (line.StartsWith("# ", StringComparison.Ordinal)) return (1, line[2..]);
        if (line == "##") return (2, "");
        if (line == "#") return (1, "");
        return null;
    }

    // The part a heading names, or null when it names none of them.
    private static string? PartOf(string heading)
    {
        var words = heading.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        foreach (var part in Parts)
            if (words.Count >= part.Words.Length
                && part.Words.Select((w, i) => string.Equals(w, words[i], StringComparison.OrdinalIgnoreCase)).All(same => same))
                return part.Key;
        return null;
    }
}
