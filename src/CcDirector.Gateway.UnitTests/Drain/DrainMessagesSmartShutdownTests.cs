using CcDirector.ControlApi.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The smart shutdown's words (<see cref="DrainMessages.SmartShutdown"/>,
/// <see cref="DrainMessages.HandOverNow"/>, <see cref="DrainMessages.RestartIsOff"/>), and the older
/// message left as it was.
///
/// What these cannot show: that the ENGINE sends them. There is no smart shutdown engine yet - it is the
/// next task - so nothing in the product calls these three methods today. These tests hold the words; the
/// engine's tests will have to hold that the words go out.
/// </summary>
public class DrainMessagesSmartShutdownTests
{
    private const string Path = "C:/handovers/run-1/solo--A session.md";

    private static readonly IReadOnlyList<(string Name, string Path)> Nobody = Array.Empty<(string, string)>();

    private static readonly IReadOnlyList<(string Name, string Path)> TwoUnder = new[]
    {
        ("Manager", "C:/handovers/run-1/mgr--Manager.md"),
        ("Worker", "C:/handovers/run-1/w1--Worker.md"),
    };

    [Fact]
    public void SmartShutdown_Message_SaysHowLongThereIsAndToWriteTheNextActionFirst()
    {
        var text = DrainMessages.SmartShutdown("DevThrottle_1", Path, Nobody, "update to 2.9.0", TimeSpan.FromMinutes(10));

        Assert.Contains("YOU HAVE 10 MINUTES", text);
        Assert.Contains("WRITE THE EXACT NEXT ACTION FIRST", text);
        Assert.Contains("whatever is on disk when time is up is already useful", text);
        Assert.Contains("DevThrottle_1", text);
        Assert.Contains("(update to 2.9.0)", text);
        Assert.Contains($"\"{Path}\"", text);
    }

    [Fact]
    public void SmartShutdown_Message_NeverPromisesTheSessionWillNotBeKilled()
    {
        var text = DrainMessages.SmartShutdown("DevThrottle_1", Path, TwoUnder, null, TimeSpan.FromMinutes(5));

        // The smart shutdown DOES end what is still present at the limit. The older message's promise
        // would be a lie here, and a session that believed it would not hurry.
        Assert.DoesNotContain("WILL NOT BE KILLED", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing will be forced", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will not happen", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is shut down", text);
    }

    [Fact]
    public void SmartShutdown_Message_KeepsThePhrasesLearnedOnARealFleet()
    {
        var text = DrainMessages.SmartShutdown(null, Path, Nobody, null, TimeSpan.FromMinutes(60));

        Assert.Contains("START NOTHING NEW", text);
        Assert.Contains("NO SECRETS", text);
        Assert.Contains("cc-devthrottle skill get move-session", text);
        Assert.Contains("NOT the local /handover skill", text);
        Assert.Contains("YOU HAVE 60 MINUTES", text);
        Assert.StartsWith("This is your Director. This Director is shutting down", text);
    }

    [Fact]
    public void SmartShutdown_Message_DescribesTheClosingBlockInExactlyTheOlderMessagesWords()
    {
        var older = DrainMessages.Drain("D", Path, "C:/handovers/run-1", TwoUnder, null);
        var smart = DrainMessages.SmartShutdown("D", Path, TwoUnder, null, TimeSpan.FromMinutes(10));

        // One parser (DrainReportBlock) reads the block whichever message asked for it, so there is one
        // description of it. The same holds for the paragraph telling a lead the exact paths its own seats
        // write to. If somebody edits either paragraph in one message only, this goes red.
        Assert.Equal(
            Between(older, "End your document with this block", "Close the comment. "),
            Between(smart, "End your document with this block", "Close the comment. "));
        Assert.Equal(
            Between(older, "You have seats reporting to you.", "in your block below. "),
            Between(smart, "You have seats reporting to you.", "in your block below. "));
    }

    [Fact]
    public void SmartShutdown_Message_ToASessionWithNobodyUnderIt_DoesNotMentionSeatsReportingToIt()
    {
        var text = DrainMessages.SmartShutdown("D", Path, Nobody, null, TimeSpan.FromMinutes(10));

        Assert.DoesNotContain("seats reporting to you", text);
    }

    [Fact]
    public void Drain_OlderMessage_StillPromisesNothingIsForced()
    {
        var text = DrainMessages.Drain("D", Path, "C:/handovers/run-1", Nobody, "update to 2.0.6");

        // The older path never forces, so it still says so. Adding the smart shutdown changed no word of it.
        Assert.Contains("YOU WILL NOT BE KILLED, nothing will be forced, and the restart simply will not happen", text);
        Assert.DoesNotContain("YOU HAVE", text);
        Assert.DoesNotContain("EXACT NEXT ACTION FIRST", text);
    }

    [Fact]
    public void HandOverNow_Message_IsTheShortSecondRequest_NextActionFirst()
    {
        var text = DrainMessages.HandOverNow(Path, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(20));

        Assert.Contains("HAND OVER NOW: THE EXACT NEXT ACTION FIRST", text);
        Assert.Contains("about 3 minutes are left", text);
        Assert.Contains($"\"{Path}\"", text);
        Assert.Contains("'drain-report'", text);
        Assert.Contains("NO SECRETS", text);
        Assert.DoesNotContain("WILL NOT BE KILLED", text, StringComparison.OrdinalIgnoreCase);

        // Short means short: well under half the first request.
        var first = DrainMessages.SmartShutdown("D", Path, Nobody, null, TimeSpan.FromMinutes(10));
        Assert.True(text.Length < first.Length / 2, $"second message is {text.Length} characters, first is {first.Length}");
    }

    [Fact]
    public void HandOverNow_Message_NamesEveryLineTheParserReads()
    {
        var text = DrainMessages.HandOverNow(Path, TimeSpan.FromMinutes(3));

        // A session under a lead may get its only description of the block from this message. Whatever it
        // leaves out is a fact that session is never asked for: a question for the owner, or that it is
        // blocked. Held to the parser's own list, so a key added there goes red here too.
        foreach (var key in DrainReportBlock.Keys)
            Assert.Contains($"'{key} ", text);

        // A session out of time must be offered something other than "drained", which reads as clean.
        Assert.Contains("'state: blocked'", text);
    }

    [Fact]
    public void HandOverNow_Message_ABlockWrittenAsItDescribes_ParsesWithNothingLeftOver()
    {
        var text = DrainMessages.HandOverNow(Path, TimeSpan.FromMinutes(3));

        // The other direction: every line the message quotes, written out as quoted, is one the parser
        // accepts. The quoted lines are taken FROM the message, not retyped here.
        var quoted = System.Text.RegularExpressions.Regex.Matches(text, @"'([a-z-]+: [^']*)'")
            .Select(m => m.Groups[1].Value)
            .Where(line => line != "restore: no" && line != "state: drained")
            .ToList();
        Assert.Equal(6, quoted.Count);

        var block = DrainReportBlock.Parse("<!-- drain-report\n" + string.Join("\n", quoted) + "\n-->");

        Assert.NotNull(block);
        Assert.Empty(block!.UnparsedLines);
        Assert.Equal("blocked", block.State);
        Assert.NotNull(block.BlockedReason);
        Assert.True(block.Restore);
        Assert.NotNull(block.Why);
        Assert.Single(block.Questions);
        Assert.Single(block.Covered);
    }

    [Fact]
    public void HandOverNow_Message_IsTellableApartFromTheFirstRequest()
    {
        var text = DrainMessages.HandOverNow(Path, TimeSpan.FromMinutes(3));

        // The first request is recognised on the rig by "START NOTHING NEW". The second must not carry it,
        // or a test could not tell a session that answered the first from one that answered only the second.
        Assert.DoesNotContain("START NOTHING NEW", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(-45)]
    public void HandOverNow_Message_WithUnderAMinuteLeft_SaysOneMinuteNeverZero(int seconds)
    {
        var text = DrainMessages.HandOverNow(Path, TimeSpan.FromSeconds(seconds));

        Assert.Contains("about 1 minute is left", text);
    }

    [Fact]
    public void RestartIsOff_Message_SaysTheRestartIsOffAndToCarryOn()
    {
        var text = DrainMessages.RestartIsOff("DevThrottle_1");

        Assert.Contains("DevThrottle_1", text);
        Assert.Contains("CANCELLED", text);
        Assert.Contains("THE RESTART IS OFF", text);
        Assert.Contains("you will not be closed", text);
        Assert.Contains("Carry on", text);
        Assert.DoesNotContain("START NOTHING NEW", text);

        Assert.Contains("this Director", DrainMessages.RestartIsOff(null));
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"\"{from}\" is not in the message");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"\"{to}\" does not follow \"{from}\" in the message");
        return text[start..(end + to.Length)];
    }
}
