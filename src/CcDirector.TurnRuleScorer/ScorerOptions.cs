using CcDirector.Core.Wingman;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// What one run is pointed at. BOTH PATHS ARE ARGUMENTS AND NEITHER HAS A DEFAULT. The corpus and
/// the screens it names never enter this repository: this repository is public and those screens
/// are real work carrying client names and file paths. The tool is public; the data is passed in.
/// </summary>
internal sealed record ScorerOptions(
    string ManifestPath,
    string ScreenRoot,
    IReadOnlyList<int> SizeThresholds)
{
    /// <summary>
    /// The size thresholds scored when none are named: exactly one, and it is the threshold the
    /// shipped detector would run (<c>TerminalContentNovelty.StartingSizeRule</c>). A default
    /// sweep would print several numbers of which none is the product's, and the first table
    /// anyone reads should be the one that describes what ships.
    /// </summary>
    internal static IReadOnlyList<int> DefaultSizeThresholds { get; } =
        new[] { TerminalContentNovelty.StartingChangedCharacterThreshold };

    internal const string Usage = """
        Score the shipped turn-detection rules against a pinned wake corpus.

          CcDirector.TurnRuleScorer --manifest <path> --screens <path> [--sizes 20,200,2000]

          --manifest  the pinned corpus manifest, one JSON wake per line
          --screens   the turn-review directory the manifest's screen paths are relative to
          --sizes     comma-separated size-rule thresholds in characters
                      (default: the threshold the shipped detector runs)

        Neither path has a default. The corpus lives outside this repository and is passed in.
        """;

    /// <summary>
    /// Parse the command line. Returns null and writes the reason when the arguments do not name a
    /// manifest and a screen root - there is nothing sensible to fall back to, and a run against a
    /// guessed path would score the wrong evidence and say nothing about it.
    /// </summary>
    internal static ScorerOptions? Parse(IReadOnlyList<string> args, TextWriter errors)
    {
        string? manifest = null, screens = null;
        var thresholds = new List<int>();

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Count:
                    manifest = args[++i];
                    break;
                case "--screens" when i + 1 < args.Count:
                    screens = args[++i];
                    break;
                case "--sizes" when i + 1 < args.Count:
                    foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(part, out var threshold) || threshold < 0)
                        {
                            errors.WriteLine($"ERROR: --sizes takes whole numbers of characters, zero or more; got '{part}'.");
                            return null;
                        }
                        thresholds.Add(threshold);
                    }
                    break;
                default:
                    errors.WriteLine($"ERROR: unrecognised argument '{args[i]}'.");
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(manifest))
        {
            errors.WriteLine("ERROR: --manifest is required.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(screens))
        {
            errors.WriteLine("ERROR: --screens is required.");
            return null;
        }
        if (!File.Exists(manifest))
        {
            errors.WriteLine($"ERROR: no manifest at '{manifest}'.");
            return null;
        }
        if (!Directory.Exists(screens))
        {
            errors.WriteLine($"ERROR: no screen directory at '{screens}'.");
            return null;
        }

        return new ScorerOptions(
            manifest,
            screens,
            thresholds.Count > 0 ? thresholds : DefaultSizeThresholds);
    }
}
