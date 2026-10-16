using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>The launch line the Director composes for Pi, turned into the argument list an API-mode process starts with.</summary>
public sealed class ApiModeArgumentsTests
{
    [Fact]
    public void Split_PisLaunchLine_KeepsTheQuotedPreamblePathAsOneArgument()
    {
        // Act
        var arguments = ApiModeArguments.Split("--session-id 9f1c --append-system-prompt \"/home/a user/.cc/pi-preamble/9f1c.md\"");

        // Assert
        Assert.Equal(["--session-id", "9f1c", "--append-system-prompt", "/home/a user/.cc/pi-preamble/9f1c.md"], arguments);
    }

    [Fact]
    public void Split_AWindowsPath_KeepsEveryBackslash()
    {
        // Act
        var arguments = ApiModeArguments.Split(@"--append-system-prompt ""C:\Users\a user\AppData\Local\cc-director\pi\9f1c.md""");

        // Assert: nothing in the line is escaped, so a backslash is a path separator, not an escape.
        Assert.Equal(["--append-system-prompt", @"C:\Users\a user\AppData\Local\cc-director\pi\9f1c.md"], arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_NoArguments_IsEmpty(string? line)
    {
        // Act
        var arguments = ApiModeArguments.Split(line);

        // Assert
        Assert.Empty(arguments);
    }

    [Fact]
    public void Split_ExtraSpacesAndTabs_SeparateWithoutEmptyArguments()
    {
        // Act
        var arguments = ApiModeArguments.Split("  --model\tgpt  --session-id   x ");

        // Assert
        Assert.Equal(["--model", "gpt", "--session-id", "x"], arguments);
    }

    [Fact]
    public void Split_AnEmptyQuotedArgument_IsKept()
    {
        // Act
        var arguments = ApiModeArguments.Split("--name \"\" --x");

        // Assert
        Assert.Equal(["--name", "", "--x"], arguments);
    }

    [Fact]
    public void Split_AQuoteNeverClosed_Throws()
    {
        // Act
        var error = Assert.Throws<ArgumentException>(() => ApiModeArguments.Split("--append-system-prompt \"/no/end"));

        // Assert
        Assert.Contains("never closed", error.Message);
    }
}
