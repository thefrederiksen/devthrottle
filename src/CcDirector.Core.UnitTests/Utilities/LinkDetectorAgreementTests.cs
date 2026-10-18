using System.Text.Json;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// Issue #4085: the Director's detector and the Chat's detector (packages/client-core historyLinks.ts) must
/// find the same paths. The Director builds a session's named-path set with this detector, called the way the
/// Chat calls its own (no repository root, no existence check); a path the Chat links that the Director does
/// not name is refused when clicked. Both sides read ONE list of cases, linkDetectorAgreement.json, so a case
/// added for one is enforced on the other.
/// </summary>
public sealed class LinkDetectorAgreementTests
{
    private sealed record AgreementCase(string Line, string[] Paths, string[] Urls);

    private static readonly Lazy<IReadOnlyList<AgreementCase>> Cases = new(Load);

    private static IReadOnlyList<AgreementCase> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "the repository root (cc-director.sln) was not found above the test assembly");
        var file = Path.Combine(dir!.FullName, "packages", "client-core", "src", "history", "linkDetectorAgreement.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new AgreementCase(
                c.GetProperty("line").GetString()!,
                c.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                c.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!).ToArray()))
            .ToList();
    }

    public static IEnumerable<object[]> Lines() => Cases.Value.Select(c => new object[] { c.Line });

    [Fact]
    public void Cases_areThere()
    {
        Assert.True(Cases.Value.Count > 10, $"only {Cases.Value.Count} agreement cases were read");
    }

    [Theory]
    [MemberData(nameof(Lines))]
    public void FindAllLinkMatches_onLinuxOrMacOS_findsWhatTheChatFinds(string line)
    {
        var expected = Cases.Value.Single(c => c.Line == line);

        var matches = LinkDetector.FindAllLinkMatches(line, repoPath: null, pathExistsCheck: null, posixPaths: true);

        Assert.Equal(expected.Paths, matches.Where(m => m.Type == LinkDetector.LinkType.Path).Select(m => m.Text).ToArray());
        Assert.Equal(expected.Urls, matches.Where(m => m.Type == LinkDetector.LinkType.Url).Select(m => m.Text).ToArray());
    }

    [Theory]
    [InlineData("open /tmp/x now")]
    [InlineData("The brief is at `/home/soren/briefs/q3.pdf`.")]
    public void FindAllLinkMatches_onWindows_doesNotNameAPosixPath(string line)
    {
        // On Windows "/home/x" is relative to the current drive, so the Director names nothing; the Chat cannot
        // know the Director's system, so it links it and the click shows the Director's refusal.
        var matches = LinkDetector.FindAllLinkMatches(line, repoPath: null, pathExistsCheck: null, posixPaths: false);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_posixPathWithAnExistenceCheck_isALinkOnlyWhenItExists()
    {
        // The terminal passes an existence check, so a POSIX-shaped string in its output is a link only when real.
        var matches = LinkDetector.FindAllLinkMatches("open /tmp/real.txt and /tmp/missing.txt", null,
            p => p == "/tmp/real.txt", posixPaths: true);

        Assert.Equal(new[] { "/tmp/real.txt" }, matches.Select(m => m.Text).ToArray());
    }
}
