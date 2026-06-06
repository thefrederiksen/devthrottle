using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

// =====================================================================================
// DictatedPromptResolver (issue #208, review rounds 3-5): bare @file prompts (dictation
// drops "@.temp/input_*.txt") substitute the file's CONTENT into the TurnPackage so the
// brain, the saved package, and the review corpus see the user's actual words.
// =====================================================================================
public sealed class DictatedPromptResolverTests : IDisposable
{
    private readonly string _repo;

    public DictatedPromptResolverTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "resolver-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repo, ".temp"));
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static TurnPackage Package(string? first, string? last)
        => new(Guid.NewGuid(), 7, first, last, "reply", ReplyPending: false,
               TranscriptDelta: "", ScreenTail: "",
               RollingIntent: null, PriorRailLines: new List<string>());

    [Theory]
    [InlineData("@.temp/input_20260606_171005_bnzc3s.txt", true)]
    [InlineData("@.temp\\seed-wingman.md", true)]
    [InlineData("  @.temp/input_x.txt  ", true)]
    [InlineData("look at @.temp/input_x.txt and tell me", false)] // mixed prompt: user words win
    [InlineData("@.temp/../secrets.txt", false)]                  // upward escape
    [InlineData("plain prompt", false)]
    [InlineData("@singleword", false)]                            // no path separator
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBareAtReference_DetectsOnlyWholePromptReferences(string? prompt, bool expected)
    {
        Assert.Equal(expected, DictatedPromptResolver.IsBareAtReference(prompt, out _));
    }

    [Fact]
    public void Resolve_SubstitutesFileContent_ForBothPrompts()
    {
        File.WriteAllText(Path.Combine(_repo, ".temp", "input_a.txt"), "I dictated this seed ask.");
        File.WriteAllText(Path.Combine(_repo, ".temp", "input_b.txt"), "And this follow-up.");
        var p = Package("@.temp/input_a.txt", "@.temp/input_b.txt");

        var resolved = DictatedPromptResolver.Resolve(p, _repo);

        Assert.Equal("I dictated this seed ask.", resolved.FirstUserPrompt);
        Assert.Equal("And this follow-up.", resolved.LastUserPrompt);
    }

    [Fact]
    public void Resolve_MissingFile_KeepsReferenceVerbatim()
    {
        // The remote-Director boundary: the path is not readable on this machine.
        var p = Package("first ask", "@.temp/input_gone.txt");
        var resolved = DictatedPromptResolver.Resolve(p, _repo);
        Assert.Same(p, resolved);
        Assert.Equal("@.temp/input_gone.txt", resolved.LastUserPrompt);
    }

    [Fact]
    public void Resolve_NoRepoPath_ReturnsSamePackage()
    {
        var p = Package("@.temp/input_a.txt", "@.temp/input_b.txt");
        Assert.Same(p, DictatedPromptResolver.Resolve(p, null));
        Assert.Same(p, DictatedPromptResolver.Resolve(p, "  "));
    }

    [Fact]
    public void Resolve_EmptyFile_KeepsReferenceVerbatim()
    {
        File.WriteAllText(Path.Combine(_repo, ".temp", "input_empty.txt"), "   ");
        var p = Package(null, "@.temp/input_empty.txt");
        var resolved = DictatedPromptResolver.Resolve(p, _repo);
        Assert.Equal("@.temp/input_empty.txt", resolved.LastUserPrompt);
    }

    [Fact]
    public void Resolve_OversizeContent_Capped()
    {
        File.WriteAllText(Path.Combine(_repo, ".temp", "input_big.txt"), new string('x', 10_000));
        var p = Package(null, "@.temp/input_big.txt");
        var resolved = DictatedPromptResolver.Resolve(p, _repo);
        Assert.NotNull(resolved.LastUserPrompt);
        Assert.Equal(DictatedPromptResolver.MaxChars + 3, resolved.LastUserPrompt.Length); // + "..."
    }

    [Fact]
    public void NeedsResolution_TrueOnlyWhenABarePromptExists()
    {
        Assert.True(DictatedPromptResolver.NeedsResolution(Package("@.temp/input_a.txt", "plain")));
        Assert.True(DictatedPromptResolver.NeedsResolution(Package("plain", "@.temp/input_b.txt")));
        Assert.False(DictatedPromptResolver.NeedsResolution(Package("plain", "also plain")));
    }
}
