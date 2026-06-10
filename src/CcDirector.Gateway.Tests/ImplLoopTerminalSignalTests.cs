using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="ImplLoopTerminalSignal"/> (issue #274, reads child 1's #272 contract):
/// parsing the IMPL-LOOP-TERMINAL sentinel block off a session transcript, correlating by issue,
/// and the three signal values. The block shape is DEVELOPMENT_METHOD.md Section 7a.
/// </summary>
public sealed class ImplLoopTerminalSignalTests
{
    private static string Block(int issue, string signal, string pr = "none", string merged = "no", string reason = "x") =>
        $"IMPL-LOOP-TERMINAL\nissue: {issue}\nsignal: {signal}\npr: {pr}\nmerged: {merged}\nreason: {reason}\n";

    [Fact]
    public void ParseLatest_DoneBlock_ParsesAllFields()
    {
        var transcript = "some prior output\n" + Block(274, "done", "281", "yes", "verified and merged");

        var sig = ImplLoopTerminalSignal.ParseLatest(transcript, 274);

        Assert.NotNull(sig);
        Assert.Equal(274, sig.Issue);
        Assert.Equal(ImplLoopSignal.Done, sig.Signal);
        Assert.Equal(281, sig.Pr);
        Assert.True(sig.Merged);
        Assert.Equal("verified and merged", sig.Reason);
    }

    [Fact]
    public void ParseLatest_NeedsHumanBlock_ParsesSignal_NotMerged()
    {
        var sig = ImplLoopTerminalSignal.ParseLatest(Block(99, "needs-human", "300", "no", "3-strike park"), 99);

        Assert.NotNull(sig);
        Assert.Equal(ImplLoopSignal.NeedsHuman, sig.Signal);
        Assert.Equal(300, sig.Pr);
        Assert.False(sig.Merged);
    }

    [Fact]
    public void ParseLatest_FailedBlock_ParsesSignal_PrNone()
    {
        var sig = ImplLoopTerminalSignal.ParseLatest(Block(5, "failed", "none", "no", "build tool crashed"), 5);

        Assert.NotNull(sig);
        Assert.Equal(ImplLoopSignal.Failed, sig.Signal);
        Assert.Null(sig.Pr);
        Assert.False(sig.Merged);
    }

    [Fact]
    public void ParseLatest_MergedYesOnNonDone_IsCoercedToFalse()
    {
        // merged: yes is only meaningful on done; a malformed block claiming merged on failed must
        // not be trusted.
        var sig = ImplLoopTerminalSignal.ParseLatest(Block(7, "failed", "none", "yes", "bogus"), 7);

        Assert.NotNull(sig);
        Assert.False(sig.Merged);
    }

    [Fact]
    public void ParseLatest_NoBlock_ReturnsNull()
    {
        Assert.Null(ImplLoopTerminalSignal.ParseLatest("just working, no sentinel yet", 274));
        Assert.Null(ImplLoopTerminalSignal.ParseLatest("", 274));
        Assert.Null(ImplLoopTerminalSignal.ParseLatest(null, 274));
    }

    [Fact]
    public void ParseLatest_WrongIssue_ReturnsNull()
    {
        // The block correlates with a different issue; the watcher for issue 274 must not consume it.
        var sig = ImplLoopTerminalSignal.ParseLatest(Block(999, "done", "1", "yes", "other"), 274);

        Assert.Null(sig);
    }

    [Fact]
    public void ParseLatest_TwoBlocks_ReturnsLastMatchingIssue()
    {
        // A transcript that re-ran (or carries an earlier provisional block) must yield the LAST
        // block for the issue, so the parse is idempotent against a growing transcript.
        var transcript =
            Block(274, "failed", "none", "no", "first attempt aborted") +
            "more work\n" +
            Block(274, "done", "281", "yes", "second attempt merged");

        var sig = ImplLoopTerminalSignal.ParseLatest(transcript, 274);

        Assert.NotNull(sig);
        Assert.Equal(ImplLoopSignal.Done, sig.Signal);
        Assert.Equal("second attempt merged", sig.Reason);
    }

    [Fact]
    public void ParseLatest_CrLfLineEndings_Parses()
    {
        var transcript = "IMPL-LOOP-TERMINAL\r\nissue: 12\r\nsignal: done\r\npr: 34\r\nmerged: yes\r\nreason: ok\r\n";

        var sig = ImplLoopTerminalSignal.ParseLatest(transcript, 12);

        Assert.NotNull(sig);
        Assert.Equal(ImplLoopSignal.Done, sig.Signal);
        Assert.Equal(34, sig.Pr);
    }

    [Fact]
    public void ParseLatest_PrWithHashPrefix_ParsesNumber()
    {
        var sig = ImplLoopTerminalSignal.ParseLatest(Block(1, "done", "#42", "yes", "ok"), 1);

        Assert.NotNull(sig);
        Assert.Equal(42, sig.Pr);
    }

    [Fact]
    public void ParseLatest_IncompleteBlock_MissingSignal_ReturnsNull()
    {
        var transcript = "IMPL-LOOP-TERMINAL\nissue: 274\npr: none\n";

        Assert.Null(ImplLoopTerminalSignal.ParseLatest(transcript, 274));
    }
}
