using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression tests for claude.exe's project-folder name encoding (issue #184 live
/// finding): claude replaces EVERY non-alphanumeric character with a dash. Our reader
/// previously missed dots, which made every transcript under a path containing a dot
/// (e.g. "...\.temp\brain-sandbox") invisible - AskAsync then waited forever on a
/// reply that had already landed.
/// </summary>
public class ClaudeProjectFolderEncodingTests
{
    [Theory]
    [InlineData(@"D:\Repos\my_project", "D--Repos-my-project")]
    [InlineData(@"D:\ReposFred\cc-director", "D--ReposFred-cc-director")]
    // The live #184 case: a dot in a path segment becomes a dash (".temp" -> "-temp").
    [InlineData(@"D:\ReposFred\cc-director\.temp\brain-sandbox", "D--ReposFred-cc-director--temp-brain-sandbox")]
    [InlineData(@"C:\Users\alice\AppData\Local\cc-director\brain", "C--Users-alice-AppData-Local-cc-director-brain")]
    // Spaces are non-alphanumeric too.
    [InlineData(@"D:\My Repos\app", "D--My-Repos-app")]
    public void GetProjectFolder_MatchesClaudeEncoding(string repoPath, string expected)
    {
        Assert.Equal(expected, ClaudeSessionReader.GetProjectFolder(repoPath));
    }

    [Fact]
    public void GetProjectFolder_ForwardSlashes_NormalizedLikeBackslashes()
    {
        Assert.Equal(
            ClaudeSessionReader.GetProjectFolder(@"D:\ReposFred\cc-director"),
            ClaudeSessionReader.GetProjectFolder("D:/ReposFred/cc-director"));
    }
}
