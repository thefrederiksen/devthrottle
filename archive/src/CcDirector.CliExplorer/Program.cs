using System.Diagnostics;
using CcDirector.CliExplorer.Execution;
using CcDirector.CliExplorer.Reporting;
using CcDirector.CliExplorer.Scenarios;
using CcDirector.CliExplorer.Scenarios.Categories;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.CliExplorer;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        FileLog.Start();
        FileLog.Write("[Program] CLI Explorer starting");

        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL: {ex.Message}");
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
        finally
        {
            FileLog.Stop();
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);
        if (options == null) return 1;

        ConsoleReporter.PrintHeader();

        // Build all scenario categories
        var allCategories = BuildAllCategories();
        FileLog.Write($"[Program] Built {allCategories.Count} categories with {allCategories.Sum(c => c.Scenarios.Count)} total scenarios");

        // Filter by category if specified
        var categories = allCategories;
        if (options.Category != null)
        {
            categories = allCategories
                .Where(c => c.Name.Contains(options.Category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (categories.Count == 0)
            {
                Console.Error.WriteLine($"No category matching \"{options.Category}\". Available:");
                foreach (var cat in allCategories)
                    Console.Error.WriteLine($"  - {cat.Name}");
                return 1;
            }
        }

        // Filter by scenario if specified
        if (options.ScenarioId != null)
        {
            var filtered = new List<ScenarioCategory>();
            foreach (var cat in categories)
            {
                var matching = cat.Scenarios
                    .Where(s => s.Id.Equals(options.ScenarioId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matching.Count > 0)
                    filtered.Add(cat with { Scenarios = matching });
            }
            categories = filtered;

            if (categories.Count == 0)
            {
                Console.Error.WriteLine($"No scenario with ID \"{options.ScenarioId}\".");
                return 1;
            }
        }

        // Dry run mode
        if (options.DryRun)
        {
            ConsoleReporter.PrintDryRun(categories);
            return 0;
        }

        // Test session ID helper
        if (options.TestSessionId)
            return await RunTestSessionId();

        // Find claude.exe
        var claudePath = ClaudeRunner.FindClaudeOnPath();
        if (claudePath == null)
        {
            Console.Error.WriteLine("ERROR: claude.exe not found on PATH.");
            return 1;
        }
        Console.WriteLine($"Claude: {claudePath}");

        // Get version for report header
        var claudeVersion = await GetClaudeVersion(claudePath);
        Console.WriteLine($"Version: {claudeVersion}");

        var workDir = Directory.GetCurrentDirectory();
        Console.WriteLine($"Working dir: {workDir}");
        Console.WriteLine();

        // Resolve resource paths relative to the exe directory
        var resourceDir = Path.Combine(AppContext.BaseDirectory, "Resources");
        var runner = new ClaudeRunner(claudePath, workDir);

        // Execute scenarios
        var results = new List<TestResult>();
        var totalSw = Stopwatch.StartNew();

        foreach (var category in categories)
        {
            ConsoleReporter.PrintCategoryHeader(category);

            foreach (var scenario in category.Scenarios)
            {
                // Skip API scenarios if --skip-api
                if (options.SkipApi && scenario.CostsApiCredits)
                {
                    var skipResult = new TestResult(scenario, null, TestOutcome.Skip, "Skipped: costs API credits");
                    results.Add(skipResult);
                    ConsoleReporter.PrintScenarioStart(scenario);
                    ConsoleReporter.PrintScenarioResult(skipResult);
                    continue;
                }

                ConsoleReporter.PrintScenarioStart(scenario);

                // Resolve resource file paths in arguments
                var resolvedArgs = ResolveResourcePaths(scenario.Arguments, resourceDir);

                try
                {
                    var runResult = await runner.RunAsync(resolvedArgs, scenario.StdinText, scenario.TimeoutMs);
                    var outcome = EvaluateOutcome(scenario, runResult);
                    var notes = BuildNotes(scenario, runResult, outcome);
                    var testResult = new TestResult(scenario, runResult, outcome, notes);
                    results.Add(testResult);
                    ConsoleReporter.PrintScenarioResult(testResult);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] Scenario {scenario.Id} threw: {ex.Message}");
                    var errorResult = new TestResult(scenario, null, TestOutcome.Error, ex.Message);
                    results.Add(errorResult);
                    ConsoleReporter.PrintScenarioResult(errorResult);
                }
            }
        }

        totalSw.Stop();
        ConsoleReporter.PrintSummary(results, totalSw.Elapsed);

        // Generate markdown report
        var report = MarkdownReportGenerator.Generate(
            categories, results, totalSw.Elapsed, claudeVersion, workDir);

        var reportPath = options.OutputPath;
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine($"\nReport written to: {reportPath}");
        FileLog.Write($"[Program] Report written to {reportPath}");

        return results.Any(r => r.Outcome == TestOutcome.Fail) ? 1 : 0;
    }

    private static async Task<int> RunTestSessionId()
    {
        Console.WriteLine("=== Testing ClaudeProcess.GetSessionIdAsync ===");
        Console.WriteLine();

        var claudePath = ClaudeRunner.FindClaudeOnPath();
        if (claudePath == null)
        {
            Console.Error.WriteLine("ERROR: claude.exe not found on PATH.");
            return 1;
        }
        Console.WriteLine($"Claude: {claudePath}");

        var workDir = Directory.GetCurrentDirectory();
        var sw = Stopwatch.StartNew();

        Console.WriteLine("Starting claude and capturing session ID...");
        var sessionId = await ClaudeProcess.GetSessionIdAsync(
            claudePath,
            "--dangerously-skip-permissions --max-turns 1 --model haiku",
            workDir,
            "Say just the word pong");

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  Session ID: {sessionId}");
        Console.WriteLine($"  Duration:   {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();
        Console.WriteLine("SUCCESS: ClaudeProcess.GetSessionIdAsync works.");
        return 0;
    }

    private static IReadOnlyList<ScenarioCategory> BuildAllCategories()
    {
        return new List<ScenarioCategory>
        {
            VersionAndHelpScenarios.Create(),
            PrintModeScenarios.Create(),
            OutputFormatScenarios.Create(),
            SystemPromptScenarios.Create(),
            ModelScenarios.Create(),
            ExecutionControlScenarios.Create(),
            ToolsAndPermissionsScenarios.Create(),
            JsonSchemaScenarios.Create(),
            SessionManagementScenarios.Create(),
            DirectoryAndSettingsScenarios.Create(),
            DebugScenarios.Create(),
            InitAndMaintenanceScenarios.Create(),
            AgentAndMcpScenarios.Create(),
            CombinationScenarios.Create(),
        };
    }

    private static async Task<string> GetClaudeVersion(string claudePath)
    {
        FileLog.Write("[Program] GetClaudeVersion: querying...");
        try
        {
            var runner = new ClaudeRunner(claudePath, Directory.GetCurrentDirectory(), timeoutMs: 10_000);
            var result = await runner.RunAsync("--version");
            var version = result.Stdout.Trim();
            if (string.IsNullOrEmpty(version))
                version = result.Stderr.Trim();
            FileLog.Write($"[Program] GetClaudeVersion: {version}");
            return string.IsNullOrEmpty(version) ? "unknown" : version;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] GetClaudeVersion FAILED: {ex.Message}");
            return "unknown";
        }
    }

    private static string ResolveResourcePaths(string arguments, string resourceDir)
    {
        // Replace "Resources/" references with absolute paths to the output Resources directory
        return arguments.Replace("Resources/", resourceDir + Path.DirectorySeparatorChar);
    }

    private static TestOutcome EvaluateOutcome(TestScenario scenario, RunResult result)
    {
        if (result.TimedOut)
            return TestOutcome.Fail;

        // If we expect a specific exit code and got it, pass
        if (scenario.ExpectedExitCode == result.ExitCode)
            return TestOutcome.Pass;

        // If expected exit code is -1 (unknown/error expected), any non-zero is acceptable
        if (scenario.ExpectedExitCode == -1 && result.ExitCode != 0)
            return TestOutcome.Pass;

        // Exit code mismatch
        return TestOutcome.Fail;
    }

    private static string? BuildNotes(TestScenario scenario, RunResult result, TestOutcome outcome)
    {
        if (result.TimedOut)
            return "Timed out";

        if (outcome == TestOutcome.Fail)
            return $"Expected exit={scenario.ExpectedExitCode}, got exit={result.ExitCode}";

        return null;
    }

    private static CliOptions? ParseArgs(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--category":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--category requires a value");
                        return null;
                    }
                    options.Category = args[++i];
                    break;

                case "--scenario":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--scenario requires a value");
                        return null;
                    }
                    options.ScenarioId = args[++i];
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    break;

                case "--skip-api":
                    options.SkipApi = true;
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--output requires a value");
                        return null;
                    }
                    options.OutputPath = args[++i];
                    break;

                case "--test-session-id":
                    options.TestSessionId = true;
                    break;

                case "--help":
                    PrintUsage();
                    return null;

                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: CcDirector.CliExplorer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --category <name>    Run only categories matching name");
        Console.WriteLine("  --scenario <id>      Run only a specific scenario by ID");
        Console.WriteLine("  --dry-run            Show all scenarios without executing");
        Console.WriteLine("  --skip-api           Skip scenarios that cost API credits");
        Console.WriteLine("  --output <path>      Markdown report path (default: cli-explorer-report.md)");
        Console.WriteLine("  --test-session-id    Test the ClaudeProcess.GetSessionIdAsync helper");
        Console.WriteLine("  --help               Show this help");
    }

    private sealed class CliOptions
    {
        public string? Category { get; set; }
        public string? ScenarioId { get; set; }
        public bool DryRun { get; set; }
        public bool SkipApi { get; set; }
        public bool TestSessionId { get; set; }
        public string OutputPath { get; set; } = "cli-explorer-report.md";
    }
}
