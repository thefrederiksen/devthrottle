using CcDirector.Core.ApiMode;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>The one translation between the Director's connection mode and the Gateway contracts' words (issue #4070).</summary>
public sealed class SessionConnectionModeWordsTests
{
    [Theory]
    [InlineData(SessionConnectionMode.Terminal, ConnectionModes.Terminal)]
    [InlineData(SessionConnectionMode.Api, ConnectionModes.Api)]
    public void ToWord_EachMode_IsPhaseTwosWord(SessionConnectionMode mode, string word)
    {
        // Act
        var result = SessionConnectionModeWords.ToWord(mode);

        // Assert
        Assert.Equal(word, result);
    }

    [Theory]
    [InlineData(null, SessionConnectionMode.Terminal)]
    [InlineData("", SessionConnectionMode.Terminal)]
    [InlineData("terminal", SessionConnectionMode.Terminal)]
    [InlineData("api", SessionConnectionMode.Api)]
    public void FromWord_AbsentOrAKnownWord_IsThatMode(string? word, SessionConnectionMode mode)
    {
        // Act
        var result = SessionConnectionModeWords.FromWord(word);

        // Assert: absent is terminal, as from a caller that predates the field.
        Assert.Equal(mode, result);
    }

    [Theory]
    [InlineData("API")]
    [InlineData("rpc")]
    [InlineData(" api")]
    public void FromWord_AnyOtherWord_IsRefusedNamingIt(string word)
    {
        // Act
        var error = Assert.Throws<ArgumentException>(() => SessionConnectionModeWords.FromWord(word));

        // Assert: never guessed into a mode nobody asked for.
        Assert.Contains($"\"{word}\" is not a connection mode", error.Message);
    }

    [Theory]
    [InlineData(SessionConnectionMode.Api, false, WheelHolders.Protocol)]
    [InlineData(SessionConnectionMode.Api, true, WheelHolders.None)]
    [InlineData(SessionConnectionMode.Terminal, false, WheelHolders.None)]
    [InlineData(SessionConnectionMode.Terminal, true, WheelHolders.None)]
    public void WheelHolderOf_ModeAndWhetherItEnded_IsWhoHoldsTheConversation(SessionConnectionMode mode, bool ended, string holder)
    {
        // Act
        var result = SessionConnectionModeWords.WheelHolderOf(mode, ended);

        // Assert
        Assert.Equal(holder, result);
    }
}
