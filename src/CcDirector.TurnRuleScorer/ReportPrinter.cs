using System.Globalization;
using System.Text;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// Turns a <see cref="ScoreReport"/> into the printed tables. Plain ASCII, and the caveats go
/// FIRST - above the numbers, not under them - because the two things that most change how these
/// numbers should be read (the body split is a guess, and the labels are behaviour classes rather
/// than observed causes) are the two things a reader skips if they are printed as a footnote.
/// </summary>
internal static class ReportPrinter
{
    /// <summary>The classes, in printed order, with what the interesting number means for each.</summary>
    private static readonly (string Label, string Column, bool CountHeld)[] Classes =
    [
        ("short-unexplained", "held red", true),
        ("long-unexplained", "opened", false),
        ("explained", "opened", false),
        ("grey", "opened", false),
    ];

    internal static void Write(ScoreReport report, TextWriter output)
    {
        WriteHeading(report, output);
        WriteCaveats(output);
        WriteCorpus(report, output);
        WriteBodySplit(report, output);
        WriteEmptyScreens(report, output);
        WriteMisses(report, output);

        var candidates = CandidateOrder(report);

        output.WriteLine();
        output.WriteLine("EVERY AGENT TOGETHER");
        output.WriteLine("An aggregate number here is evidence about one agent on one machine - see the");
        output.WriteLine("per-agent tables below, which is where over-reach shows up.");
        WriteTable(report.Tallies, candidates, output);

        foreach (var agent in report.Tallies.Select(t => t.Agent).Distinct().OrderBy(a => a, StringComparer.Ordinal))
        {
            var forAgent = report.Tallies.Where(t => t.Agent == agent).ToList();
            output.WriteLine();
            output.WriteLine($"AGENT: {agent}");
            WriteEmptyWarning(report, agent, forAgent, output);
            WriteTable(forAgent, candidates, output);
        }

        output.WriteLine();
        output.WriteLine("WHAT THIS RUN DID NOT MEASURE");
        output.WriteLine("  The settling window. The two screens in a pair are about ten seconds apart and");
        output.WriteLine("  carry no byte timing at all, so nothing here can justify any delay before the");
        output.WriteLine("  screen is read.");
        output.WriteLine("  The rule as production runs it. Production splits the body at the real cursor;");
        output.WriteLine("  this run guessed. The corpus has NOT scored the rule that actually ships.");
        output.WriteLine("  Live bytes. The shadow comparison on a running Director is still outstanding.");
    }

    private static void WriteHeading(ScoreReport report, TextWriter output)
    {
        output.WriteLine("TURN DETECTION - THE SHIPPED RULES SCORED ON A PINNED CORPUS");
        output.WriteLine($"manifest: {report.ManifestPath}");
        output.WriteLine($"screens : {report.ScreenRoot}");
    }

    private static void WriteCaveats(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("READ THIS FIRST - IT CHANGES WHAT THE NUMBERS MEAN");
        output.WriteLine("  1. THE BODY SPLIT IS A GUESS AND IT CONTROLS THE RESULT. Saved screens do not");
        output.WriteLine("     record the cursor; production does. This tool finds the input box by taking");
        output.WriteLine("     the last row that starts with a prompt glyph. The census below says how often");
        output.WriteLine("     that guess had nothing to go on or had to choose. THE CORPUS HAS THEREFORE");
        output.WriteLine("     NOT SCORED THE RULE THAT ACTUALLY SHIPS.");
        output.WriteLine("  2. THE LABELS ARE BEHAVIOUR CLASSES, NOT CAUSES. 'short-unexplained' means blue");
        output.WriteLine("     lasted eleven seconds or less with no submission to explain it, which is a");
        output.WriteLine("     shape the rule being REPLACED produces. 'long-unexplained' means it lasted");
        output.WriteLine("     over two minutes. No record says a screen repainted and no record says a");
        output.WriteLine("     background task returned. Nothing below is an observed cause.");
        output.WriteLine("  3. THIS IS A REGRESSION GATE, NOT PROOF. It catches a change that loses");
        output.WriteLine("     suppression or stops opening long wakes. It cannot show the rule is better.");
    }

    private static void WriteCorpus(ScoreReport report, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("CORPUS");
        output.WriteLine($"  wakes in the manifest        {report.Wakes,8}");
        output.WriteLine($"  pairs with both screens      {report.PairsInManifest,8}");
        output.WriteLine($"  pairs scored                 {report.PairsScored,8}");
        output.WriteLine($"  corpus misses (not scored)   {report.Misses.Count,8}");
        if (report.UnknownDrivers.Count > 0)
        {
            output.WriteLine("  agents the product does not know, scored with NO marker list:");
            foreach (var driver in report.UnknownDrivers) output.WriteLine($"    {driver}");
        }
    }

    private static void WriteBodySplit(ScoreReport report, TextWriter output)
    {
        var split = report.BodySplit;
        output.WriteLine();
        output.WriteLine("BODY SPLIT CENSUS - HOW BIG THE GUESS IS (counted over screens, not pairs)");
        output.WriteLine($"  screens read and split       {split.Screens,8}");
        output.WriteLine($"  exactly one prompt-like row  {split.SingleAnchor,8}   {Percent(split.SingleAnchor, split.Screens)}  the only unambiguous case");
        output.WriteLine($"  no prompt-like row at all    {split.NoAnchor,8}   {Percent(split.NoAnchor, split.Screens)}  the whole screen became the body");
        output.WriteLine($"  several prompt-like rows     {split.SeveralAnchors,8}   {Percent(split.SeveralAnchors, split.Screens)}  the last one was taken");
        output.WriteLine($"  AMBIGUOUS                    {split.Ambiguous,8}   {Percent(split.Ambiguous, split.Screens)}");
    }

    private static void WriteMisses(ScoreReport report, TextWriter output)
    {
        if (report.Misses.Count == 0) return;

        output.WriteLine();
        output.WriteLine("CORPUS MISSES - REPORTED, NOT SCORED");
        output.WriteLine("A screen that no longer matches its pinned hash is not evidence. These pairs were");
        output.WriteLine("dropped from every table above and below.");
        foreach (var group in report.Misses.GroupBy(m => m.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
            foreach (var miss in group.Take(20))
            {
                output.WriteLine($"    {miss.Session} {miss.RelativePath} - {miss.Detail}");
            }
            if (group.Count() > 20) output.WriteLine($"    ... and {group.Count() - 20} more");
        }
    }

    private static void WriteEmptyScreens(ScoreReport report, TextWriter output)
    {
        if (report.EmptyScreens == 0) return;

        int pairs = report.PairsTouchingAnEmptyScreen.Sum(e => e.Pairs);
        output.WriteLine();
        output.WriteLine("EMPTY CAPTURES - THE ABSENCE OF EVIDENCE, NOT A VERDICT");
        output.WriteLine($"  screens with no rows at all  {report.EmptyScreens,8}");
        output.WriteLine($"  pairs touching one           {pairs,8}");
        output.WriteLine("  A pair whose screens are empty scores as 'gained nothing' for every candidate");
        output.WriteLine("  and as 'opened' for the old byte rule, which in a table is indistinguishable");
        output.WriteLine("  from a rule suppressing a real reply. It is neither. The capture is empty.");
        output.WriteLine("  These pairs are still counted, because dropping them would be choosing which");
        output.WriteLine("  pinned pairs count; they are named here so no row below is misread.");
        foreach (var group in report.PairsTouchingAnEmptyScreen)
        {
            output.WriteLine($"    {group.Agent} / {group.Label}: {group.Pairs}");
        }
    }

    /// <summary>
    /// Said at the top of an agent's own table, where it cannot be missed: how much of THIS agent's
    /// population is empty captures. When it is all of it, the table below establishes nothing
    /// about that agent whatsoever, and saying so in the census alone is not enough - a reader who
    /// scrolls to the agent they care about would never see it.
    /// </summary>
    private static void WriteEmptyWarning(ScoreReport report, string agent, IReadOnlyList<Tally> forAgent, TextWriter output)
    {
        int empty = report.PairsTouchingAnEmptyScreen.Where(e => e.Agent == agent).Sum(e => e.Pairs);
        if (empty == 0) return;

        // Every candidate saw every pair, so one candidate's pair count is this agent's population.
        int pairs = forAgent.Where(t => t.Candidate == ScoreRun.ByteRuleName).Sum(t => t.Pairs);
        output.WriteLine(empty >= pairs
            ? $"  WARNING: ALL {pairs} of this agent's pairs carry an empty capture. Nothing below is"
            : $"  WARNING: {empty} of this agent's {pairs} pairs carry an empty capture, so part of what is");
        output.WriteLine(empty >= pairs
            ? "  evidence about this agent - it is evidence that nothing was saved."
            : "  below is the absence of a capture rather than a verdict about the rule.");
    }

    /// <summary>
    /// The old byte rule first, because every candidate is read against it, then the candidates in
    /// the order the run constructed them.
    /// </summary>
    private static IReadOnlyList<string> CandidateOrder(ScoreReport report)
    {
        var order = new List<string> { ScoreRun.ByteRuleName };
        foreach (var name in report.Tallies.Select(t => t.Candidate))
        {
            if (!order.Contains(name, StringComparer.Ordinal)) order.Add(name);
        }
        return order;
    }

    private static void WriteTable(IReadOnlyList<Tally> tallies, IReadOnlyList<string> candidates, TextWriter output)
    {
        var header = new StringBuilder();
        header.Append("rule".PadRight(22));
        foreach (var (label, column, _) in Classes)
        {
            header.Append(("  " + label + " " + column).PadLeft(30));
        }
        output.WriteLine(header.ToString());
        output.WriteLine(new string('-', header.Length));

        foreach (var candidate in candidates)
        {
            var line = new StringBuilder();
            line.Append(Shorten(candidate, 22).PadRight(22));
            foreach (var (label, _, countHeld) in Classes)
            {
                var rows = tallies.Where(t => t.Candidate == candidate && t.Label == label).ToList();
                int pairs = rows.Sum(t => t.Pairs);
                int opened = rows.Sum(t => t.Opened);
                int shown = countHeld ? pairs - opened : opened;
                line.Append(pairs == 0
                    ? "-".PadLeft(30)
                    : $"{shown}/{pairs} {Percent(shown, pairs)}".PadLeft(30));
            }
            output.WriteLine(line.ToString());
        }
    }

    private static string Shorten(string text, int width) =>
        text.Length <= width ? text : text[..width];

    private static string Percent(int part, int whole) =>
        whole == 0
            ? "    -"
            : (100.0 * part / whole).ToString("F1", CultureInfo.InvariantCulture).PadLeft(5) + "%";
}
