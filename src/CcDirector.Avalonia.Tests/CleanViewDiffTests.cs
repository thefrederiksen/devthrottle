using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for the Clean view incremental-update decision logic (#5). These pin the
/// behaviour that stops the transcript from being torn down and rebuilt every poll tick:
///   - identical content does nothing,
///   - a pure tail append only adds the new cards,
///   - any in-place change (e.g. a pending tool gaining a result) forces a full rebuild.
/// </summary>
public class CleanViewDiffTests
{
    private static string[] Sigs(params string[] s) => s;

    // ---- Classify ----------------------------------------------------------

    [Fact]
    public void Classify_IdenticalLists_ReturnsNone()
    {
        var cur = Sigs("a", "b", "c");
        var inc = Sigs("a", "b", "c");

        var verdict = CleanViewDiff.Classify(cur, inc, out _);

        Assert.Equal(CleanViewDiff.Update.None, verdict);
    }

    [Fact]
    public void Classify_BothEmpty_ReturnsNone()
    {
        var verdict = CleanViewDiff.Classify(Sigs(), Sigs(), out _);
        Assert.Equal(CleanViewDiff.Update.None, verdict);
    }

    [Fact]
    public void Classify_EmptyCurrent_NonEmptyIncoming_AppendsFromZero()
    {
        var verdict = CleanViewDiff.Classify(Sigs(), Sigs("a", "b"), out int appendFrom);

        Assert.Equal(CleanViewDiff.Update.Append, verdict);
        Assert.Equal(0, appendFrom);
    }

    [Fact]
    public void Classify_PureAppend_ReturnsAppendFromTail()
    {
        var cur = Sigs("a", "b");
        var inc = Sigs("a", "b", "c", "d");

        var verdict = CleanViewDiff.Classify(cur, inc, out int appendFrom);

        Assert.Equal(CleanViewDiff.Update.Append, verdict);
        Assert.Equal(2, appendFrom);
    }

    [Fact]
    public void Classify_LastItemChanged_SameCount_ReturnsRebuild()
    {
        // The classic case: a pending tool widget gains its result, so the last
        // signature changes in place. Must rebuild, not append.
        var cur = Sigs("a", "b", "tool-pending");
        var inc = Sigs("a", "b", "tool-with-result");

        var verdict = CleanViewDiff.Classify(cur, inc, out _);

        Assert.Equal(CleanViewDiff.Update.Rebuild, verdict);
    }

    [Fact]
    public void Classify_MiddleItemChanged_ReturnsRebuild()
    {
        var cur = Sigs("a", "b", "c");
        var inc = Sigs("a", "B", "c", "d");

        var verdict = CleanViewDiff.Classify(cur, inc, out _);

        Assert.Equal(CleanViewDiff.Update.Rebuild, verdict);
    }

    [Fact]
    public void Classify_IncomingShorter_ReturnsRebuild()
    {
        // A rewind shortens history: not an append, so rebuild.
        var cur = Sigs("a", "b", "c", "d");
        var inc = Sigs("a", "b");

        var verdict = CleanViewDiff.Classify(cur, inc, out _);

        Assert.Equal(CleanViewDiff.Update.Rebuild, verdict);
    }

    [Fact]
    public void Classify_ChangedTailEvenWithSamePrefix_ReturnsRebuild()
    {
        // Prefix matches but the new list does not strictly extend the old one
        // (the overlapping last item differs): rebuild.
        var cur = Sigs("a", "b", "c");
        var inc = Sigs("a", "b", "x", "y");

        var verdict = CleanViewDiff.Classify(cur, inc, out _);

        Assert.Equal(CleanViewDiff.Update.Rebuild, verdict);
    }

    // ---- Signature ---------------------------------------------------------

    [Fact]
    public void Signature_SameFields_AreEqual()
    {
        var a = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "Claude", Content = "hello" };
        var b = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "Claude", Content = "hello" };

        Assert.Equal(CleanViewDiff.Signature(a), CleanViewDiff.Signature(b));
    }

    [Fact]
    public void Signature_DifferentContent_Differs()
    {
        var a = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "Claude", Content = "hello" };
        var b = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "Claude", Content = "world" };

        Assert.NotEqual(CleanViewDiff.Signature(a), CleanViewDiff.Signature(b));
    }

    [Fact]
    public void Signature_PendingThenResult_Differs()
    {
        var pending = new CleanWidgetViewModel { Kind = WidgetKind.Bash, Header = "Terminal", Content = "ls", IsPending = true };
        var done = new CleanWidgetViewModel { Kind = WidgetKind.Bash, Header = "Terminal", Content = "ls", IsPending = false, Result = "file.txt" };

        Assert.NotEqual(CleanViewDiff.Signature(pending), CleanViewDiff.Signature(done));
    }

    [Fact]
    public void Signature_FieldBoundaries_DoNotCollide()
    {
        // Header "a" + Content "b" must not collide with Header "ab" + Content "".
        var a = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "a", Content = "b" };
        var b = new CleanWidgetViewModel { Kind = WidgetKind.Text, Header = "ab", Content = "" };

        Assert.NotEqual(CleanViewDiff.Signature(a), CleanViewDiff.Signature(b));
    }

    // ---- SelectResponseOnly (Wingman tab: final answer only, no chat, junk removed) ----

    [Fact]
    public void SelectResponseOnly_KeepsOnlyFinalAnswer_DropsPromptNarrationAndJunk()
    {
        // A single turn: our prompt, an opening "Let me start..." narration line, a pile of
        // tool calls, then the actual final reply. Only the final reply survives - no prompt
        // echo, no narration, no tool/bash/thinking cards.
        var kinds = new[]
        {
            WidgetKind.UserMessage, // 0 - our prompt (dropped: no chat)
            WidgetKind.Text,        // 1 - "I'll start by..." narration (dropped)
            WidgetKind.Bash,        // 2 - junk
            WidgetKind.Read,        // 3 - junk
            WidgetKind.Thinking,    // 4 - junk
            WidgetKind.Text,        // 5 - the actual answer (kept)
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 5 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_MultiBlockFinalAnswer_KeepsTheWholeTrailingRun()
    {
        // A final answer split across two adjacent text blocks (no tool between them):
        // keep both so nothing of the answer is lost.
        var kinds = new[]
        {
            WidgetKind.UserMessage, // 0
            WidgetKind.Bash,        // 1 - junk
            WidgetKind.Text,        // 2 - answer part 1 (kept)
            WidgetKind.Text,        // 3 - answer part 2 (kept)
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 2, 3 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_KeepsOnlyTheLatestTurnsAnswer()
    {
        // Two turns in the JSONL; only the second (latest) turn's answer should survive.
        var kinds = new[]
        {
            WidgetKind.UserMessage, // 0 - turn 1 prompt
            WidgetKind.Text,        // 1 - turn 1 reply (dropped: previous turn)
            WidgetKind.UserMessage, // 2 - turn 2 prompt (latest)
            WidgetKind.Bash,        // 3 - junk
            WidgetKind.Text,        // 4 - turn 2 reply (kept)
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 4 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_NewPromptAwaitingReply_ShowsNoStaleAnswer()
    {
        // The user just sent a prompt and Claude has not replied yet (last text precedes
        // the last user message): the previous turn's answer must be gone, not lingering.
        var kinds = new[]
        {
            WidgetKind.UserMessage, // 0
            WidgetKind.Text,        // 1 - previous answer (now stale)
            WidgetKind.UserMessage, // 2 - the just-sent prompt (latest)
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Empty(keep);
    }

    [Fact]
    public void SelectResponseOnly_NoUserMessageYet_KeepsTrailingText()
    {
        // Brand-new session that has only printed text (no prompt submitted): keep the
        // trailing answer text only, dropping the tool card.
        var kinds = new[] { WidgetKind.Text, WidgetKind.Bash, WidgetKind.Text };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 2 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_PendingQuestion_AlwaysKeptWithFinalAnswer()
    {
        // The orange "Claude is waiting" card must show through, alongside the answer.
        var kinds = new[]
        {
            WidgetKind.UserMessage,    // 0 - dropped
            WidgetKind.Text,           // 1 - answer (kept)
            WidgetKind.PendingQuestion // 2 - kept
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 1, 2 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_PendingQuestionWithNoAnswerYet_KeepsOnlyTheCard()
    {
        // Claude went straight to a pending question after a fresh prompt: just the card.
        var kinds = new[]
        {
            WidgetKind.UserMessage,    // 0
            WidgetKind.PendingQuestion // 1
        };

        var keep = CleanViewDiff.SelectResponseOnly(kinds);

        Assert.Equal(new[] { 1 }, keep);
    }

    [Fact]
    public void SelectResponseOnly_Empty_ReturnsEmpty()
    {
        var keep = CleanViewDiff.SelectResponseOnly(System.Array.Empty<WidgetKind>());

        Assert.Empty(keep);
    }
}
