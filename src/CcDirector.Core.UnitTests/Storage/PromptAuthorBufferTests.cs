using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// WHO AUTHORED A TURN, and the one answer this buffer is never allowed to give by accident.
///
/// The Chat screen wants to show the owner his own prompts and leave out the fleet doorbell, the queue
/// drain and the handover text. The agent's transcript cannot tell those apart - in it they are all user
/// messages with no mark on them - so the answer comes from what the Director saw at its submit choke
/// point, which is what this buffer holds.
///
/// The rule the tests below exist to pin is the asymmetry. Getting it wrong in one direction shows the
/// reader a doorbell among his prompts, which he can see and shrug at. Getting it wrong in the other
/// direction HIDES SOMETHING HE TYPED, which he cannot see and has no reason to suspect. So an author
/// this buffer does not know is "unknown" and never "agent", and every caller that hides turns must
/// treat unknown as the owner's.
/// </summary>
public sealed class PromptAuthorBufferTests
{
    private const string Sid = "5b8e1a40-0000-4000-8000-0000000000cc";

    public PromptAuthorBufferTests() => PromptAuthorBuffer.Clear();

    [Fact]
    public void AuthorOf_TextTheOwnerSubmitted_IsTheOwner()
    {
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "find my last prompt");

        Assert.Equal(WorkingOrigins.Owner, PromptAuthorBuffer.AuthorOf(Sid, "find my last prompt"));
    }

    [Fact]
    public void AuthorOf_TheFleetDoorbell_IsTheAgent()
    {
        const string doorbell = "[DevThrottle doorbell] 2 fleet messages are waiting for you.";
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Agent, doorbell);

        Assert.Equal(WorkingOrigins.Agent, PromptAuthorBuffer.AuthorOf(Sid, doorbell));
    }

    [Fact]
    public void AuthorOf_TextThisDirectorNeverSaw_IsUnknown_NotAgent()
    {
        // The case that matters most: a Director that restarted, a turn older than the buffer, or a
        // prompt typed straight into the terminal. "I do not know" must never be dressed up as "the
        // product said it" - that is what would hide the owner's own words from him.
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "something else entirely");

        var author = PromptAuthorBuffer.AuthorOf(Sid, "a prompt from before this Director started");

        Assert.Equal(PromptAuthorBuffer.Unknown, author);
        Assert.NotEqual(WorkingOrigins.Agent, author);
    }

    [Fact]
    public void AuthorOf_ASessionWithNothingRecorded_IsUnknown()
    {
        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, "anything at all"));
    }

    [Theory]
    [InlineData("  find my last prompt  ")]
    [InlineData("find  my   last prompt")]
    [InlineData("find my\nlast prompt")]
    [InlineData("find my\r\n\tlast prompt")]
    public void AuthorOf_MatchesThroughWhitespaceTheTranscriptReshaped(string asItCameBack)
    {
        // A prompt is submitted as one line and can come back out of a transcript wrapped, re-indented or
        // with its newlines normalized. None of that changes a word of it, so none of it may change the
        // answer - otherwise the owner's own turn quietly becomes "unknown".
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "find my last prompt");

        Assert.Equal(WorkingOrigins.Owner, PromptAuthorBuffer.AuthorOf(Sid, asItCameBack));
    }

    [Fact]
    public void AuthorOf_TheSameWordsSentTwice_AnswersWithTheLatestSender()
    {
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Agent, "carry on");
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "carry on");

        Assert.Equal(WorkingOrigins.Owner, PromptAuthorBuffer.AuthorOf(Sid, "carry on"));
    }

    [Fact]
    public void AuthorOf_AnotherSessionsSubmission_IsNotBorrowed()
    {
        PromptAuthorBuffer.Record("another-session", WorkingOrigins.Agent, "carry on");

        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, "carry on"));
    }

    [Fact]
    public void Record_WhitespaceOnlyText_IsNeverRecordedAndNeverMatched()
    {
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Agent, "   \n\t ");

        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, "   "));
        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, ""));
    }

    [Fact]
    public void Forget_DropsTheSessionsAuthors()
    {
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "find my last prompt");
        PromptAuthorBuffer.Forget(Sid);

        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, "find my last prompt"));
    }

    [Fact]
    public void Record_KeepsNoPromptText()
    {
        // The key is a hash and nothing else. A record read back must not hand anyone the words.
        const string secret = "the thing I typed";
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, secret);

        var key = PromptAuthorBuffer.KeyFor(secret);

        Assert.DoesNotContain(secret, key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, key.Length); // SHA-256, hex
        Assert.Matches("^[0-9A-F]+$", key);
    }

    [Fact]
    public void Record_PastTheBound_AgesOutToUnknownRatherThanGrowingForever()
    {
        PromptAuthorBuffer.Record(Sid, WorkingOrigins.Owner, "the first thing I ever said");
        for (var i = 0; i < 1000; i++)
            PromptAuthorBuffer.Record(Sid, WorkingOrigins.Agent, $"filler turn {i}");

        // Aged out - and it comes back as unknown, which is SHOWN, not as agent, which would hide it.
        Assert.Equal(PromptAuthorBuffer.Unknown, PromptAuthorBuffer.AuthorOf(Sid, "the first thing I ever said"));
        Assert.Equal(WorkingOrigins.Agent, PromptAuthorBuffer.AuthorOf(Sid, "filler turn 999"));
    }
}
