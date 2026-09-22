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
/// the Director that constructs a timer, a periodic timer or a file watcher, runs a loop until it
/// is cancelled, or runs any loop that sleeps, delays or waits between its turns, must have a row naming that file and the number of
/// such sites in it, and every row must still point at a file that has that many. A new timer
/// without a row fails the build; so does a row left behind after its job was removed. A row with
/// a site count of zero is a job that has moved onto the scheduler (BackgroundJobs) and constructs
/// nothing of its own.
///
/// THE PERIMETER IS NOT A LIST. It is every project the Director application links, read from
/// CcDirector.Avalonia.csproj's project references and theirs, so a project linked in tomorrow is
/// scanned tomorrow (the first review of this test found a retry loop in tools/cc-director-setup-engine
/// that a hand-kept list of six names could not see).
///
/// Presence, not absence (skill checks-that-fail-open): the scan must find the files this mission
/// measured, so a scan of the wrong tree cannot pass.
/// </summary>
public sealed class BackgroundWorkRegisterTests
{
    // Construction of a recurring mechanism. A comment line does not count. The initializer brace
    // may sit on the next line, so a bare "new DispatcherTimer" at the end of a line counts too.
    private static readonly Regex ConstructionPattern = new(
        @"new\s+(DispatcherTimer|System\.Threading\.Timer|Timer|PeriodicTimer|FileSystemWatcher)\s*(\(|\{|$)",
        RegexOptions.Compiled);

    // A loop that runs until it is cancelled is a standing job by its own declaration. ANY other
    // loop - while, do, for(;;) - is one when its body sleeps, delays or waits between turns: a
    // forever loop, a boolean-flag loop, a stopwatch-bounded loop, a state-condition loop alike.
    // A delay reached through a delegate (await _delay(...), await retryDelay(...)) is still a
    // delay, so anything named like one counts. The second review of this test found each of
    // those shapes standing in this tree with no row.
    private static readonly Regex UntilCancelledLoopPattern = new(
        @"while\s*\(\s*!\s*[\w\.]*IsCancellationRequested", RegexOptions.Compiled);
    private static readonly Regex AnyLoopPattern = new(
        @"(^|[\s;}])(while\s*\(|do\s*\{?\s*$|for\s*\(\s*;\s*;)", RegexOptions.Compiled);
    private static readonly Regex WaitInsideLoopPattern = new(
        @"[Dd]elay\w*\s*\(|Sleep\s*\(|Wait(One|Any|All)\s*\(|WaitForNextTickAsync\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ProjectReferencePattern = new(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.Compiled);

    // A register row: | Job | `src/Project/Path.cs` | 2 | ... - the file cell and the site count cell.
    private static readonly Regex RowPattern = new(@"^\|[^|]*\|\s*`((?:src|tools)/[^`]+\.cs)`\s*\|\s*(\d+)\s*\|", RegexOptions.Compiled);

    [Fact]
    public void EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne()
    {
        var root = FindRepositoryRoot();
        var registerPath = Path.Combine(root, "docs", "BackgroundWork.md");
        Assert.True(File.Exists(registerPath), $"the register is missing: {registerPath}");

        var projects = ProjectsTheApplicationLinks(Path.Combine(root, "src", "CcDirector.Avalonia", "CcDirector.Avalonia.csproj"));
        Assert.Contains(projects, p => p.EndsWith("CcDirector.Core.csproj", StringComparison.Ordinal));
        Assert.Contains(projects, p => p.EndsWith("CcDirector.Setup.Engine.csproj", StringComparison.Ordinal));

        var inCode = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var filesScanned = 0;
        foreach (var project in projects)
        {
            var dir = Path.GetDirectoryName(project)!;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                filesScanned++;
                var sites = CountSites(File.ReadAllLines(file));
                if (sites > 0)
                    inCode[rel] = sites;
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
                problems.Add($"NOT IN THE REGISTER: {file} has {sites} recurring mechanism(s) - add a row saying what it is, what triggers it, how often it may run and what switches it off");
            else if (registered != sites)
                problems.Add($"COUNT DIFFERS: {file} has {sites} site(s) in code, the register says {registered}");
        }
        foreach (var (file, registered) in inRegister)
        {
            // A row with zero sites is a job on the scheduler; a row claiming sites that are gone is stale.
            if (registered > 0 && !inCode.ContainsKey(file))
                problems.Add($"STALE ROW: the register lists {file} with {registered} site(s), and it constructs nothing any more - set the count to 0 if it moved onto the scheduler, or remove the row");
        }

        Assert.True(problems.Count == 0, "docs/BackgroundWork.md and the code disagree:\n" + string.Join("\n", problems));
    }

    /// <summary>Constructions, plus loops whose body waits. A comment line never counts.</summary>
    internal static int CountSites(string[] lines)
    {
        var sites = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (ConstructionPattern.IsMatch(line)) sites++;
            else if (UntilCancelledLoopPattern.IsMatch(line)) sites++;
            else if (AnyLoopPattern.IsMatch(line) && LoopBodyWaits(lines, i)) sites++;
        }
        return sites;
    }

    /// <summary>Walks the loop's braces from its header and says whether the body sleeps, delays or waits.</summary>
    private static bool LoopBodyWaits(string[] lines, int headerIndex)
    {
        var depth = 0;
        var opened = false;
        for (var i = headerIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            // Only text inside the body counts: from the opening brace on, and up to the closing one.
            var bodyStart = opened ? 0 : line.IndexOf('{');
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (ch == '{') { depth++; opened = true; }
                else if (ch == '}')
                {
                    depth--;
                    if (opened && depth == 0)
                        return bodyStart >= 0 && WaitInsideLoopPattern.IsMatch(line[bodyStart..c]);
                }
            }
            if (opened && bodyStart >= 0 && WaitInsideLoopPattern.IsMatch(line[bodyStart..])) return true;
            if (!opened && i > headerIndex + 2) return false; // a brace-less loop: one statement, no body to wait in
        }
        return false;
    }

    /// <summary>Every project the application links, transitively, from its own project references.</summary>
    internal static IReadOnlyList<string> ProjectsTheApplicationLinks(string applicationProject)
    {
        var seen = new List<string>();
        void Walk(string csproj)
        {
            var full = Path.GetFullPath(csproj);
            if (seen.Contains(full, StringComparer.Ordinal)) return;
            seen.Add(full);
            foreach (Match m in ProjectReferencePattern.Matches(File.ReadAllText(full)))
                Walk(Path.Combine(Path.GetDirectoryName(full)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
        }
        Walk(applicationProject);
        return seen;
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
