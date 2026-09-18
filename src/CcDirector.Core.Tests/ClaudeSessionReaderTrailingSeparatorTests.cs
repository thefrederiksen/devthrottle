using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A TRAILING SEPARATOR ON THE REPO PATH MUST NOT CHANGE THE TRANSCRIPT FOLDER NAME.
///
/// The bug these pin, found live on 2026-07-27: a session whose repo path was stored with a trailing
/// separator (equivalent to a human, and invisible in every UI) resolved its Claude transcript folder
/// to a name with a TRAILING DASH. Every non-alphanumeric character becomes a dash here, and
/// Path.GetFullPath PRESERVES a trailing separator, so the separator became part of the name and named
/// a folder that does not exist.
///
/// What that cost, in order: the transcript lookup missed -> the Director's "turns" read returned
/// no_jsonl with an EMPTY widget list -> the Gateway's voice service found no assistant reply and
/// recorded "nothing to narrate" about a session that had just written a full answer -> it never
/// called the speech provider -> no audio ever existed -> and because the roster holds a voice
/// session yellow until audio is ready, the session sat on "Preparing voice" permanently, with no
/// error surfaced anywhere.
///
/// So this is not a cosmetic path-hygiene test. The folder name is the join between a session and
/// everything that reads its conversation, and it silently returned the wrong answer.
///
/// THE SAMPLE PATHS ARE CHOSEN PER PLATFORM, and they have to be. The encoding runs the path through
/// Path.GetFullPath, so a drive-letter path is ABSOLUTE on Windows and RELATIVE everywhere else - off
/// Windows it was being resolved against the test runner's working directory, which made every
/// expectation here unmeetable and every one of these tests red on a Mac. The rule under test (dash
/// every non-alphanumeric character, trim a trailing separator, keep a root's separator) is the same
/// rule on both, so each platform gets paths that are genuinely absolute for it.
/// </summary>
public class ClaudeSessionReaderTrailingSeparatorTests
{
    /// <summary>An absolute directory for this platform, and the folder name it must encode to.</summary>
    private static (string Path, string Encoded) Sample =>
        OperatingSystem.IsWindows()
            ? (@"D:\ReposFred\cc-consult", "D--ReposFred-cc-consult")
            : ("/ReposFred/cc-consult", "-ReposFred-cc-consult");

    /// <summary>The filesystem root for this platform, and the name it must encode to.</summary>
    private static (string Path, string Encoded) Root =>
        OperatingSystem.IsWindows() ? (@"D:\", "D--") : ("/", "-");

    public static IEnumerable<object[]> TrailingSeparatorPairs()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new object[] { @"D:\ReposFred\cc-consult", @"D:\ReposFred\cc-consult\" };
            yield return new object[] { @"D:\ReposFred\cc-consult", @"D:\ReposFred\cc-consult/" };
            yield return new object[] { @"C:\Users\example\.claude", @"C:\Users\example\.claude\" };
        }
        else
        {
            yield return new object[] { "/ReposFred/cc-consult", "/ReposFred/cc-consult/" };
            yield return new object[] { "/Users/example/.claude", "/Users/example/.claude/" };
        }
    }

    [Theory]
    [MemberData(nameof(TrailingSeparatorPairs))]
    public void GetProjectFolder_TrailingSeparator_MatchesTheSamePathWithout(string clean, string withSeparator)
    {
        Assert.Equal(
            ClaudeSessionReader.GetProjectFolder(clean),
            ClaudeSessionReader.GetProjectFolder(withSeparator));
    }

    [Fact]
    public void GetProjectFolder_TrailingSeparator_ProducesNoTrailingDash()
    {
        // The exact shape of the live defect: the name must not pick up a trailing dash.
        var (path, encoded) = Sample;
        var folder = ClaudeSessionReader.GetProjectFolder(path + Path.DirectorySeparatorChar);

        Assert.Equal(encoded, folder);
        Assert.False(folder.EndsWith('-'), $"folder name gained a trailing dash: {folder}");
    }

    [Fact]
    public void GetJsonlPath_TrailingSeparator_ResolvesToTheSameFile()
    {
        // The consumer that actually broke: SessionReadExecutor.Turns calls GetJsonlPath and treats a
        // missing file as "no_jsonl" + empty widgets, which downstream reads as "nothing to say".
        const string claudeSessionId = "adc79f6f-5230-46e5-af77-ed0aa6ca4ee7";
        var (path, _) = Sample;

        Assert.Equal(
            ClaudeSessionReader.GetJsonlPath(claudeSessionId, path),
            ClaudeSessionReader.GetJsonlPath(claudeSessionId, path + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GetProjectFolder_Root_KeepsItsSeparator()
    {
        // The reason this trims with TrimEndingDirectorySeparator rather than a bare TrimEnd: a ROOT
        // must keep its separator. On Windows "D:\" is a real directory whose sanitized name is "D--",
        // while "D:" means "the current directory on drive D:", which is a different place entirely.
        // On a Unix filesystem "/" is the root and a bare TrimEnd would leave an empty string. Either
        // way a bare TrimEnd silently retargets the root to somewhere else.
        var (path, encoded) = Root;

        Assert.Equal(encoded, ClaudeSessionReader.GetProjectFolder(path));
    }

    [Fact]
    public void GetProjectFolder_StillDashesEveryNonAlphanumeric()
    {
        // The behaviour that must NOT regress: dots, underscores, colons and separators all become
        // dashes (the issue #184 fix - a char-list version once missed dots and hid every transcript
        // under a dotted path).
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("D--Repos-my-project", ClaudeSessionReader.GetProjectFolder(@"D:\Repos\my_project"));
            Assert.Equal("D--Repos--temp-brain-sandbox", ClaudeSessionReader.GetProjectFolder(@"D:\Repos\.temp\brain-sandbox"));
        }
        else
        {
            Assert.Equal("-Repos-my-project", ClaudeSessionReader.GetProjectFolder("/Repos/my_project"));
            Assert.Equal("-Repos--temp-brain-sandbox", ClaudeSessionReader.GetProjectFolder("/Repos/.temp/brain-sandbox"));
        }
    }
}
