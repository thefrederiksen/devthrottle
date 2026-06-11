using System.Diagnostics;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #334 - Source Control tab review: BuildTree and CompactFolders correctness
/// with large-repo fixture inputs (2000+ files).
/// </summary>
public class GitChangesViewTreeTests
{
    private static List<GitFileEntry> MakeEntries(int count, string dir, GitFileStatus status, bool isStaged)
    {
        var list = new List<GitFileEntry>(count);
        for (int i = 1; i <= count; i++)
        {
            var path = $"{dir}/file-{i}.cs";
            list.Add(new GitFileEntry
            {
                FilePath = path,
                FileName = $"file-{i}.cs",
                Status = status,
                StatusChar = status == GitFileStatus.Untracked ? "?" : "M",
                IsStaged = isStaged
            });
        }
        return list;
    }

    [Fact]
    public void BuildTree_50StagedInOneDir_SingleFolderNodeWith50Children()
    {
        var files = MakeEntries(50, "src/alpha", GitFileStatus.Added, isStaged: true);
        var tree = GitChangesView.BuildTree(files);

        // All 50 files are in src/alpha -> compacted to one folder node "src\alpha"
        Assert.Single(tree);
        var folder = tree[0] as GitFolderNode;
        Assert.NotNull(folder);
        Assert.Equal(50, folder.Children.Count);
        Assert.True(folder.Children.All(c => c is GitFileLeafNode));
    }

    [Fact]
    public void BuildTree_1950UntrackedAcross8Dirs_8FolderNodes()
    {
        var dirs = new[] { "src/beta", "src/gamma", "src/delta", "tests/unit", "tests/integration", "docs/api", "tools/build", "config/dev" };
        var files = new List<GitFileEntry>();
        // 1950 / 8 dirs, spread evenly enough
        int perDir = 1950 / dirs.Length;
        int remaining = 1950 - perDir * dirs.Length;
        foreach (var dir in dirs)
            files.AddRange(MakeEntries(perDir + (remaining-- > 0 ? 1 : 0), dir, GitFileStatus.Untracked, isStaged: false));

        var sw = Stopwatch.StartNew();
        var tree = GitChangesView.BuildTree(files);
        sw.Stop();

        // Two top-level folders: "src", "tests", "docs", "tools", "config"
        Assert.NotEmpty(tree);

        // Total file count across entire tree should equal 1950
        int totalLeaves = CountLeaves(tree);
        Assert.Equal(1950, totalLeaves);

        // Performance: building tree for 1950 files should complete quickly
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"BuildTree for 1950 files took {sw.ElapsedMilliseconds}ms (expected < 1000ms)");
    }

    [Fact]
    public void BuildTree_2001MixedFiles_TotalLeafCountIsExact()
    {
        var files = new List<GitFileEntry>();
        // 50 staged
        files.AddRange(MakeEntries(50, "src/alpha", GitFileStatus.Added, isStaged: true));
        // 1950 untracked across dirs
        string[] dirs = { "src/beta", "src/gamma", "src/delta", "tests/unit", "tests/integration", "docs/api", "tools/build", "config/dev" };
        int unt = 0;
        for (int d = 0; d < dirs.Length && unt < 1950; d++)
        {
            int take = Math.Min(1950 - unt, 1950 / dirs.Length + 1);
            files.AddRange(MakeEntries(take, dirs[d], GitFileStatus.Untracked, isStaged: false));
            unt += take;
        }
        // 1 modified at root
        files.Add(new GitFileEntry
        {
            FilePath = "large-tracked.txt",
            FileName = "large-tracked.txt",
            Status = GitFileStatus.Modified,
            StatusChar = "M",
            IsStaged = false
        });

        var sw = Stopwatch.StartNew();
        var tree = GitChangesView.BuildTree(files);
        sw.Stop();

        int totalLeaves = CountLeaves(tree);
        Assert.Equal(2001, totalLeaves);

        // Performance gate
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"BuildTree for 2001 files took {sw.ElapsedMilliseconds}ms (expected < 2000ms)");
    }

    [Fact]
    public void BuildTree_FileAtRootLevel_AppearsAsDirectLeaf()
    {
        var files = new List<GitFileEntry>
        {
            new() { FilePath = "root-file.cs", FileName = "root-file.cs", Status = GitFileStatus.Modified, StatusChar = "M" },
            new() { FilePath = "src/nested.cs", FileName = "nested.cs", Status = GitFileStatus.Untracked, StatusChar = "?" }
        };

        var tree = GitChangesView.BuildTree(files);

        // root-file.cs should be a leaf at top level
        var rootLeaf = tree.OfType<GitFileLeafNode>()
            .FirstOrDefault(n => n.RelativePath == "root-file.cs");
        Assert.NotNull(rootLeaf);

        // src/nested.cs should be under a folder node
        var folderNode = tree.OfType<GitFolderNode>().FirstOrDefault();
        Assert.NotNull(folderNode);
        Assert.Single(folderNode.Children);
    }

    [Fact]
    public void CompactFolders_SingleChildFolders_AreCompacted()
    {
        // Arrange: a -> b -> c -> file.cs (three levels of single-child folders)
        var root = new GitFolderNode { DisplayName = "root", RelativePath = "" };
        var a = new GitFolderNode { DisplayName = "a", RelativePath = "a" };
        var b = new GitFolderNode { DisplayName = "b", RelativePath = "a\\b" };
        var c = new GitFolderNode { DisplayName = "c", RelativePath = "a\\b\\c" };
        var leaf = new GitFileLeafNode { DisplayName = "file.cs", RelativePath = "a/b/c/file.cs" };

        c.Children.Add(leaf);
        b.Children.Add(c);
        a.Children.Add(b);
        root.Children.Add(a);

        GitChangesView.CompactFolders(root);

        // a -> b -> c should be compacted to a single node "a\b\c"
        Assert.Single(root.Children);
        var compacted = root.Children[0] as GitFolderNode;
        Assert.NotNull(compacted);
        Assert.Equal("a\\b\\c", compacted.DisplayName);
        Assert.Single(compacted.Children);
        Assert.IsType<GitFileLeafNode>(compacted.Children[0]);
    }

    [Fact]
    public void CompactFolders_BranchingFolder_NotCompacted()
    {
        // Arrange: root -> src -> (alpha, beta) - src has two children so should NOT compact
        var root = new GitFolderNode { DisplayName = "root", RelativePath = "" };
        var src = new GitFolderNode { DisplayName = "src", RelativePath = "src" };
        var alpha = new GitFolderNode { DisplayName = "alpha", RelativePath = "src\\alpha" };
        var beta = new GitFolderNode { DisplayName = "beta", RelativePath = "src\\beta" };
        alpha.Children.Add(new GitFileLeafNode { DisplayName = "file1.cs" });
        beta.Children.Add(new GitFileLeafNode { DisplayName = "file2.cs" });
        src.Children.Add(alpha);
        src.Children.Add(beta);
        root.Children.Add(src);

        GitChangesView.CompactFolders(root);

        // src should NOT be compacted (has two children)
        Assert.Single(root.Children);
        var srcNode = root.Children[0] as GitFolderNode;
        Assert.NotNull(srcNode);
        Assert.Equal("src", srcNode.DisplayName);
        Assert.Equal(2, srcNode.Children.Count);
    }

    private static int CountLeaves(IEnumerable<GitTreeNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node is GitFileLeafNode)
                count++;
            else if (node is GitFolderNode folder)
                count += CountLeaves(folder.Children);
        }
        return count;
    }
}
