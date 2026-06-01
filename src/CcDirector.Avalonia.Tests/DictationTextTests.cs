using CcDirector.Avalonia.Voice;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="DictationText.Join"/>, the whitespace-aware join that
/// assembles the dictation transcript. These pin the "Resume appends new speech to
/// the (edited) transcript without overwriting edits" invariant from #156: the left
/// side is whatever the user currently has in the editable review box and the right
/// side is the freshly cleaned segment.
/// </summary>
public class DictationTextTests
{
    [Fact]
    public void Join_EmptyLeft_ReturnsRight()
    {
        Assert.Equal("hello", DictationText.Join("", "hello"));
    }

    [Fact]
    public void Join_EmptyRight_ReturnsLeft()
    {
        Assert.Equal("hello", DictationText.Join("hello", ""));
    }

    [Fact]
    public void Join_BothEmpty_ReturnsEmpty()
    {
        Assert.Equal("", DictationText.Join("", ""));
    }

    [Fact]
    public void Join_TwoWords_InsertsSingleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello", "world"));
    }

    [Fact]
    public void Join_LeftEndsWithSpace_DoesNotDoubleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello ", "world"));
    }

    [Fact]
    public void Join_RightStartsWithSpace_DoesNotDoubleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello", " world"));
    }

    [Fact]
    public void Join_LeftEndsWithNewline_PreservesBoundaryWithoutAddingSpace()
    {
        Assert.Equal("hello\nworld", DictationText.Join("hello\n", "world"));
    }

    [Fact]
    public void Join_EditedTextThenNewSpeech_PreservesEditsAndAppends()
    {
        // The #156 invariant: the user edited the reviewed transcript, then resumed
        // and spoke more. The edited text must survive verbatim as a prefix, with
        // the new cleaned segment appended after a single separating space.
        var edited = "We need to fix the desktop transcription tool.";
        var newSpeech = "It now lets us edit before sending.";

        var result = DictationText.Join(edited, newSpeech);

        Assert.StartsWith(edited, result);
        Assert.EndsWith(newSpeech, result);
        Assert.Equal(edited + " " + newSpeech, result);
    }
}
