using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Architecture fitness function: nothing in the setup surface picks a RELEASE ASSET for itself.
///
/// This guard exists because of how the defect was found, not because of the defect itself. Four
/// separate places wrote "Windows, or else macOS" by hand, and each one silently handed Linux
/// another platform's download - the Windows Director executable, or the macOS application bundle.
/// Three were fixed together; the fourth, in the Avalonia wizard that
/// <c>scripts/install-linux.sh</c> actually hands over to, was missed by the person fixing the other
/// three and caught by a reviewer. A pattern written by hand in several files gets written by hand
/// again, in a file the next reader does not think to open.
///
/// The rule is not "never test the operating system". Placement, autostart and user-interface
/// differences may branch on it freely. The rule is that choosing WHICH FILE TO DOWNLOAD goes
/// through <see cref="Component.AssetFor"/> or <see cref="PythonToolsInstaller.PythonAssetFor"/>,
/// which take the platform as a parameter and have no fall-through branch.
///
/// The Avalonia setup wizard has no test project of its own, so this guard is the only automated
/// thing standing between that file and a repeat.
/// </summary>
public sealed class PlatformAssetSelectionGuardTests
{
    /// <summary>The setup surface: everything that can resolve a release asset.</summary>
    private static readonly string[] ScannedDirectories =
    {
        "tools/cc-director-setup-engine",
        "tools/cc-director-setup-avalonia",
        "tools/cc-director-setup-cli",
    };

    /// <summary>
    /// The cross-platform CONSUMERS. These run on Windows, macOS and Linux, and must ask the
    /// registry rather than read a per-platform property. (The engine itself is excluded: it is
    /// where the properties are declared and where AssetFor is implemented.)
    /// </summary>
    private static readonly string[] ConsumerDirectories =
    {
        "tools/cc-director-setup-avalonia",
        "tools/cc-director-setup-cli",
    };

    /// <summary>A line naming one of these is choosing an asset.</summary>
    private static readonly string[] AssetTokens =
    {
        "WindowsAsset", "MacAsset", "LinuxAsset", "MacAppPlacer.DirectorAsset",
    };

    /// <summary>A line testing one of these is branching on the operating system.</summary>
    private static readonly string[] OsTestTokens =
    {
        "OperatingSystem.IsWindows()", "OperatingSystem.IsMacOS()", "OperatingSystem.IsLinux()",
    };

    /// <summary>
    /// The one per-platform asset name a consumer may still name directly:
    /// <c>MacAppPlacer.DirectorAsset</c>, inside the macOS-only application-bundle placement, which
    /// is a different install mechanism rather than a different filename. Every other direct read
    /// must go through <see cref="Component.AssetFor"/>.
    /// </summary>
    private const string AllowedDirectAssetName = "MacAppPlacer.DirectorAsset";

    [Fact]
    public void No_setup_file_selects_a_release_asset_with_a_two_way_os_test()
    {
        var offenders = new List<string>();
        var root = GetRepoRoot();

        foreach (var (file, index, line, code) in CodeLines(root, ScannedDirectories))
        {
            if (!AssetTokens.Any(t => line.Contains(t, StringComparison.Ordinal))) continue;
            if (!OsTestTokens.Any(t => line.Contains(t, StringComparison.Ordinal))) continue;
            offenders.Add($"{Relative(root, file)}:{index}: {code}");
        }

        Assert.True(offenders.Count == 0,
            "A release asset is being chosen by an operating-system test rather than by "
            + "Component.AssetFor(platform). On Linux that shape returns another platform's file, "
            + "downloads it, and reports success:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_wizard_and_the_command_line_ask_the_registry_rather_than_reading_a_platform_asset()
    {
        var offenders = new List<string>();
        var root = GetRepoRoot();

        foreach (var (file, index, line, code) in CodeLines(root, ConsumerDirectories))
        {
            if (line.Contains(AllowedDirectAssetName, StringComparison.Ordinal)) continue;
            if (!AssetTokens.Any(t => line.Contains(t, StringComparison.Ordinal))) continue;
            offenders.Add($"{Relative(root, file)}:{index}: {code}");
        }

        Assert.True(offenders.Count == 0,
            "The wizard and the setup command line run on three platforms, so they must call "
            + "Component.AssetFor(platform) instead of reading WindowsAsset / MacAsset / LinuxAsset "
            + "directly. Reading one of them is how a Linux install came to download "
            + "cc-director-win-x64.exe:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Both guards above pass trivially if they scan nothing, so prove the sweep actually reaches
    /// the files - including the one that carried the missed defect. An empty sweep is a broken
    /// instrument, not a clean result.
    /// </summary>
    [Fact]
    public void The_guards_actually_read_the_setup_surface_including_the_avalonia_wizard()
    {
        var root = GetRepoRoot();
        var all = CodeLines(root, ScannedDirectories).Select(l => Relative(root, l.File)).Distinct().ToList();
        var consumers = CodeLines(root, ConsumerDirectories).Select(l => Relative(root, l.File)).Distinct().ToList();

        Assert.Contains("tools/cc-director-setup-engine/Component.cs", all);
        Assert.Contains("tools/cc-director-setup-engine/ComponentRegistry.cs", all);
        Assert.Contains("tools/cc-director-setup-avalonia/Services/EngineInstallRunner.cs", all);
        Assert.Contains("tools/cc-director-setup-cli/Commands.cs", all);

        Assert.Contains("tools/cc-director-setup-avalonia/Services/EngineInstallRunner.cs", consumers);
        Assert.Contains("tools/cc-director-setup-cli/Commands.cs", consumers);

        Assert.True(all.Count > 20, $"only {all.Count} files scanned; the sweep is not reaching the setup surface");
    }

    private readonly record struct CodeLine(string File, int Index, string Line, string Code);

    /// <summary>Every non-comment line of every C# file under the given repo-relative directories.</summary>
    private static IEnumerable<CodeLine> CodeLines(string root, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            var full = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var rel = Relative(root, file);
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i].TrimStart();
                    // Prose is exempt, including this rule's own explanations of the banned shape.
                    if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                    yield return new CodeLine(file, i + 1, lines[i], code);
                }
            }
        }
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
