using System.Text;
using CcDirector.CliExplorer.Scenarios;
using CcDirector.Core.Utilities;

namespace CcDirector.CliExplorer.Reporting;

public static class MarkdownReportGenerator
{
    public static string Generate(
        IReadOnlyList<ScenarioCategory> categories,
        IReadOnlyList<TestResult> results,
        TimeSpan totalDuration,
        string claudeVersion,
        string workingDirectory)
    {
        FileLog.Write($"[MarkdownReportGenerator] Generate: categories={categories.Count}, results={results.Count}");

        var sb = new StringBuilder();

        WriteHeader(sb, claudeVersion, workingDirectory, totalDuration);
        WriteSummary(sb, results, totalDuration);
        WriteResults(sb, categories, results);

        FileLog.Write($"[MarkdownReportGenerator] Generate: report length={sb.Length} chars");
        return sb.ToString();
    }

    private static void WriteHeader(StringBuilder sb, string claudeVersion, string workDir, TimeSpan duration)
    {
        sb.AppendLine("# Claude Code CLI Explorer Report");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");
        sb.AppendLine($"| Date | {DateTime.Now:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| Claude Version | {EscapeMd(claudeVersion)} |");
        sb.AppendLine($"| Working Directory | `{workDir}` |");
        sb.AppendLine($"| Duration | {duration.TotalSeconds:F1}s |");
        sb.AppendLine();
    }

    private static void WriteSummary(StringBuilder sb, IReadOnlyList<TestResult> results, TimeSpan duration)
    {
        var pass = results.Count(r => r.Outcome == TestOutcome.Pass);
        var fail = results.Count(r => r.Outcome == TestOutcome.Fail);
        var skip = results.Count(r => r.Outcome == TestOutcome.Skip);
        var error = results.Count(r => r.Outcome == TestOutcome.Error);

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Outcome | Count |");
        sb.AppendLine($"|---------|-------|");
        sb.AppendLine($"| Pass | {pass} |");
        sb.AppendLine($"| Fail | {fail} |");
        sb.AppendLine($"| Skip | {skip} |");
        sb.AppendLine($"| Error | {error} |");
        sb.AppendLine($"| **Total** | **{results.Count}** |");
        sb.AppendLine();
    }

    private static void WriteResults(
        StringBuilder sb,
        IReadOnlyList<ScenarioCategory> categories,
        IReadOnlyList<TestResult> results)
    {
        var resultMap = results.ToDictionary(r => r.Scenario.Id);

        foreach (var cat in categories)
        {
            sb.AppendLine($"## {cat.Name}");
            sb.AppendLine();
            sb.AppendLine($"*{cat.Description}*");
            sb.AppendLine();

            foreach (var scenario in cat.Scenarios)
            {
                if (!resultMap.TryGetValue(scenario.Id, out var result))
                    continue;

                var outcomeEmoji = result.Outcome switch
                {
                    TestOutcome.Pass => "PASS",
                    TestOutcome.Fail => "FAIL",
                    TestOutcome.Skip => "SKIP",
                    TestOutcome.Error => "ERROR",
                    _ => "?"
                };

                sb.AppendLine($"### [{outcomeEmoji}] {scenario.Id}: {scenario.Name}");
                sb.AppendLine();
                sb.AppendLine($"**Description:** {scenario.Description}");
                sb.AppendLine();
                sb.AppendLine($"**Command:**");
                sb.AppendLine($"```");
                sb.AppendLine($"claude {scenario.Arguments}");
                sb.AppendLine($"```");

                if (scenario.StdinText != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Stdin:** `{EscapeMd(Truncate(scenario.StdinText, 200))}`");
                }

                if (result.RunResult != null)
                {
                    var run = result.RunResult;
                    sb.AppendLine();
                    sb.AppendLine($"| Metric | Value |");
                    sb.AppendLine($"|--------|-------|");
                    sb.AppendLine($"| Exit Code | {run.ExitCode} |");
                    sb.AppendLine($"| Duration | {run.Duration.TotalSeconds:F1}s |");
                    sb.AppendLine($"| Timed Out | {run.TimedOut} |");

                    if (!string.IsNullOrWhiteSpace(run.Stdout))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"**Stdout:**");
                        sb.AppendLine("```");
                        sb.AppendLine(Truncate(run.Stdout.Trim(), 2000));
                        sb.AppendLine("```");
                    }

                    if (!string.IsNullOrWhiteSpace(run.Stderr))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"**Stderr:**");
                        sb.AppendLine("```");
                        sb.AppendLine(Truncate(run.Stderr.Trim(), 1000));
                        sb.AppendLine("```");
                    }
                }

                if (result.Notes != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Notes:** {result.Notes}");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "\n... (truncated)";
    }

    private static string EscapeMd(string text)
    {
        return text.Replace("|", "\\|").Replace("`", "\\`");
    }
}
