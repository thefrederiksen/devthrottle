using System.Text;
using CcDirector.TurnRuleScorer;

// The corpus scorer for turn detection phase one, work item five.
//
// It runs THE SHIPPED RULES - the objects TerminalStateDetector holds, reached through
// ITerminalNoveltyRule and ITerminalSizeRule - against a corpus of saved screen pairs pinned by
// content hash. It re-implements no rule. If the rule changes, this score changes, with no edit
// here; that is the whole reason the interfaces exist.
//
// The corpus never enters this repository. Both paths are arguments.

Console.OutputEncoding = Encoding.UTF8;

var options = ScorerOptions.Parse(args, Console.Error);
if (options is null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(ScorerOptions.Usage);
    return 2;
}

var report = ScoreRun.Execute(options);
ReportPrinter.Write(report, Console.Out);

// A corpus miss is a failure of the gate, not a footnote: a screen that no longer hashes to its
// pinned value means the evidence moved underneath the manifest, and a run that reported it in the
// middle of a page and still exited zero would be waved through. Non-zero, and the tables are
// printed anyway so what WAS scorable is still readable.
return report.Misses.Count == 0 ? 0 : 1;
