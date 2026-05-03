using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class LinkDetectorTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;
    private static readonly Func<string, bool> NeverExists = _ => false;

    // ========================================================================
    // Quoted Paths
    // ========================================================================

    [Fact]
    public void FindAllLinkMatches_QuotedWindowsPath_DoubleQuotes_MatchesPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Check ""D:\Projects\file.txt"" for details", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\Projects\file.txt", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedWindowsPath_SingleQuotes_MatchesPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Check 'D:\Projects\file.txt' for details", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\Projects\file.txt", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedWindowsPath_Backticks_MatchesPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Check `D:\Projects\file.txt` for details", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\Projects\file.txt", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedPathWithSpaces_MatchesEntirePath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"""D:\Projects\course\sample-course\AI Agents Join the Club.pdf""",
            null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\Projects\course\sample-course\AI Agents Join the Club.pdf", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedPathWithSpaces_SingleQuotes_MatchesEntirePath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"'D:\Users\test\Pictures\Screenshots\Screenshot 2026-03-01 074852.png'",
            null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\Users\test\Pictures\Screenshots\Screenshot 2026-03-01 074852.png", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedRelativePath_MatchesWhenExists()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"""./src/some file.cs""", @"C:\repo", AlwaysExists);

        Assert.Single(matches);
        Assert.Equal("./src/some file.cs", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedRelativePath_SkippedWhenNotExists()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"""./src/some file.cs""", @"C:\repo", NeverExists);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedUnixPath_MatchesPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"""/c/Users/test/my file.txt""", null, null);

        Assert.Single(matches);
        Assert.Equal("/c/Users/test/my file.txt", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_EmptyQuotes_NoMatch()
    {
        var matches = LinkDetector.FindAllLinkMatches(@"he said """"", null, null);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_UnclosedQuote_NoQuotedMatch()
    {
        // Unclosed quote - should still detect the unquoted part
        var matches = LinkDetector.FindAllLinkMatches(
            @"""D:\path\file.txt", null, null);

        // The unquoted regex should pick up D:\path\file.txt (no closing quote means no quoted match)
        // The unquoted regex excludes " so it won't include the opening quote
        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.txt", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_QuotedPathUnderlineExcludesQuotes()
    {
        string text = @"Open ""D:\path\file.txt"" now";
        var matches = LinkDetector.FindAllLinkMatches(text, null, null);

        Assert.Single(matches);
        // StartCol should be at 'D', not at '"'
        Assert.Equal(6, matches[0].StartCol); // index of D after "Open "
        // EndCol should be at closing quote position, not after it
        Assert.Equal(text.IndexOf('"', 6), matches[0].EndCol);
    }

    // ========================================================================
    // Trailing Punctuation
    // ========================================================================

    [Theory]
    [InlineData("file.txt,", "file.txt")]
    [InlineData("file.txt;", "file.txt")]
    [InlineData("file.txt.,", "file.txt")]
    [InlineData("file.txt,;", "file.txt")]
    public void StripTrailingPunctuation_CommonPunctuation_Stripped(string input, string expected)
    {
        Assert.Equal(expected, LinkDetector.StripTrailingPunctuation(input));
    }

    [Fact]
    public void StripTrailingPunctuation_TrailingPeriodAfterExtension_Stripped()
    {
        Assert.Equal("file.txt", LinkDetector.StripTrailingPunctuation("file.txt."));
    }

    [Fact]
    public void StripTrailingPunctuation_FileExtension_NotStripped()
    {
        Assert.Equal("file.txt", LinkDetector.StripTrailingPunctuation("file.txt"));
    }

    [Fact]
    public void StripTrailingPunctuation_UrlTrailingDot_Stripped()
    {
        Assert.Equal("https://example.com/page", LinkDetector.StripTrailingPunctuation("https://example.com/page."));
    }

    [Fact]
    public void StripTrailingPunctuation_UrlTrailingComma_Stripped()
    {
        Assert.Equal("https://example.com/page", LinkDetector.StripTrailingPunctuation("https://example.com/page,"));
    }

    [Fact]
    public void StripTrailingPunctuation_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", LinkDetector.StripTrailingPunctuation(""));
    }

    [Fact]
    public void StripTrailingPunctuation_NoDotNoPunctuation_Unchanged()
    {
        Assert.Equal("path/to/file", LinkDetector.StripTrailingPunctuation("path/to/file"));
    }

    [Fact]
    public void StripTrailingPunctuation_SingleDot_Stripped()
    {
        // Trailing period is always sentence punctuation
        Assert.Equal("file", LinkDetector.StripTrailingPunctuation("file."));
    }

    [Fact]
    public void FindAllLinkMatches_WindowsPathWithTrailingComma_StrippedInResult()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Check D:\path\file.txt, then proceed", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.txt", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_WindowsPathWithTrailingPeriod_StrippedInResult()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Check D:\path\file.txt. Then proceed", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.txt", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_UrlWithTrailingPeriod_StrippedInResult()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            "Visit https://example.com/page. Thanks", null, null);

        Assert.Single(matches);
        Assert.Equal("https://example.com/page", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_LocalhostUrlWithTrailingPeriod_StrippedInResult()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            "available at http://localhost:4001.", null, null);

        Assert.Single(matches);
        Assert.Equal("http://localhost:4001", matches[0].Text);
    }

    // ========================================================================
    // Overlap Deduplication
    // ========================================================================

    [Fact]
    public void FindAllLinkMatches_QuotedAndUnquotedOverlap_OnlyOneReturned()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"""D:\path\file.txt""", null, null);

        // Should have exactly one match (quoted), not two (quoted + unquoted)
        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.txt", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_MultipleNonOverlapping_AllReturned()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"D:\file1.txt and D:\file2.txt", null, null);

        Assert.Equal(2, matches.Count);
        Assert.Equal(@"D:\file1.txt", matches[0].Text);
        Assert.Equal(@"D:\file2.txt", matches[1].Text);
    }

    // ========================================================================
    // Windows Absolute Paths
    // ========================================================================

    [Theory]
    [InlineData(@"D:\path\file.txt")]
    [InlineData(@"C:\Users\test\Documents\report.pdf")]
    [InlineData(@"D:/path/file.txt")]
    public void FindAllLinkMatches_AbsoluteWindowsPath_Matches(string path)
    {
        var matches = LinkDetector.FindAllLinkMatches(path, null, null);

        Assert.Single(matches);
        Assert.Equal(path, matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_AbsoluteWindowsPathWithLineNumber_StripsLineNumber()
    {
        var matches = LinkDetector.FindAllLinkMatches(@"D:\path\file.cs:42", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.cs", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_AbsoluteWindowsPathWithLineAndColumn_StripsLineNumber()
    {
        var matches = LinkDetector.FindAllLinkMatches(@"D:\path\file.cs:42:10", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.cs", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_PathEmbeddedInText_MatchesJustPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            @"Error in D:\src\file.cs:10 at runtime", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\src\file.cs", matches[0].Text);
    }

    // ========================================================================
    // Absolute Paths With Spaces (existence-driven extension)
    // ========================================================================

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_ExtendsToLongestExistingPath()
    {
        const string fullPath = @"D:\Test Root\Outer Folder\Inner Folder\nested\plan.md";
        string line = $"Created at {fullPath}.";

        bool Exists(string p) => p == fullPath;
        var matches = LinkDetector.FindAllLinkMatches(line, null, Exists);

        Assert.Single(matches);
        Assert.Equal(fullPath, matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_NoCallback_FallsBackToNoSpaceMatch()
    {
        // Without a callback we cannot validate existence, so we must preserve the
        // baseline regex behavior of stopping at the first space.
        var matches = LinkDetector.FindAllLinkMatches(
            @"Created at D:\Test Root\Outer Folder\file.md.", null, null);

        Assert.NotEmpty(matches);
        Assert.Equal(@"D:\Test", matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_DoesNotConsumeFollowingWords()
    {
        // "D:\file.md" exists but "D:\file.md and trailing" does not.
        // Extension must not eat the trailing prose.
        const string filePath = @"D:\file.md";
        string line = $"Open {filePath} and trailing words";

        bool Exists(string p) => p == filePath;
        var matches = LinkDetector.FindAllLinkMatches(line, null, Exists);

        Assert.Single(matches);
        Assert.Equal(filePath, matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_TrailingPeriodStripped()
    {
        const string filePath = @"D:\Outer Folder\file.md";
        string line = $"See {filePath}.";

        bool Exists(string p) => p == filePath;
        var matches = LinkDetector.FindAllLinkMatches(line, null, Exists);

        Assert.Single(matches);
        Assert.Equal(filePath, matches[0].Text);
        // EndCol should land before the trailing period
        Assert.Equal(line.Length - 1, matches[0].EndCol);
    }

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_ClaimsFullRangeSoRelativeRegexDoesNotDoubleMatch()
    {
        // Without claiming the extended range, the relative-path regex would match
        // "Outer Folder\nested\..." as a separate link.
        const string fullPath = @"D:\Outer Folder\Inner Folder\nested\file.md";
        string line = $"Created at {fullPath}.";

        bool Exists(string p) => p == fullPath;
        var matches = LinkDetector.FindAllLinkMatches(line, @"D:\repo", Exists);

        Assert.Single(matches);
        Assert.Equal(fullPath, matches[0].Text);
    }

    [Fact]
    public void FindAllLinkMatches_AbsolutePathWithSpaces_NonExistentPath_FallsBackToNoSpaceMatch()
    {
        // If neither the no-space match nor any extension exists, behavior should
        // match baseline (no-space match returned).
        var matches = LinkDetector.FindAllLinkMatches(
            @"Some D:\nonexistent\path here", null, NeverExists);

        Assert.Single(matches);
        Assert.Equal(@"D:\nonexistent\path", matches[0].Text);
    }

    [Fact]
    public void DetectLinkAtPosition_AbsolutePathWithSpaces_CursorInExtension_ReturnsFullPath()
    {
        const string fullPath = @"D:\Outer Folder\file.md";
        string line = $"Created at {fullPath}.";
        int cursor = line.IndexOf("Folder", StringComparison.Ordinal) + 2;

        bool Exists(string p) => p == fullPath;
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, cursor, null, Exists);

        Assert.Equal(fullPath, text);
        Assert.Equal(LinkDetector.LinkType.Path, type);
    }

    // ========================================================================
    // Unix Paths
    // ========================================================================

    [Theory]
    [InlineData("/c/Users/test/file.txt")]
    [InlineData("/d/Projects/myapp/src/main.cs")]
    public void FindAllLinkMatches_UnixPath_Matches(string path)
    {
        var matches = LinkDetector.FindAllLinkMatches(path, null, null);

        Assert.Single(matches);
        Assert.Equal(path, matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    // ========================================================================
    // Relative Paths
    // ========================================================================

    [Theory]
    [InlineData("./src/file.cs")]
    [InlineData("src/Components/App.tsx")]
    [InlineData("../other/file.txt")]
    [InlineData(@"tools\communication_manager\run.bat")]
    public void FindAllLinkMatches_RelativePath_MatchesWhenExists(string path)
    {
        var matches = LinkDetector.FindAllLinkMatches(
            path, @"C:\repo", AlwaysExists);

        Assert.Single(matches);
        Assert.Equal(path, matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[0].Type);
    }

    [Theory]
    [InlineData("./src/file.cs")]
    [InlineData("src/Components/App.tsx")]
    public void FindAllLinkMatches_RelativePath_SkippedWhenNotExists(string path)
    {
        var matches = LinkDetector.FindAllLinkMatches(
            path, @"C:\repo", NeverExists);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_RelativePath_SkippedWhenNoRepoPath()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            "src/file.cs", null, AlwaysExists);

        Assert.Empty(matches);
    }

    // ========================================================================
    // URLs
    // ========================================================================

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path")]
    [InlineData("https://github.com/user/repo/blob/main/file.cs")]
    public void FindAllLinkMatches_HttpUrl_Matches(string url)
    {
        var matches = LinkDetector.FindAllLinkMatches(url, null, null);

        Assert.Single(matches);
        Assert.Equal(url, matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Url, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_GitAtUrl_Matches()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            "git@github.com:user/repo.git", null, null);

        Assert.Single(matches);
        Assert.Equal("git@github.com:user/repo.git", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Url, matches[0].Type);
    }

    [Fact]
    public void FindAllLinkMatches_UrlInMiddleOfText_Matches()
    {
        var matches = LinkDetector.FindAllLinkMatches(
            "Visit https://docs.example.com/guide for help", null, null);

        Assert.Single(matches);
        Assert.Equal("https://docs.example.com/guide", matches[0].Text);
    }

    // ========================================================================
    // StripLineNumber
    // ========================================================================

    [Theory]
    [InlineData("file.cs:42", "file.cs")]
    [InlineData("file.cs:10:20", "file.cs")]
    [InlineData("file.cs", "file.cs")]
    [InlineData("D:\\path\\file.cs:100", "D:\\path\\file.cs")]
    public void StripLineNumber_VariousCases(string input, string expected)
    {
        Assert.Equal(expected, LinkDetector.StripLineNumber(input));
    }

    [Fact]
    public void StripLineNumber_ColonInPath_NotStripped()
    {
        // Drive letter colon should not be stripped
        Assert.Equal(@"D:\path\file.cs", LinkDetector.StripLineNumber(@"D:\path\file.cs"));
    }

    // ========================================================================
    // ResolvePath
    // ========================================================================

    [Fact]
    public void ResolvePath_UnixPath_ConvertsToWindows()
    {
        string result = LinkDetector.ResolvePath("/c/Users/test/file.txt", null);

        Assert.Equal(@"C:\Users\test\file.txt", result);
    }

    [Fact]
    public void ResolvePath_AbsoluteWindowsPath_ReturnsAsIs()
    {
        string result = LinkDetector.ResolvePath(@"D:\path\file.txt", null);

        Assert.Equal(@"D:\path\file.txt", result);
    }

    [Fact]
    public void ResolvePath_RelativePath_ResolvesAgainstRepo()
    {
        string result = LinkDetector.ResolvePath("src/file.cs", @"C:\repo");

        Assert.Equal(@"C:\repo\src\file.cs", result);
    }

    [Fact]
    public void ResolvePath_RelativePath_NoRepo_ReturnsAsIs()
    {
        string result = LinkDetector.ResolvePath("src/file.cs", null);

        Assert.Equal("src/file.cs", result);
    }

    // ========================================================================
    // DetectLinkAtPosition
    // ========================================================================

    [Fact]
    public void DetectLinkAtPosition_CursorOnUrl_ReturnsUrl()
    {
        string line = "Visit https://example.com for info";
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, 10, null, null);

        Assert.Equal("https://example.com", text);
        Assert.Equal(LinkDetector.LinkType.Url, type);
    }

    [Fact]
    public void DetectLinkAtPosition_CursorOnPath_ReturnsPath()
    {
        string line = @"Error in D:\src\file.cs at line 10";
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, 12, null, null);

        Assert.Equal(@"D:\src\file.cs", text);
        Assert.Equal(LinkDetector.LinkType.Path, type);
    }

    [Fact]
    public void DetectLinkAtPosition_CursorOffLink_ReturnsNone()
    {
        string line = @"Error in D:\src\file.cs at line 10";
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, 0, null, null);

        Assert.Null(text);
        Assert.Equal(LinkDetector.LinkType.None, type);
    }

    [Fact]
    public void DetectLinkAtPosition_CursorOnQuotedPath_ReturnsPath()
    {
        string line = @"Check ""D:\path with spaces\file.txt"" now";
        // Cursor at 'p' in 'path' (inside the quotes)
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, 9, null, null);

        Assert.Equal(@"D:\path with spaces\file.txt", text);
        Assert.Equal(LinkDetector.LinkType.Path, type);
    }

    [Fact]
    public void DetectLinkAtPosition_CursorOutsideQuotedPath_ReturnsNone()
    {
        string line = @"Check ""D:\path\file.txt"" now";
        // Cursor at 'n' in 'now'
        var (text, type) = LinkDetector.DetectLinkAtPosition(line, 26, null, null);

        Assert.Null(text);
        Assert.Equal(LinkDetector.LinkType.None, type);
    }

    // ========================================================================
    // Edge Cases
    // ========================================================================

    [Fact]
    public void FindAllLinkMatches_EmptyString_ReturnsEmpty()
    {
        var matches = LinkDetector.FindAllLinkMatches("", null, null);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_WhitespaceOnly_ReturnsEmpty()
    {
        var matches = LinkDetector.FindAllLinkMatches("   ", null, null);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_NullInput_ReturnsEmpty()
    {
        var matches = LinkDetector.FindAllLinkMatches(null, null, null);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindAllLinkMatches_MixedPathsAndUrls_AllDetected()
    {
        string line = @"See D:\file.txt and https://example.com for more";
        var matches = LinkDetector.FindAllLinkMatches(line, null, null);

        Assert.Equal(2, matches.Count);
        // URLs are collected before absolute paths in priority order
        Assert.Equal("https://example.com", matches[0].Text);
        Assert.Equal(LinkDetector.LinkType.Url, matches[0].Type);
        Assert.Equal(@"D:\file.txt", matches[1].Text);
        Assert.Equal(LinkDetector.LinkType.Path, matches[1].Type);
    }

    [Fact]
    public void FindAllLinkMatches_PathFollowedByParens_ParensNotIncluded()
    {
        // Parens are excluded by the regex character class
        var matches = LinkDetector.FindAllLinkMatches(
            @"(see D:\path\file.txt)", null, null);

        Assert.Single(matches);
        Assert.Equal(@"D:\path\file.txt", matches[0].Text);
    }

    // ========================================================================
    // ExtractQuotedSpans (internal)
    // ========================================================================

    [Fact]
    public void ExtractQuotedSpans_DoubleQuotes_ExtractsSpan()
    {
        var spans = LinkDetector.ExtractQuotedSpans(@"hello ""world"" end");

        Assert.Single(spans);
        Assert.Equal("world", spans[0].InnerText);
        Assert.Equal(6, spans[0].OuterStart);
        Assert.Equal(13, spans[0].OuterEnd);
        Assert.Equal(7, spans[0].InnerStart);
        Assert.Equal(12, spans[0].InnerEnd);
    }

    [Fact]
    public void ExtractQuotedSpans_SingleQuotes_ExtractsSpan()
    {
        var spans = LinkDetector.ExtractQuotedSpans("hello 'world' end");

        Assert.Single(spans);
        Assert.Equal("world", spans[0].InnerText);
    }

    [Fact]
    public void ExtractQuotedSpans_Backticks_ExtractsSpan()
    {
        var spans = LinkDetector.ExtractQuotedSpans("hello `world` end");

        Assert.Single(spans);
        Assert.Equal("world", spans[0].InnerText);
    }

    [Fact]
    public void ExtractQuotedSpans_MultipleSpans_ExtractsAll()
    {
        var spans = LinkDetector.ExtractQuotedSpans(@"""first"" and 'second'");

        Assert.Equal(2, spans.Count);
        Assert.Equal("first", spans[0].InnerText);
        Assert.Equal("second", spans[1].InnerText);
    }

    [Fact]
    public void ExtractQuotedSpans_UnclosedQuote_NoSpan()
    {
        var spans = LinkDetector.ExtractQuotedSpans(@"""unclosed");

        Assert.Empty(spans);
    }

    [Fact]
    public void ExtractQuotedSpans_EmptyQuotes_NoSpan()
    {
        // Empty quotes (adjacent quotes with nothing inside) - closeIndex would equal i+1, not > i+1
        var spans = LinkDetector.ExtractQuotedSpans(@"""""");

        Assert.Empty(spans);
    }
}
