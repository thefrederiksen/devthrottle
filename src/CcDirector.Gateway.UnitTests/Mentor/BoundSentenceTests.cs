using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/test_bound_sentence.py</c>, the part that lives in this repository: the
/// guarantee sentence the rendered "How this was made" carries is IDENTICAL, character for character, to the one
/// written HERE in the test's own text - never read from the renderer and compared to itself. The reference's test
/// also checks the skill the hosted agent loads (SKILL.md) and the design page in the internal repository, which
/// hold the same sentence; those two files are not in this repository and that half of the check stays in Python.
/// </summary>
public sealed class BoundSentenceTests
{
    private const string Sentence = "Every quotation and citation in this report was fetched and verified by the tools it lists; "
        + "the advice lines and the level words are the mentor's judgement over the cited evidence.";

    [Fact]
    public void The_guarantee_sentence_is_this_exact_sentence()
    {
        Assert.Equal(Sentence, Render.BoundAndJudged);
        // The test can fail: the one-character variant the second inspection found is NOT the sentence.
        var lower = "e" + Sentence.Substring(1);
        Assert.NotEqual(Render.BoundAndJudged, lower);
    }
}
