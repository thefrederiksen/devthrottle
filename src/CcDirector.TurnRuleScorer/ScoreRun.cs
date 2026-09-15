using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// The run: read the pinned manifest, verify every screen against its pinned hash, split each
/// screen into a body, and ask THE SHIPPED RULES what they make of each pair.
///
/// The rules are not described here and are not re-stated here. They are constructed from
/// <see cref="TerminalContentNovelty"/> and held as <see cref="ITerminalNoveltyRule"/> - the same
/// interface <c>TerminalStateDetector</c> holds them through - so a change to the rule changes this
/// score with no edit to this file. That is the entire point: the corpus scores what ships.
/// </summary>
internal static class ScoreRun
{
    /// <summary>
    /// The old rule, and the baseline every candidate is read against: at a settled session, ANY
    /// byte opens the turn. It is a constant here rather than a rule object because that is what it
    /// is in the product - <c>TurnContentRule.Off</c> is the absence of a content check, and the
    /// detector's off branch opens the turn without consulting any rule at all. There is no shipped
    /// object to ask, so there is none to borrow.
    /// </summary>
    internal const string ByteRuleName = "today: any byte opens";

    internal static ScoreReport Execute(ScorerOptions options)
    {
        var wakes = CorpusManifest.Read(options.ManifestPath);
        var paired = wakes.Where(w => w.HasBothScreens).ToList();

        var candidates = Candidates(options.SizeThresholds);
        var counts = new Dictionary<(string Candidate, string Label, string Agent), (int Pairs, int Opened)>();
        var misses = new List<CorpusMiss>();
        var unknownDrivers = new SortedSet<string>(StringComparer.Ordinal);

        int screens = 0, noAnchor = 0, singleAnchor = 0, severalAnchors = 0, emptyScreens = 0;
        int scored = 0;
        var emptyPairs = new Dictionary<(string Agent, string Label), int>();

        foreach (var wake in paired)
        {
            if (!TryReadScreen(options.ScreenRoot, wake, wake.BeforeScreen!, wake.BeforeSha256, misses, out var before)) continue;
            if (!TryReadScreen(options.ScreenRoot, wake, wake.AfterScreen!, wake.AfterSha256, misses, out var after)) continue;

            foreach (var rows in new[] { before!.Rows, after!.Rows })
            {
                screens++;
                if (rows.Count == 0) emptyScreens++;
                switch (ScreenBodySplit.ConfidenceOf(rows))
                {
                    case ScreenBodySplit.Confidence.NoAnchor: noAnchor++; break;
                    case ScreenBodySplit.Confidence.SingleAnchor: singleAnchor++; break;
                    default: severalAnchors++; break;
                }
            }

            // An empty capture is not a corpus miss - the file is there and it hashes to the pinned
            // value, so the corpus is intact. It is the absence of evidence, and it is counted
            // rather than dropped: dropping it would be quietly choosing which pinned pairs count.
            if (before.Rows.Count == 0 || after.Rows.Count == 0)
            {
                var emptyKey = (wake.AgentLabel, wake.Label);
                emptyPairs.TryGetValue(emptyKey, out var seen);
                emptyPairs[emptyKey] = seen + 1;
            }

            var settledBody = ScreenBodySplit.Body(before.Rows);
            var currentBody = ScreenBodySplit.Body(after.Rows);
            var markers = MarkersFor(wake, unknownDrivers);

            scored++;
            Record(counts, ByteRuleName, wake, opened: true);
            foreach (var rule in candidates)
            {
                bool opened = rule.GainedContent(settledBody, currentBody, markers, out _);
                Record(counts, rule.Name, wake, opened);
            }
        }

        var tallies = counts
            .Select(e => new Tally(e.Key.Candidate, e.Key.Label, e.Key.Agent, e.Value.Pairs, e.Value.Opened))
            .ToList();

        return new ScoreReport(
            ManifestPath: options.ManifestPath,
            ScreenRoot: options.ScreenRoot,
            Wakes: wakes.Count,
            PairsInManifest: paired.Count,
            PairsScored: scored,
            Misses: misses,
            BodySplit: new BodySplitCensus(screens, noAnchor, singleAnchor, severalAnchors),
            EmptyScreens: emptyScreens,
            PairsTouchingAnEmptyScreen: emptyPairs
                .Select(e => new EmptyScreenPairs(e.Key.Agent, e.Key.Label, e.Value))
                .OrderBy(e => e.Agent, StringComparer.Ordinal)
                .ThenBy(e => e.Label, StringComparer.Ordinal)
                .ToList(),
            UnknownDrivers: unknownDrivers.ToList(),
            Tallies: tallies);
    }

    /// <summary>
    /// The candidates, in the order they are printed: the row rule, then one size rule per
    /// threshold asked for. Both come out of <see cref="TerminalContentNovelty"/>'s own factories,
    /// so the size rule's threshold is the one the rule declares rather than one this tool decided.
    /// </summary>
    internal static IReadOnlyList<ITerminalNoveltyRule> Candidates(IReadOnlyList<int> sizeThresholds)
    {
        var rules = new List<ITerminalNoveltyRule> { TerminalContentNovelty.RowRule };
        foreach (var threshold in sizeThresholds) rules.Add(TerminalContentNovelty.SizeRule(threshold));
        return rules;
    }

    /// <summary>
    /// The marker list the row rule is given, taken from THE REAL DRIVER for the agent the manifest
    /// recorded - the same object <c>TerminalStateDetector</c> reads
    /// <c>session.Driver.SelfDescribingRowMarkers</c> from. A driver string the enum does not know
    /// gets an empty list and is counted: guessing a kind would hand one agent another agent's
    /// markers, which is a different rule.
    /// </summary>
    private static IReadOnlyCollection<string> MarkersFor(WakeRecord wake, ISet<string> unknownDrivers)
    {
        var kind = wake.Kind;
        if (kind is null)
        {
            unknownDrivers.Add(wake.AgentLabel);
            return Array.Empty<string>();
        }
        return AgentDrivers.For(kind.Value).SelfDescribingRowMarkers;
    }

    private static void Record(
        Dictionary<(string, string, string), (int Pairs, int Opened)> counts,
        string candidate,
        WakeRecord wake,
        bool opened)
    {
        var key = (candidate, wake.Label, wake.AgentLabel);
        counts.TryGetValue(key, out var current);
        counts[key] = (current.Pairs + 1, current.Opened + (opened ? 1 : 0));
    }

    /// <summary>
    /// Read one screen, but only after its bytes hash to the value the manifest pinned. The order
    /// matters: hash first, parse second, so a file that changed is reported as a corpus miss
    /// rather than scored on its new contents.
    /// </summary>
    private static bool TryReadScreen(
        string screenRoot,
        WakeRecord wake,
        string relativePath,
        string? pinnedHash,
        List<CorpusMiss> misses,
        out SavedScreen? screen)
    {
        screen = null;
        var path = Path.Combine(screenRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            misses.Add(new CorpusMiss(CorpusMissKind.ScreenFileMissing, wake.Session, relativePath, "not under the screen root"));
            return false;
        }

        if (string.IsNullOrEmpty(pinnedHash))
        {
            misses.Add(new CorpusMiss(CorpusMissKind.NoPinnedHash, wake.Session, relativePath, "the manifest pins no hash for this screen"));
            return false;
        }

        var actual = SavedScreen.HashFile(path);
        if (!string.Equals(actual, pinnedHash, StringComparison.OrdinalIgnoreCase))
        {
            misses.Add(new CorpusMiss(
                CorpusMissKind.ScreenHashMismatch, wake.Session, relativePath,
                $"pinned {pinnedHash}, found {actual}"));
            return false;
        }

        try
        {
            screen = SavedScreen.Read(path);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            misses.Add(new CorpusMiss(CorpusMissKind.ScreenUnreadable, wake.Session, relativePath, ex.Message));
            return false;
        }
    }
}
