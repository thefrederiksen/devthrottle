using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// The rule, held over the source itself: nothing in the product's C# composes a path to an installed
/// tool out of a Director's own folder.
///
/// This is here for the readers that cannot be instantiated in a unit test - the session PATH the
/// Director stamps at launch, the Tools page's own-tools check inside a window class, and the macOS
/// terminal helper that only extracts on a Mac. Every one of them was a place the leak lived, and a
/// suite that could only reach the two readers with a convenient seam would pass while the other three
/// went on filling Director folders.
///
/// Both halves are checked, and the PRESENCE half is the one that carries the weight. A guard whose
/// pass condition is only "no bad string was found" reports the same green when the file is renamed,
/// moved or emptied, so each reader is also named and required to CALL the machine-root accessor. A
/// reader that stops calling it fails by name.
///
/// WHAT THE ABSENCE HALF ACTUALLY SEES, because it used to claim more than it could see. It scans
/// non-test <c>.cs</c> files under <c>src</c> and <c>tools</c>, a STATEMENT at a time, for a statement
/// that both names one of the four ways the product can produce a Director's folder
/// (<see cref="DirectorFolderProducers"/>) and quotes one of the installed folder names
/// (<see cref="InstalledFolderNames"/>). Until this was widened it recognised the
/// <c>InstanceHome</c> spelling only - so
/// <c>Path.Combine(CcStorage.Root(), "bin")</c>, which is the ORIGINAL LEAK'S OWN SHAPE and the whole
/// reason this rule exists, passed it green. <see cref="The_scan_catches_every_shape_that_reaches_a_Directors_folder"/>
/// holds every shape against it by hand so the claim above is answerable without running a sweep.
///
/// WHAT IT DOES NOT SEE, stated so the next reader spends their scepticism in the right place:
/// <list type="bullet">
/// <item>Anything outside C#. The Python toolbelt is not scanned; <c>tools/cc_storage/storage.py</c>
/// resolves <c>bin()</c> from LOCALAPPDATA and deliberately ignores the CC_DIRECTOR_ROOT setting, so
/// it is machine-root by construction rather than by this guard.</item>
/// <item>A fifth way of producing a Director's folder that nobody has added to the list below. The
/// list is the instrument; a producer missing from it is a blind spot, not a pass.</item>
/// <item>A composition assembled across two statements - a Director's folder into a local, the local
/// joined with the folder name later. Statements, not lines, is as far as source text goes.</item>
/// <item>A folder name that is not spelled as a literal in the same statement (a constant, an
/// interpolation, a name built at run time).</item>
/// </list>
/// </summary>
public sealed class ToolPathsNeverComeFromADirectorFolderTests
{
    /// <summary>
    /// The readers that must ask for the machine's tools, each with the call that proves it. Named one
    /// by one rather than scanned for, so a reader that disappears from the product fails here and is
    /// removed on purpose instead of silently ceasing to be covered.
    /// </summary>
    private static readonly (string File, string MustContain, string Why)[] Readers =
    {
        ("src/CcDirector.Core/Sessions/SessionManager.cs", "var ownToolBin = Storage.CcStorage.Bin();",
            "the PATH every session is launched with - the one that decides which cc-devthrottle an agent gets"),
        ("src/CcDirector.Avalonia/MainWindow.axaml.cs",
            "private static string OwnToolBinDir() => CcDirector.Core.Storage.CcStorage.Bin();",
            "the Tools page's check of whether the tool on PATH is the installed one"),
        ("src/CcDirector.Core/Git/CcWorktreesPool.cs", "Path.Combine(Storage.CcStorage.Bin(), ToolName)",
            "the worktree pool's search for cc-worktrees"),
        ("src/CcDirector.Core/UnixPty/PtyShim.cs", "var binDir = CcStorage.Bin();",
            "the macOS terminal helper, which extracts its shim into the tool directory"),
        ("src/CcDirector.Core/Browsers/BrowserHarnessInstaller.cs", "CcStorage.PythonRuntime()",
            "the browser harness, built from the bundled interpreter"),
        ("src/CcDirector.Core/Diagnostics/AboutInfo.cs", "Path.Combine(CcStorage.MachineRoot(), \"config\", \"setup\", \"installed.json\")",
            "what the About boxes report as installed"),
        ("tools/cc-director-setup-engine/InstallLayout.cs", "new(CcStorage.MachineRoot())",
            "where the installer and the tool reconciler put everything they install"),
    };

    /// <summary>
    /// The folder names that belong to the installed product. A Director's folder holds sessions,
    /// settings and logs and never one of these.
    /// </summary>
    private static readonly string[] InstalledFolderNames = { "\"bin\"", "\"pyenv\"", "\"python\"" };

    /// <summary>
    /// Every way the product can hand out a path that is a Director's own folder, with the name to
    /// print when one of them turns up beside an installed folder name.
    ///
    /// <c>Root()</c> and <c>Base()</c> belong here and are the reason this list exists: neither reads
    /// like a Director's folder, and both ARE one whenever the CC_DIRECTOR_ROOT setting points at one -
    /// which is to say, inside every Director and every session it starts. That is precisely how the
    /// leak was written the first time. <c>\bRoot\(\)</c> does not match inside <c>MachineRoot()</c>,
    /// because there is no word boundary in the middle of a word; the machine-root accessor is the
    /// answer to this rule, not a breach of it.
    /// </summary>
    private static readonly (Regex Pattern, string Name)[] DirectorFolderProducers =
    {
        (new Regex(@"\bInstanceHome\b", RegexOptions.Compiled),
            "InstanceContext.InstanceHome - this Director's own data folder"),
        (new Regex(@"\bRoot\(\)", RegexOptions.Compiled),
            "CcStorage.Root() - a Director's own folder whenever the CC_DIRECTOR_ROOT setting points at one, which is inside every Director and every session"),
        (new Regex(@"\bBase\(\)", RegexOptions.Compiled),
            "CcStorage.Base() - the raw setting, the same folder Root() hands out"),
        (new Regex(@"\bSharedRoot\b", RegexOptions.Compiled),
            "InstanceContext.SharedRoot - captured from CcStorage.Root(), so it is a Director's folder under the same conditions"),
    };

    [Fact]
    public void Every_reader_asks_for_the_machines_tools()
    {
        var root = GetRepoRoot();
        var broken = new List<string>();

        foreach (var (file, mustContain, why) in Readers)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                broken.Add($"{file}: the file is gone. It held {why}; find where that moved to and name it here.");
                continue;
            }

            if (!File.ReadAllText(path).Contains(mustContain, StringComparison.Ordinal))
                broken.Add($"{file}: expected to find `{mustContain}` - {why}.");
        }

        Assert.True(broken.Count == 0,
            "These readers no longer ask for the MACHINE's installed tools. Each one that looks in a "
            + "Director's own folder gives that Director a private copy of the tools, ageing at its own "
            + "pace, and whichever copy reaches the search path first answers for the whole machine:\n  "
            + string.Join("\n  ", broken));
    }

    [Fact]
    public void No_production_file_composes_a_tool_path_out_of_a_Directors_folder()
    {
        var root = GetRepoRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var area in new[] { "src", "tools" })
        {
            var areaRoot = Path.Combine(root, area);
            if (!Directory.Exists(areaRoot)) continue;

            foreach (var file in Directory.EnumerateFiles(areaRoot, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
                if (TestProjectPath.IsTestProject(relative)) continue;

                scanned++;
                offenders.AddRange(ToolPathsBuiltFromADirectorsFolder(relative, File.ReadAllLines(file)));
            }
        }

        // An empty result is only news when the instrument ran. A sweep that reached no files reports
        // the same nothing as a clean product, and that is the failure this whole class is about.
        Assert.True(scanned > 100,
            $"The scan read {scanned} production C# files, which is too few to have reached the "
            + "product. An absence found by an instrument that never looked is not an absence.");

        Assert.True(offenders.Count == 0,
            "These statements build a path to an installed tool out of a Director's own data folder. "
            + "Nothing installs there and nothing may look there - ask CcStorage.Bin() or "
            + "CcStorage.PythonRuntime() for the machine's copy:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The scan held against every shape by hand, so its reach is a fact rather than a claim.
    ///
    /// The second case is the one that matters. It is the probe an independent reviewer ran against
    /// the earlier, <c>InstanceHome</c>-only scan, where it passed GREEN - the original leak's own
    /// shape, walking through the guard that exists to stop it.
    ///
    /// Both widenings were watched failing before this was written, each with the producers or the
    /// statement-joining put back the way they were, built rather than re-run against an old binary:
    /// <list type="bullet">
    /// <item>Producers back to <c>InstanceHome</c> alone: <c>Assert.Equal() Failure: Values differ /
    /// Expected: 5 / Actual: 1</c> - one of the five shapes seen, which is the reviewer's probe result
    /// exactly.</item>
    /// <item>Back to a line at a time: <c>Expected: 5 / Actual: 4</c> - the wrapped
    /// <c>Path.Combine</c> is the one that disappears.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_scan_catches_every_shape_that_reaches_a_Directors_folder()
    {
        var probe = new[]
        {
            "var a = Path.Combine(Instances.InstanceContext.InstanceHome, \"bin\");",
            "var b = Path.Combine(CcStorage.Root(), \"bin\");",
            "var c = Path.Combine(Base(), \"pyenv\");",
            "var d = Path.Combine(InstanceContext.SharedRoot, \"instances\", slug, \"python\");",
            "var e = Path.Combine(",
            "    CcStorage.Root(),",
            "    \"python\");",
        };

        var found = ToolPathsBuiltFromADirectorsFolder("src/Probe.cs", probe);

        Assert.Equal(5, found.Count);
        Assert.Contains(found, f => f.Contains("InstanceHome", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("CcStorage.Root()", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("CcStorage.Base()", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("SharedRoot", StringComparison.Ordinal));
        // The multi-line composition, which a line-at-a-time scan cannot see at all. The statement is
        // reported rejoined, against the line it started on.
        Assert.Contains(found, f => f.Contains("src/Probe.cs:5: var e = Path.Combine( CcStorage.Root(), \"python\");", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of a usable instrument: what it must NOT say. A scan that cried wolf would be
    /// turned off, and then it would be worth nothing at all.
    /// </summary>
    [Fact]
    public void The_scan_leaves_alone_what_is_not_a_breach()
    {
        var innocent = new[]
        {
            "public static string Bin() => Path.Combine(MachineRoot(), \"bin\");",
            "public static string PythonRuntime() => Path.Combine(MachineRoot(), \"python\");",
            "public static string Config() => Path.Combine(Base(), \"config\");",
            "var home = Path.Combine(SharedRoot, \"instances\", slug);",
            "// never write Path.Combine(CcStorage.Root(), \"bin\") - ask CcStorage.Bin()",
            "/// <summary>The tools live in <c>bin</c>, never under Root().</summary>",
        };

        Assert.Empty(ToolPathsBuiltFromADirectorsFolder("src/Innocent.cs", innocent));
    }

    /// <summary>
    /// The scan itself, over the text of one file, so it can be held against a probe rather than only
    /// against a repository that is expected to be clean.
    ///
    /// It works a STATEMENT at a time, not a line at a time, because <c>Path.Combine</c> wraps: the
    /// lines are joined until a <c>;</c> closes them. Comments are dropped first, so a line that
    /// WRITES OUT the forbidden shape in order to forbid it - and the ones that do exist say exactly
    /// that - is not itself reported as a breach.
    /// </summary>
    private static List<string> ToolPathsBuiltFromADirectorsFolder(string relativePath, IReadOnlyList<string> lines)
    {
        var offenders = new List<string>();
        var statement = new System.Text.StringBuilder();
        var startLine = 0;
        var inBlockComment = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var code = StripComments(lines[i], ref inBlockComment);
            if (code.Trim().Length == 0) continue;

            if (statement.Length == 0) startLine = i + 1;
            statement.Append(' ').Append(code.Trim());

            if (!code.Contains(';', StringComparison.Ordinal)) continue;

            Report(statement.ToString(), startLine);
            statement.Clear();
        }

        if (statement.Length > 0) Report(statement.ToString(), startLine);

        return offenders;

        void Report(string text, int line)
        {
            if (!InstalledFolderNames.Any(n => text.Contains(n, StringComparison.Ordinal))) return;

            foreach (var (pattern, name) in DirectorFolderProducers)
            {
                if (!pattern.IsMatch(text)) continue;
                offenders.Add($"{relativePath}:{line}: {text.Trim()}  <- {name}");
                return;
            }
        }
    }

    /// <summary>
    /// Everything a compiler would ignore, removed. The <c>//</c> only counts when an even number of
    /// quotes precedes it, so a URL inside a string literal does not swallow the rest of the line.
    /// </summary>
    private static string StripComments(string line, ref bool inBlockComment)
    {
        if (inBlockComment)
        {
            var close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0) return string.Empty;
            inBlockComment = false;
            line = line[(close + 2)..];
        }

        var open = line.IndexOf("/*", StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = line.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                inBlockComment = true;
                return line[..open];
            }

            line = line[..open] + line[(close + 2)..];
        }

        var quotes = 0;
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quotes++;
            if (line[i] == '/' && line[i + 1] == '/' && quotes % 2 == 0) return line[..i];
        }

        return line;
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
