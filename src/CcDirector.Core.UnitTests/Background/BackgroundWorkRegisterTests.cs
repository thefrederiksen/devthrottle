using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Background;

/// <summary>
/// A RECURRING JOB MAY NOT EXIST WITHOUT A ROW IN THE REGISTER (Director Optimizer mission,
/// devthrottle_internal #2237, Stage 3).
///
/// On 22 September 2026 one Director ran 808,945 git commands in fourteen hours, 51,864 of them
/// network calls, flat all night with nobody working, because nothing said how often any of its
/// background work may run and nothing noticed when a loop started feeding itself. The register
/// at docs/BackgroundWork.md is the one place that says what each recurring job costs, what
/// triggers it, how often it may run, how stale its answer may be, which thread it runs on and
/// what switches it off. This test keeps the register and the code in step: every source file in
/// the Director that constructs a timer, a periodic timer, a file watcher, or runs a cancellation
/// polling loop must have a row naming that file and the number of such sites in it, and every
/// row must still point at a file that has that many. A new timer without a row fails the build;
/// so does a row left behind after its job was removed.
///
/// Presence, not absence (skill checks-that-fail-open): the scan must find the files this mission
/// measured, so a scan of the wrong tree cannot pass.
/// </summary>
public sealed class BackgroundWorkRegisterTests
{
    // Construction of a recurring mechanism. A comment line does not count.
    private static readonly Regex SitePattern = new(
        // The initializer brace may sit on the next line, so a bare "new DispatcherTimer" at the end
        // of a line counts too.
        @"new\s+(DispatcherTimer|System\.Threading\.Timer|Timer|PeriodicTimer|FileSystemWatcher)\s*(\(|\{|$)|" +
        @"while\s*\(\s*!\s*[\w\.]*(IsCancellationRequested)",
        RegexOptions.Compiled);

    private static readonly string[] DirectorProjects =
    {
        "CcDirector.Core", "CcDirector.Avalonia", "CcDirector.ControlApi", "CcDirector.Engine",
        "CcDirector.Terminal.Avalonia", "CcDirector.Terminal.Core",
    };

    // A register row: | Job | `src/Project/Path.cs` | 2 | ... - the file cell and the site count cell.
    private static readonly Regex RowPattern = new(@"^\|[^|]*\|\s*`(src/[^`]+\.cs)`\s*\|\s*(\d+)\s*\|", RegexOptions.Compiled);

    [Fact]
    public void EveryTimerWatcherAndPollingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne()
    {
        var root = FindRepositoryRoot();
        var registerPath = Path.Combine(root, "docs", "BackgroundWork.md");
        Assert.True(File.Exists(registerPath), $"the register is missing: {registerPath}");

        var inCode = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var filesScanned = 0;
        foreach (var project in DirectorProjects)
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                filesScanned++;
                var sites = File.ReadLines(file)
                    .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    .Count(line => SitePattern.IsMatch(line));
                if (sites > 0)
                    inCode[Path.GetRelativePath(root, file).Replace('\\', '/')] = sites;
            }
        }

        // The scan read the real tree: the loop this mission started from, and the terminal's own poll.
        Assert.True(filesScanned > 500, $"expected the Director's sources, scanned {filesScanned} files under {root}");
        Assert.Contains("src/CcDirector.Core/Git/RepositoryWatcher.cs", inCode.Keys);
        Assert.Contains("src/CcDirector.Terminal.Avalonia/TerminalControl.cs", inCode.Keys);

        var inRegister = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(registerPath))
        {
            var m = RowPattern.Match(line);
            if (!m.Success) continue;
            var file = m.Groups[1].Value;
            inRegister[file] = inRegister.TryGetValue(file, out var have) ? have + int.Parse(m.Groups[2].Value) : int.Parse(m.Groups[2].Value);
        }
        Assert.True(inRegister.Count > 0, "the register has no rows this test can read (a row's second cell is the file in backticks, the third its site count)");

        var problems = new List<string>();
        foreach (var (file, sites) in inCode)
        {
            if (!inRegister.TryGetValue(file, out var registered))
                problems.Add($"NOT IN THE REGISTER: {file} constructs {sites} recurring mechanism(s) - add a row saying what it is, what triggers it, how often it may run and what switches it off");
            else if (registered != sites)
                problems.Add($"COUNT DIFFERS: {file} has {sites} site(s) in code, the register says {registered}");
        }
        foreach (var (file, _) in inRegister)
        {
            if (!inCode.ContainsKey(file))
                problems.Add($"STALE ROW: the register lists {file}, which constructs nothing any more - remove the row");
        }

        Assert.True(problems.Count == 0, "docs/BackgroundWork.md and the code disagree:\n" + string.Join("\n", problems));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Core")) && Directory.Exists(Path.Combine(dir.FullName, "docs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find the repository root above " + AppContext.BaseDirectory);
    }
}
