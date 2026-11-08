using CcDirector.Gateway.Inbox;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Inbox;

/// <summary>An email's HTML body as Your inbox's plain text (8 November 2026): the words, and every link's address.</summary>
public sealed class OwnerEmailTextTests
{
    [Fact]
    public void FromHtml_LinkWithWords_KeepsTheWordsAndTheAddress()
        => Assert.Equal("Open the run (https://x.test/run/5) now.", OwnerEmailText.FromHtml("<p>Open <a href=\"https://x.test/run/5\">the run</a> now.</p>"));

    [Fact]
    public void FromHtml_LinkWhoseWordsAreTheAddress_IsWrittenOnce()
        => Assert.Equal("https://x.test/a", OwnerEmailText.FromHtml("<a href='https://x.test/a'>https://x.test/a</a>"));

    [Fact]
    public void FromHtml_BlocksAndBreaks_BecomeLines()
        => Assert.Equal("Title\n\nOne\nTwo\n\n- a\n- b", OwnerEmailText.FromHtml("<h2>Title</h2>\n\n<p>One<br>Two</p><ul><li>a</li><li>b</li></ul>"));

    [Fact]
    public void FromHtml_ScriptsStylesAndEntities_AreDroppedOrDecoded()
        => Assert.Equal("A & B < C", OwnerEmailText.FromHtml("<style>p{color:red}</style><script>x()</script><p>A &amp; B &lt; C</p>"));

    [Fact]
    public void FromHtml_Empty_IsEmpty()
        => Assert.Equal("", OwnerEmailText.FromHtml(null));

    [Fact]
    public void FromHtml_PreElement_KeepsItsLineBreaksAndSpacing()
        => Assert.Equal("Log:\n\nERROR one\n  at Two()\n  at Three()\n\nDone.",
            OwnerEmailText.FromHtml("<p>Log:</p><pre>ERROR one\n  at Two()\n  at Three()</pre><p>Done.</p>"));

    [Fact]
    public void FromHtml_PreElement_StillDecodesEntitiesAndDropsTags()
        => Assert.Equal("x < y\nz", OwnerEmailText.FromHtml("<pre><b>x</b> &lt; y\nz</pre>"));
}
