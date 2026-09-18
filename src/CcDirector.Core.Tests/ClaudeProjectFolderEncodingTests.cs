using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression tests for claude.exe's project-folder name encoding (issue #184 live
/// finding): claude replaces EVERY non-alphanumeric character with a dash. Our reader
/// previously missed dots, which made every transcript under a path containing a dot
/// (e.g. a ".temp/brain-sandbox" segment) invisible - AskAsync then waited forever on a
/// reply that had already landed.
///
/// THE CASES ARE PER PLATFORM. The encoding resolves its input with Path.GetFullPath, so a
/// drive-letter path is absolute on Windows and RELATIVE everywhere else - off Windows these cases
/// were being resolved against the test runner's working directory, so all five failed on a Mac for
/// the input rather than for the encoding. The rule is identical on both platforms; only what counts
/// as an absolute path differs.
/// </summary>
public class ClaudeProjectFolderEncodingTests
{
    public static IEnumerable<object[]> EncodingCases()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new object[] { @"D:\Repos\my_project", "D--Repos-my-project" };
            yield return new object[] { @"C:\repos\cc-director", "C--repos-cc-director" };
            // The live #184 case: a dot in a path segment becomes a dash (".temp" -> "-temp").
            yield return new object[] { @"C:\repos\cc-director\.temp\brain-sandbox", "C--repos-cc-director--temp-brain-sandbox" };
            yield return new object[] { @"C:\Users\example\AppData\Local\cc-director\brain", "C--Users-example-AppData-Local-cc-director-brain" };
            // Spaces are non-alphanumeric too.
            yield return new object[] { @"D:\My Repos\app", "D--My-Repos-app" };
        }
        else
        {
            yield return new object[] { "/Repos/my_project", "-Repos-my-project" };
            yield return new object[] { "/repos/cc-director", "-repos-cc-director" };
            // The live #184 case: a dot in a path segment becomes a dash (".temp" -> "-temp").
            yield return new object[] { "/repos/cc-director/.temp/brain-sandbox", "-repos-cc-director--temp-brain-sandbox" };
            yield return new object[] { "/Users/example/Library/Application Support/cc-director/brain", "-Users-example-Library-Application-Support-cc-director-brain" };
            // Spaces are non-alphanumeric too.
            yield return new object[] { "/My Repos/app", "-My-Repos-app" };
        }
    }

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void GetProjectFolder_MatchesClaudeEncoding(string repoPath, string expected)
    {
        Assert.Equal(expected, ClaudeSessionReader.GetProjectFolder(repoPath));
    }

    [Fact]
    public void GetProjectFolder_ForwardSlashes_NormalizedLikeBackslashes()
    {
        // Windows accepts either separator for the same directory, so both must encode alike. There is
        // no such pair off Windows - a backslash is an ordinary character in a Unix file name, and
        // treating it as a separator there would be wrong - so the equivalence is asserted where it
        // exists and the encoding of the platform's own separator is asserted everywhere.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                ClaudeSessionReader.GetProjectFolder(@"C:\repos\cc-director"),
                ClaudeSessionReader.GetProjectFolder("C:/repos/cc-director"));
        }
        else
        {
            Assert.Equal("-repos-cc-director", ClaudeSessionReader.GetProjectFolder("/repos/cc-director"));
        }
    }
}
