using System.Text.RegularExpressions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.UnitTests.Wingman;

/// <summary>
/// The verdict vocabulary the CLIENT offers is the same list as the one the Gateway accepts (the
/// Wingman-on-every-turn mission, slice G).
///
/// WHY THIS GUARD EXISTS. Slice G puts a picker of verdict words on the verdict panel - the owner saying which
/// word he thinks was right - and a picker has to hold the words before he chooses one, so the list is in
/// TypeScript as well as here. Two lists of the same closed vocabulary drift apart by default, and the drift is
/// silent in the worst direction: the panel would offer a word the route refuses, which reads to the owner as the
/// report being broken, or it would quietly stop offering one, which reads as that reading being impossible. The
/// corpus then holds labels drawn from a narrower vocabulary than the one it is graded against, and every number
/// computed across the split compares two different questions.
///
/// It is the same protection the labelling tool in the internal repository has (tools/turn-log/verdicts.py,
/// pinned by a test there) - one list per place that must agree, and a test in each place that fails when they
/// do not.
///
/// IT RUNS IN THE DEFAULT GATE, deliberately. A guard whose whole value is catching an edit at the moment it is
/// made belongs in a project that actually runs, not in a parked suite that tells a developer nothing at commit
/// time.
/// </summary>
public sealed class TurnVerdictVocabularyReachesTheClientTests
{
    private const string ClientFile = "packages/client-core/src/sessions/verdictVocabulary.ts";

    [Fact]
    public void The_clients_picker_offers_exactly_the_vocabulary_the_gateway_accepts_in_the_same_order()
    {
        var words = WordsInTheClientList();

        // ORDER TOO, not just content. The picker renders them in the order the list gives, so a list that agreed
        // as a set but not as a sequence would put a different word first on the screen than the vocabulary calls
        // first - and the order here carries meaning: the six the Wingman may answer with, then the detector's.
        Assert.Equal(TurnVerdictVocabulary.AllVerdicts, words);
    }

    [Fact]
    public void The_client_list_holds_the_detectors_word_too_and_names_why()
    {
        var text = ClientText();

        // The Wingman may only ANSWER with six; the owner may correct TO the seventh, because his correction
        // becomes a label in the graded corpus and "that was never the end of a turn" is a reading none of the six
        // can express. A future edit that trimmed the list to the six would pass the test above only by changing
        // it too, so the reason is pinned where it is written down.
        Assert.Contains(TurnVerdictVocabulary.NotATurnEnd, text, StringComparison.Ordinal);
        Assert.Contains("detector", text, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> WordsInTheClientList()
    {
        var text = ClientText();
        var start = text.IndexOf("export const VERDICT_WORDS = [", StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"{ClientFile} no longer declares `export const VERDICT_WORDS = [`, so this guard cannot read the "
            + "list it exists to compare. Rename it back, or teach this test the new shape - do not delete the "
            + "guard, because the two lists then drift in silence.");

        var end = text.IndexOf(']', start);
        Assert.True(end > start, $"{ClientFile} has an unterminated VERDICT_WORDS list.");

        var body = text[(start + "export const VERDICT_WORDS = [".Length)..end];
        var words = Regex.Matches(body, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(words);
        return words;
    }

    private static string ClientText()
    {
        var path = Path.Combine(RepoRoot(), ClientFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path),
            $"{ClientFile} is missing. The verdict panel's picker reads it, so a missing file is a broken picker "
            + "rather than a guard with nothing to do.");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "packages", "client-core", "src", "sessions")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root from " + AppContext.BaseDirectory
            + " - no ancestor holds packages/client-core/src/sessions.");
    }
}
