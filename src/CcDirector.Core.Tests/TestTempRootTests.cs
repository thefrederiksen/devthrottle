using System.Diagnostics;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The shared temporary-tree delete removes what a real git fixture leaves behind: read-only objects,
/// and directory links (a junction on Windows, a symbolic link elsewhere) - removed as links, with their
/// targets untouched.
/// </summary>
public sealed class TestTempRootTests
{
    [Fact]
    public void DeleteTree_removes_a_tree_holding_read_only_files_and_a_directory_link_and_leaves_the_links_target_alone()
    {
        var root = TestTempRoot.For("ccd-temproot-test-");
        var outside = TestTempRoot.For("ccd-temproot-outside-");
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        Directory.CreateDirectory(outside);
        try
        {
            var readOnly = Path.Combine(root, "inner", "object");
            File.WriteAllText(readOnly, "a git object is written read-only");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var outsideFile = Path.Combine(outside, "kept");
            File.WriteAllText(outsideFile, "the link's target must not be touched");
            File.SetAttributes(outsideFile, FileAttributes.ReadOnly);

            var linkInside = Path.Combine(root, "alias-inner");
            var linkOutside = Path.Combine(root, "alias-outside");
            LinkDirectory(linkInside, Path.Combine(root, "inner"));
            LinkDirectory(linkOutside, outside);
            Assert.True((File.GetAttributes(linkOutside) & FileAttributes.ReparsePoint) != 0, "the fixture should have made a directory link");

            TestTempRoot.DeleteTree(root);

            Assert.False(Directory.Exists(root), "the whole tree, links included, should be gone");
            Assert.True(File.Exists(outsideFile), "the link's target outside the tree must survive");
            Assert.True((File.GetAttributes(outsideFile) & FileAttributes.ReadOnly) != 0,
                "the read-only sweep must not reach through a link to the target's files");
        }
        finally
        {
            if (Directory.Exists(root)) TestTempRoot.DeleteTree(root);
            TestTempRoot.DeleteTree(outside);
        }
    }

    /// <summary>A junction on Windows (no elevation needed), a symbolic link elsewhere.</summary>
    private static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed (exit {process.ExitCode}): {error}");
    }
}
