using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// The rule, held over the source itself: nothing in the product composes a path to an installed tool
/// out of a Director's own folder.
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

        foreach (var area in new[] { "src", "tools" })
        {
            var areaRoot = Path.Combine(root, area);
            if (!Directory.Exists(areaRoot)) continue;

            foreach (var file in Directory.EnumerateFiles(areaRoot, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
                if (TestProjectPath.IsTestProject(relative)) continue;

                foreach (var line in File.ReadAllLines(file))
                {
                    if (!line.Contains("InstanceHome", StringComparison.Ordinal)) continue;
                    if (!InstalledFolderNames.Any(n => line.Contains(n, StringComparison.Ordinal))) continue;

                    offenders.Add($"{relative}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These lines build a path to an installed tool out of a Director's own data folder. Nothing "
            + "installs there and nothing may look there - ask CcStorage.Bin() or "
            + "CcStorage.PythonRuntime() for the machine's copy:\n  " + string.Join("\n  ", offenders));
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
