using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Coverage for TtsService.SplitIntoChunks - the sentence-boundary chunker
/// that enables unlimited-length TTS by splitting input into pieces under
/// OpenAI's per-call cap and concatenating the resulting MP3s on the server.
/// </summary>
public sealed class TtsChunkSplitterTests
{
    [Fact]
    public void SplitIntoChunks_ShortInput_ReturnsOneChunk()
    {
        var result = TtsService.SplitIntoChunks("Hello world.", 100);
        Assert.Single(result);
        Assert.Equal("Hello world.", result[0]);
    }

    [Fact]
    public void SplitIntoChunks_EmptyInput_ReturnsEmptyList()
    {
        var result = TtsService.SplitIntoChunks("", 100);
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_ExactlyAtLimit_ReturnsOneChunk()
    {
        var text = new string('a', 100);
        var result = TtsService.SplitIntoChunks(text, 100);
        Assert.Single(result);
    }

    [Fact]
    public void SplitIntoChunks_TwoSentencesWithinLimit_ReturnsOneChunk()
    {
        var result = TtsService.SplitIntoChunks("First sentence. Second sentence.", 100);
        Assert.Single(result);
    }

    [Fact]
    public void SplitIntoChunks_TwoSentencesExceedLimit_SplitsAtSentenceBoundary()
    {
        var first  = "First sentence here.";    // 20 chars
        var second = "Second sentence here.";   // 21 chars
        var text   = first + " " + second;       // 42 chars
        var result = TtsService.SplitIntoChunks(text, 25);
        Assert.Equal(2, result.Count);
        Assert.Equal(first, result[0]);
        Assert.Equal(second, result[1]);
    }

    [Fact]
    public void SplitIntoChunks_NoSentencePunctuation_FallsBackToWordBoundary()
    {
        // 200-char run with no terminal punctuation; chunker must still split.
        var text = string.Join(' ', Enumerable.Repeat("word", 50));
        var result = TtsService.SplitIntoChunks(text, 50);
        Assert.True(result.Count > 1);
        foreach (var chunk in result)
            Assert.True(chunk.Length <= 50, $"chunk too long ({chunk.Length}): {chunk}");
    }

    [Fact]
    public void SplitIntoChunks_LongerThanLimit_ProducesMultipleChunks()
    {
        var sentence = "This is a sentence about compilers. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 200));   // ~7200 chars
        var result = TtsService.SplitIntoChunks(text, 3500);
        Assert.True(result.Count >= 2);
        foreach (var chunk in result)
            Assert.True(chunk.Length <= 3500, $"chunk too long ({chunk.Length})");
    }

    [Fact]
    public void SplitIntoChunks_AllChunksJoinBackToOriginalContent()
    {
        // Round-trip: split and rejoin should preserve content (modulo
        // whitespace at chunk boundaries since we trim).
        var text = string.Join(" ", new[]
        {
            "First sentence.", "Second sentence!", "Third question?",
            "Fourth one is longer and goes on for a while.",
            "Fifth.", "Sixth and final.",
        });
        var result = TtsService.SplitIntoChunks(text, 30);
        var rejoined = string.Join(" ", result);
        // Allow internal whitespace differences but every word should survive.
        var originalWords = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var rejoinedWords = rejoined.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(originalWords.Length, rejoinedWords.Length);
        for (int i = 0; i < originalWords.Length; i++)
            Assert.Equal(originalWords[i], rejoinedWords[i]);
    }
}
