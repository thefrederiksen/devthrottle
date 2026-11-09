using CcDirector.CliExplorer.Scenarios;

namespace CcDirector.CliExplorer.Reporting;

public static class ConsoleReporter
{
    public static void PrintHeader()
    {
        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║     Claude Code CLI Explorer             ║");
        Console.WriteLine("╚══════════════════════════════════════════╝");
        Console.WriteLine();
    }

    public static void PrintCategoryHeader(ScenarioCategory category)
    {
        Console.WriteLine();
        Console.WriteLine($"── {category.Name} ({category.Scenarios.Count} scenarios) ──");
        Console.WriteLine($"   {category.Description}");
    }

    public static void PrintScenarioStart(TestScenario scenario)
    {
        Console.Write($"  [{scenario.Id}] {scenario.Name,-45} ");
    }

    public static void PrintScenarioResult(TestResult result)
    {
        var symbol = result.Outcome switch
        {
            TestOutcome.Pass => "PASS",
            TestOutcome.Fail => "FAIL",
            TestOutcome.Skip => "SKIP",
            TestOutcome.Error => "ERR ",
            _ => "????"
        };

        var color = result.Outcome switch
        {
            TestOutcome.Pass => ConsoleColor.Green,
            TestOutcome.Fail => ConsoleColor.Red,
            TestOutcome.Skip => ConsoleColor.Yellow,
            TestOutcome.Error => ConsoleColor.Magenta,
            _ => ConsoleColor.Gray
        };

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"[{symbol}]");
        Console.ForegroundColor = prev;

        if (result.RunResult != null)
        {
            Console.Write($"  exit={result.RunResult.ExitCode}  {result.RunResult.Duration.TotalSeconds:F1}s");
        }

        if (result.Notes != null)
        {
            Console.Write($"  ({result.Notes})");
        }

        Console.WriteLine();
    }

    public static void PrintSummary(IReadOnlyList<TestResult> results, TimeSpan totalDuration)
    {
        var pass = results.Count(r => r.Outcome == TestOutcome.Pass);
        var fail = results.Count(r => r.Outcome == TestOutcome.Fail);
        var skip = results.Count(r => r.Outcome == TestOutcome.Skip);
        var error = results.Count(r => r.Outcome == TestOutcome.Error);

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine($"  Total: {results.Count}  Pass: {pass}  Fail: {fail}  Skip: {skip}  Error: {error}");
        Console.WriteLine($"  Duration: {totalDuration.TotalSeconds:F1}s");
        Console.WriteLine("══════════════════════════════════════════");
    }

    public static void PrintDryRun(IReadOnlyList<ScenarioCategory> categories)
    {
        Console.WriteLine("DRY RUN -- showing all scenarios without executing");
        Console.WriteLine();

        var total = 0;
        var apiCount = 0;

        foreach (var cat in categories)
        {
            Console.WriteLine($"── {cat.Name} ({cat.Scenarios.Count} scenarios) ──");
            Console.WriteLine($"   {cat.Description}");

            foreach (var s in cat.Scenarios)
            {
                var apiTag = s.CostsApiCredits ? " [API]" : " [FREE]";
                Console.WriteLine($"  [{s.Id}] {s.Name}{apiTag}");
                Console.WriteLine($"         claude {s.Arguments}");
                if (s.StdinText != null)
                    Console.WriteLine($"         stdin: \"{Truncate(s.StdinText, 60)}\"");
                total++;
                if (s.CostsApiCredits) apiCount++;
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Total: {total} scenarios ({apiCount} require API credits, {total - apiCount} free)");
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "...";
    }
}
