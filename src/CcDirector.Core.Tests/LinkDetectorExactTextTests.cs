using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A link is the EXACT text on screen - never shortened, never extended into the words around it,
/// never rewritten. Owner ruling, 3 October 2026: "We should not mess around and change URLs ever."
/// Each row is a case the link finder got wrong: it stopped early, started late, or linked
/// something that is not a link.
/// </summary>
public class LinkDetectorExactTextTests
{
    private const string Repo = @"D:\repo";

    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"D:\repo\README.md",
        @"D:\repo\src\a\b.cs",
        @"C:\Program Files (x86)\App\app.exe",
        @"C:\Users\x.txt",
        @"\\server\share\file.txt",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "app", "settings.json"),
    };

    private static bool Exists(string path) => Existing.Contains(path);

    /// <summary>The single link found in <paramref name="line"/>: the text it underlines and its target.</summary>
    private static (string underlined, string target, LinkDetector.LinkType type) OnlyLink(string line)
    {
        var m = Assert.Single(LinkDetector.FindAllLinkMatches(line, Repo, Exists));
        return (line.Substring(m.StartCol, m.EndCol - m.StartCol), m.Text, m.Type);
    }

    // ---- URLs are never shortened ------------------------------------------------------------

    [Theory]
    [InlineData("see https://en.wikipedia.org/wiki/Foo_(bar) now", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("it is at https://example.com/it's", "https://example.com/it's")]
    [InlineData("query https://x.io/a?ids=[1,2] done", "https://x.io/a?ids=[1,2]")]
    [InlineData("The report: https://gateway.devthrottle.com/r/e3b65c4d-d5cc-4d8b-a060-273e4570db25", "https://gateway.devthrottle.com/r/e3b65c4d-d5cc-4d8b-a060-273e4570db25")]
    public void Url_IsTheWholeAddress(string line, string url)
    {
        var (underlined, target, type) = OnlyLink(line);

        Assert.Equal(url, underlined);
        Assert.Equal(url, target);
        Assert.Equal(LinkDetector.LinkType.Url, type);
    }

    // ---- ...and never takes in the punctuation around it ------------------------------------

    [Theory]
    [InlineData("(see https://example.com/x)", "https://example.com/x")]
    [InlineData("[report](https://example.com/r/1)", "https://example.com/r/1")]
    [InlineData("open https://example.com/x!", "https://example.com/x")]
    [InlineData("at https://example.com/x: then", "https://example.com/x")]
    [InlineData("**https://example.com/x**", "https://example.com/x")]
    [InlineData("'https://example.com/x'", "https://example.com/x")]
    [InlineData("https://example.com/a\u2500\u2500\u2500\u2500", "https://example.com/a")]
    public void Url_StopsAtSurroundingPunctuation(string line, string url)
    {
        var (underlined, target, _) = OnlyLink(line);

        Assert.Equal(url, underlined);
        Assert.Equal(url, target);
    }

    // ---- ...and never runs on past where it closed -----------------------------------------------

    [Theory]
    [InlineData("fetch('https://api.example.com/v1/users').then(r => r.json())", "https://api.example.com/v1/users")]
    [InlineData("requests.get('https://api.example.com/v1/users').json()", "https://api.example.com/v1/users")]
    [InlineData("(https://example.com/a)text", "https://example.com/a")]
    [InlineData("background:url(https://example.com/a.png);color:red", "https://example.com/a.png")]
    [InlineData("a[https://example.com/a]b", "https://example.com/a")]
    [InlineData("the https://example.com's page", "https://example.com")]
    [InlineData("q https://example.com/a?x=[1]&y='2' end", "https://example.com/a?x=[1]&y='2'")]
    public void Url_EndsWhereItClosed(string line, string url)
    {
        var (underlined, target, _) = OnlyLink(line);

        Assert.Equal(url, underlined);
        Assert.Equal(url, target);
    }

    [Theory]
    [InlineData("{'url':'https://a.com/x','n':1}", new[] { "https://a.com/x" })]
    [InlineData("'https://a.com/x','https://b.com/y'", new[] { "https://a.com/x", "https://b.com/y" })]
    [InlineData("[x](https://example.com/a),[y](https://example.com/b)", new[] { "https://example.com/a", "https://example.com/b" })]
    public void QuotedOrBracketedUrls_StaySeparate(string line, string[] urls)
    {
        var found = LinkDetector.FindAllLinkMatches(line, Repo, Exists)
            .Select(m => line.Substring(m.StartCol, m.EndCol - m.StartCol)).ToArray();

        Assert.Equal(urls, found);
    }

    [Theory]
    [InlineData("{'error': 'rate limited, see https://docs.x.com/limits'}", "https://docs.x.com/limits")]
    [InlineData("{ msg: 'see https://a.com/x'}", "https://a.com/x")]
    [InlineData("'see https://example.com/x'.format(a)", "https://example.com/x")]
    [InlineData("warn('docs: https://a.com/x'+suffix)", "https://a.com/x")]
    [InlineData("print('see https://example.com/x').strip()", "https://example.com/x")]
    [InlineData("console.log('Server at https://localhost:3000/api');", "https://localhost:3000/api")]
    [InlineData("https://example.com/a(see below)", "https://example.com/a")]
    // ...however the rest of the line pairs its quotes
    [InlineData("\"{'error': 'see https://a.com/x'}\"", "https://a.com/x")]
    [InlineData("msg=\"{'k': 'see https://a.com/x'}\" code=1", "https://a.com/x")]
    [InlineData("`{'k': 'see https://a.com/x'}`", "https://a.com/x")]
    [InlineData("run `warn('docs: https://a.com/x'.upper())` now", "https://a.com/x")]
    [InlineData("it's {'error': 'see https://a.com/x'}", "https://a.com/x")]
    [InlineData("can't reach {'url': 'at https://a.com/x'}", "https://a.com/x")]
    [InlineData("it's broken: {'k': 'see https://a.com/x'} and it's old", "https://a.com/x")]
    [InlineData("don't use 'see https://a.com/x'.format(a)", "https://a.com/x")]
    [InlineData("// don't forget: log('see https://a.com/x'+y)", "https://a.com/x")]
    [InlineData("{'a': 'it's', 'b': 'see https://a.com/x'}", "https://a.com/x")]
    [InlineData("it's https://x.io/it's here", "https://x.io/it's")]
    // ...when the message is joined straight to another string
    [InlineData("log('see https://a.com/x'+'and more')", "https://a.com/x")]
    [InlineData("x = 'see https://a.com/x'+' more'", "https://a.com/x")]
    [InlineData("'see https://a.com/x'.replace('a b','c')", "https://a.com/x")]
    [InlineData("('see https://a.com/x'=='a b')", "https://a.com/x")]
    [InlineData("'see https://a.com/it's' now", "https://a.com/it's")]
    [InlineData("{'k': 'see https://a.com/it's'}", "https://a.com/it's")]
    // ...including an address that ends in a slash or other punctuation
    [InlineData("print('Visit https://a.com/docs/'+page)", "https://a.com/docs/")]
    [InlineData("log('see https://a.com/docs/'+'more')", "https://a.com/docs/")]
    [InlineData("{'a':'see https://a.com/x/','b':'y'}", "https://a.com/x/")]
    [InlineData("['see https://a.com/docs/','b']", "https://a.com/docs/")]
    [InlineData("url = 'see https://a.com/api/'+id+'/items'", "https://a.com/api/")]
    [InlineData("log('see https://a.com/?q='+q+'&x=1')", "https://a.com/?q=")]
    [InlineData("'see https://en.wikipedia.org/wiki/Foo_(bar)','b'", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("log('see https://a.com/x#'+frag)", "https://a.com/x#")]
    [InlineData("https://a.com/a/''", "https://a.com/a/")]
    // ...including a string with a prefix, and after a possessive earlier on the line
    [InlineData("log(f'see https://a.com/x'+'more')", "https://a.com/x")]
    [InlineData("[f'see https://a.com/x','b']", "https://a.com/x")]
    [InlineData("log(b'see https://a.com/x'+b'more')", "https://a.com/x")]
    [InlineData("select N'see https://a.com/x',N'b'", "https://a.com/x")]
    [InlineData("print(f'Visit https://a.com/docs/'+page)", "https://a.com/docs/")]
    [InlineData("the users' guide says 'see https://a.com/x','b'", "https://a.com/x")]
    [InlineData("\"James' notes\" 'see https://a.com/x','b'", "https://a.com/x")]
    [InlineData("the users' https://host/odata/Products?$filter=Name%20eq%20'Chai'", "https://host/odata/Products?$filter=Name%20eq%20'Chai'")]
    // ...while quotes the address itself holds stay in it
    [InlineData("https://host/odata/Products?$filter=Name%20eq%20'Chai'", "https://host/odata/Products?$filter=Name%20eq%20'Chai'")]
    [InlineData("https://host/api/items(guid'12345678-1234')", "https://host/api/items(guid'12345678-1234')")]
    [InlineData("https://a.com/?q='it's'", "https://a.com/?q='it's'")]
    [InlineData("https://a.com/?name='O'Neil'", "https://a.com/?name='O'Neil'")]
    [InlineData("https://services.odata.org/V4/Northwind/Products?$filter=contains(ProductName,'Chai')", "https://services.odata.org/V4/Northwind/Products?$filter=contains(ProductName,'Chai')")]
    [InlineData("GET https://host/odata/Products?$filter=startswith(Name,'Mi') 200", "https://host/odata/Products?$filter=startswith(Name,'Mi')")]
    [InlineData("https://host/api?in=('a','b')", "https://host/api?in=('a','b')")]
    [InlineData("https://host/_api/web/lists/getbytitle('Documents')/items", "https://host/_api/web/lists/getbytitle('Documents')/items")]
    [InlineData("https://a.com/?q='' ok", "https://a.com/?q=''")]
    // ...an unquoted address followed by a quote
    [InlineData("https://a.com/x,'hello'", "https://a.com/x")]
    [InlineData("see https://a.com/x,'hello' there", "https://a.com/x")]
    [InlineData("https://a.com/x''", "https://a.com/x")]
    // ...and a closing bracket of the wrong kind
    [InlineData("(https://a.com/x[1)", "https://a.com/x")]
    [InlineData("arr[https://a.com/x(1]", "https://a.com/x")]
    public void Url_InsideAQuotedMessage_EndsAtTheClosingQuote(string line, string url)
    {
        var (underlined, target, _) = OnlyLink(line);

        Assert.Equal(url, underlined);
        Assert.Equal(url, target);
    }

    [Theory]
    [InlineData("{'url':'https://a.com/x','file':'src/a/b.cs'}")]
    [InlineData("https://a.com/x,'src/a/b.cs'")]
    [InlineData("https://a.com/x;'src/a/b.cs'")]
    public void UrlFollowedByAQuotedPath_BothAreLinks(string line)
    {
        var found = LinkDetector.FindAllLinkMatches(line, Repo, Exists)
            .Select(m => line.Substring(m.StartCol, m.EndCol - m.StartCol)).OrderBy(t => t).ToArray();

        Assert.Equal(new[] { "https://a.com/x", "src/a/b.cs" }, found);
    }

    [Fact]
    public void UrlFollowedByAQuotedPath_NeverAsksAboutItsHostAsAShare()
    {
        var asked = new List<string>();
        LinkDetector.FindAllLinkMatches("https://a.com/x,'src/a/b.cs'", Repo, p => { asked.Add(p); return Exists(p); });

        Assert.DoesNotContain(asked, p => p.StartsWith(@"\\"));
    }

    [Fact]
    public void FileUrlFollowedByAQuotedPath_StaysAFileUrl()
    {
        const string line = "file:///D:/Repos/x.html,'src/a/b.cs'";
        var m = LinkDetector.FindAllLinkMatches(line, Repo, Exists).OrderBy(x => x.StartCol).First();

        Assert.Equal("file:///D:/Repos/x.html", line.Substring(m.StartCol, m.EndCol - m.StartCol));
        Assert.Equal(@"D:\Repos\x.html", m.Text);
    }

    [Fact]
    public void MissingNetworkShapedText_IsAskedAboutOnce()
    {
        var asked = new List<string>();
        LinkDetector.FindAllLinkMatches(@"escaped \\n\\t end", Repo, p => { asked.Add(p); return false; });

        Assert.Single(asked);
    }

    [Fact]
    public void LongTailOfBrackets_IsTrimmedQuickly()
    {
        string line = "https://example.com/a" + new string(')', 10000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var (underlined, _, _) = OnlyLink(line);

        Assert.Equal("https://example.com/a", underlined);
        Assert.True(watch.ElapsedMilliseconds < 50, $"took {watch.ElapsedMilliseconds} ms");
    }

    // ---- paths ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\repo\src\File.cs(42,7): error CS1002: ; expected", @"D:\repo\src\File.cs")]
    [InlineData(@"D:\repo\src\File.cs(42): warning", @"D:\repo\src\File.cs")]
    [InlineData(@"D:\repo\src\File.cs(42,7,42,12): error", @"D:\repo\src\File.cs")]
    [InlineData(@"D:\repo\src\File.cs(42-45): error", @"D:\repo\src\File.cs")]
    [InlineData(@"D:\path\file.txt(see below)", @"D:\path\file.txt")]
    public void CompilerErrorLine_TargetsTheFile(string line, string path)
    {
        var (_, target, _) = OnlyLink(line);

        Assert.Equal(path, target);
    }

    [Fact]
    public void WindowsPathPassedToACall_EndsAtTheClosingBracket()
    {
        var (underlined, _, _) = OnlyLink(@"File.ReadAllText(D:\path\file.txt).Trim()");

        Assert.Equal(@"D:\path\file.txt", underlined);
    }

    [Fact]
    public void EscapedDrivePath_KeepsItsDriveLetter()
    {
        var (underlined, _, _) = OnlyLink(@"path=C:\\Users\\soren\\file.txt done");

        Assert.Equal(@"C:\\Users\\soren\\file.txt", underlined);
    }

    [Fact]
    public void EscapeSequencesInALog_AreNotANetworkPath()
    {
        Assert.Empty(LinkDetector.FindAllLinkMatches(@"escaped \\n\\t end", Repo, Exists));
    }

    [Fact]
    public void NumbersAndDottedNames_AreNeverLookedUp()
    {
        var asked = new List<string>();
        LinkDetector.FindAllLinkMatches("version 2.10.0 is 3.14 times faster", Repo, p => { asked.Add(p); return false; });

        Assert.Empty(asked);
    }

    [Fact]
    public void WindowsPathWithBrackets_IsWhole()
    {
        var (underlined, target, _) = OnlyLink(@"installed to C:\Program Files (x86)\App\app.exe today");

        Assert.Equal(@"C:\Program Files (x86)\App\app.exe", underlined);
        Assert.Equal(@"C:\Program Files (x86)\App\app.exe", target);
    }

    [Fact]
    public void WindowsPathInBrackets_LeavesTheClosingBracketOut()
    {
        var (underlined, _, _) = OnlyLink(@"(see D:\path\file.txt)");

        Assert.Equal(@"D:\path\file.txt", underlined);
    }

    [Fact]
    public void DotNetStackTraceLine_TargetsTheFile()
    {
        var (_, target, _) = OnlyLink(@"at Foo() in D:\repo\src\file.cs:line 12");

        Assert.Equal(@"D:\repo\src\file.cs", target);
    }

    [Fact]
    public void BareFileName_InTheRepo_IsALink()
    {
        var (underlined, target, _) = OnlyLink("see README.md for details");

        Assert.Equal("README.md", underlined);
        Assert.Equal("README.md", target);
    }

    [Fact]
    public void BareFileName_NotInTheRepo_IsNotALink()
    {
        Assert.Empty(LinkDetector.FindAllLinkMatches("bump to v1.2 and example.com", Repo, Exists));
    }

    [Fact]
    public void HomeFolderPath_IsALinkWhenItExists()
    {
        var (underlined, _, _) = OnlyLink(@"config in ~/.config/app/settings.json now");

        Assert.Equal("~/.config/app/settings.json", underlined);
    }

    [Fact]
    public void NetworkPath_IsALink()
    {
        var (underlined, _, _) = OnlyLink(@"share at \\server\share\file.txt now");

        Assert.Equal(@"\\server\share\file.txt", underlined);
    }

    // ---- things that are NOT drive paths --------------------------------------------------------

    [Fact]
    public void RelativePathWithAOneLetterFolder_IsNotADrivePath()
    {
        var (underlined, target, _) = OnlyLink("edit src/a/b.cs now");

        Assert.Equal("src/a/b.cs", underlined);
        Assert.Equal("src/a/b.cs", target);
    }

    [Fact]
    public void WslMountPath_IsNotCutDownToADrivePath()
    {
        foreach (var m in LinkDetector.FindAllLinkMatches("open /mnt/c/Users/x.txt", Repo, Exists))
            Assert.NotEqual("/c/Users/x.txt", m.Text);
    }

    [Fact]
    public void SubredditName_IsNotADrivePath()
    {
        Assert.Empty(LinkDetector.FindAllLinkMatches("post in /r/programming today", Repo, Exists));
    }

    [Fact]
    public void GitBashPath_ThatExists_IsStillALink()
    {
        var (underlined, _, _) = OnlyLink("open /c/Users/x.txt now");

        Assert.Equal("/c/Users/x.txt", underlined);
    }

    // ---- the two lookups agree -------------------------------------------------------------------

    [Theory]
    [InlineData("see https://en.wikipedia.org/wiki/Foo_(bar) now", 10)]
    [InlineData(@"installed to C:\Program Files (x86)\App\app.exe today", 20)]
    [InlineData("edit src/a/b.cs now", 8)]
    public void DetectLinkAtPosition_AgreesWithFindAllLinkMatches(string line, int col)
    {
        var m = Assert.Single(LinkDetector.FindAllLinkMatches(line, Repo, Exists));

        var (text, type) = LinkDetector.DetectLinkAtPosition(line, col, Repo, Exists);

        Assert.Equal(m.Text, text);
        Assert.Equal(m.Type, type);
    }
}
