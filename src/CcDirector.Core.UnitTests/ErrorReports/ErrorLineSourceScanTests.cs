using System.Text.RegularExpressions;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3311: "every error the Director logs reaches the Gateway" is only true if the rule that recognises
/// an error line recognises every error line the code actually writes. A line the rule misses is still
/// written to the file, so nothing would ever say it went missing - review round 2 found 13 such Director
/// lines. This scans every FileLog.Write template in the projects that run a reporter and fails, naming
/// each one, when a template carries a marker word the rule would not see.
/// </summary>
public sealed class ErrorLineSourceScanTests
{
    // The projects whose processes run an ErrorReporter (the Director and the launcher) and the libraries
    // they log through.
    private static readonly string[] ScannedProjects =
    {
        "src/CcDirector.Core",
        "src/CcDirector.Avalonia",
        "src/CcDirector.Launcher",
        "src/CcDirector.ControlApi",
        "src/CcDirector.Engine",
        "src/CcDirector.Terminal.Avalonia",
        "src/CcDirector.TrayUi",
        "tools/cc-director-setup-engine",
    };

    private static readonly Regex Template = new(@"FileLog\.Write\(\s*\$?@?""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);
    private static readonly Regex Hole = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex MarkerWord = new(@"\b(FAILED|UNHANDLED|UNOBSERVED|FATAL|ERROR)\b", RegexOptions.CultureInvariant);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"could not find the repository root above {AppContext.BaseDirectory}; this test reads the source tree");
    }

    [Fact]
    public void EveryLoggedErrorTemplate_IsRecognisedAsAnError()
    {
        var root = RepositoryRoot();
        var scanned = 0;
        var withMarker = 0;
        var missed = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project);
            Assert.True(Directory.Exists(dir), $"scanned project not found: {dir}");
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var source = File.ReadAllText(file);
                foreach (Match m in Template.Matches(source))
                {
                    scanned++;
                    // What the line looks like at run time, as near as the source can say: every hole is a value.
                    var line = Hole.Replace(m.Groups[1].Value, "x").Replace("\\\"", "\"").Replace("\\n", "\n");
                    if (line.StartsWith(ErrorLine.ReporterTag, StringComparison.Ordinal)) continue;
                    var first = line.Split('\n')[0];
                    if (!MarkerWord.IsMatch(first)) continue;
                    withMarker++;
                    if (!ErrorLine.IsError(line))
                        missed.Add($"{Path.GetRelativePath(root, file)}: {first}");
                }
            }
        }

        // The scan must have found something, or a broken pattern would pass it (a check whose pass condition
        // is an absence).
        Assert.True(scanned > 1000, $"only {scanned} FileLog.Write templates found - the scan is not reading the source");
        Assert.True(withMarker > 100, $"only {withMarker} templates carry a marker word - the scan is not reading the source");
        Assert.True(missed.Count == 0,
            $"{missed.Count} logged error line(s) would never reach the Gateway. Put the marker where the rule sees " +
            "it - before the first \": \", or followed by \":\", \"(\" or \",\":\n" + string.Join("\n", missed));
    }
}
