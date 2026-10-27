using CcDirector.Core.Licenses;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The Loud When Stranded mission, step 6: a session stopped on a usage limit is recognised from its transcript's last
/// conversation record, and from nothing else.
/// </summary>
public sealed class UsageLimitTranscriptTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private LastTurn Read(params string[] lines) => UsageLimitTranscript.ReadLastTurn(LimitTranscripts.Write(_folder.Path, "t", lines));

    [Fact]
    public void ReadLastTurn_LimitReplyEndsTheTurn_ReturnsTheLimitRecord()
    {
        var last = Read(LimitTranscripts.StoppedOnTheLimit());

        Assert.Equal(LastTurnKind.LimitReply, last.Kind);
        Assert.Equal(LimitTranscripts.LimitRecordUuid, last.RecordUuid);
        Assert.Equal(LimitTranscripts.LimitAtUtc, last.AtUtc);
    }

    [Fact]
    public void ReadLastTurn_TurnEndedNormally_ReturnsOther()
    {
        Assert.Equal(LastTurnKind.Other, Read(LimitTranscripts.EndedNormally()).Kind);
    }

    [Fact]
    public void ReadLastTurn_PromptAfterTheLimitReply_ReturnsOther()
    {
        // Session 102 after the owner's hand restart: the limit reply, then a typed "continue".
        var lines = LimitTranscripts.StoppedOnTheLimit()
            .Append(LimitTranscripts.UserPrompt("continue", LimitTranscripts.LimitAtUtc.AddMinutes(9)))
            .ToArray();

        Assert.Equal(LastTurnKind.Other, Read(lines).Kind);
    }

    [Fact]
    public void ReadLastTurn_AnotherApiError_ReturnsOther()
    {
        Assert.Equal(LastTurnKind.Other, Read(LimitTranscripts.LimitReply(apiError: "overloaded")).Kind);
    }

    [Fact]
    public void ReadLastTurn_SideChainRecordAfterTheLimitReply_IsPassedOver()
    {
        var sideChain = LimitTranscripts.UserPrompt("a sub-agent's prompt", LimitTranscripts.LimitAtUtc.AddSeconds(1))
            .Replace("\"isSidechain\":false", "\"isSidechain\":true");

        Assert.Equal(LastTurnKind.LimitReply, Read(LimitTranscripts.LimitReply(), sideChain).Kind);
    }

    [Fact]
    public void ReadLastTurn_HalfWrittenLastLine_IsPassedOver()
    {
        Assert.Equal(LastTurnKind.LimitReply, Read(LimitTranscripts.LimitReply(), "{\"type\":\"user\",\"mess").Kind);
    }

    [Fact]
    public void ReadLastTurn_LastRecordLongerThanTheFirstTail_IsStillRead()
    {
        var huge = LimitTranscripts.UserPrompt(new string('x', UsageLimitTranscript.FirstTailBytes + 1000), LimitTranscripts.LimitAtUtc.AddSeconds(5));

        Assert.Equal(LastTurnKind.Other, Read(LimitTranscripts.LimitReply(), huge).Kind);
    }

    [Fact]
    public void ReadLastTurn_TranscriptNotWrittenYet_ReturnsNoConversation()
    {
        var last = UsageLimitTranscript.ReadLastTurn(Path.Combine(_folder.Path, "absent.jsonl"));

        Assert.Equal(LastTurnKind.NoConversation, last.Kind);
        Assert.Contains("has not been written yet", last.Reason);
    }

    [Fact]
    public void ReadLastTurn_EmptyTranscript_ReturnsNoConversation()
    {
        var path = Path.Combine(_folder.Path, "empty.jsonl");
        File.WriteAllText(path, "");

        Assert.Equal(LastTurnKind.NoConversation, UsageLimitTranscript.ReadLastTurn(path).Kind);
    }

    [Fact]
    public void ReadLastTurn_NoConversationRecordYet_ReturnsNoConversation()
    {
        Assert.Equal(LastTurnKind.NoConversation, Read(LimitTranscripts.TurnDuration(LimitTranscripts.LimitAtUtc)).Kind);
    }

    [Fact]
    public void ReadLastTurn_BrokenLineBeforeTheEnd_ReturnsUnreadable()
    {
        Assert.Equal(LastTurnKind.Unreadable, Read("{not json", LimitTranscripts.TurnDuration(LimitTranscripts.LimitAtUtc)).Kind);
    }
}
