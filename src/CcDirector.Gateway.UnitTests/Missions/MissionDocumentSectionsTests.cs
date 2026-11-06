using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// Reading a mission document's why and goal sections as plain text (the Missions First-Class mission): present,
/// absent, empty, nested markup, and markers a browser would never render.
/// </summary>
public sealed class MissionDocumentSectionsTests
{
    private static string Page(string body) => "<!doctype html><html><body><main>" + body + "</main></body></html>";

    [Fact]
    public void Read_BothSectionsPresent_ReturnsEachAsPlainText()
    {
        var text = MissionDocumentSections.Read(Page(
            "<section data-dev-report=\"why\"><p>Too many sessions keep asking.</p></section>" +
            "<section data-dev-report=\"goal\"><p>Every session shows its Mission.</p></section>"));

        Assert.Equal("Too many sessions keep asking.", text.Why);
        Assert.Equal("Every session shows its Mission.", text.Goal);
    }

    [Fact]
    public void Read_NoMarkers_ReturnsNullForBoth()
    {
        var text = MissionDocumentSections.Read(Page("<section data-dev-report=\"detail\"><p>Detail.</p></section>"));

        Assert.Null(text.Why);
        Assert.Null(text.Goal);
    }

    [Fact]
    public void Read_OnlyGoal_WhyIsNullNotEmpty()
    {
        var text = MissionDocumentSections.Read(Page("<section data-dev-report=\"goal\"><p>Ship.</p></section>"));

        Assert.Null(text.Why);
        Assert.Equal("Ship.", text.Goal);
    }

    [Fact]
    public void Read_EmptySection_IsEmptyNotNull()
    {
        var text = MissionDocumentSections.Read(Page("<section data-dev-report=\"goal\">  <p> </p> </section>"));

        Assert.Equal("", text.Goal);
    }

    [Fact]
    public void Read_NestedMarkup_FlattensToLinesAndDropsHeadingsHiddenAndUnrendered()
    {
        var text = MissionDocumentSections.Read(Page("""
            <section data-dev-report="goal">
              <h2>3. The goal</h2>
              <p>In a real Director, <strong>screenshotted</strong>:</p>
              <ul>
                <li>A session with a Mission shows <em>the   button</em>.</li>
                <li>A session with none shows <code>No mission</code>.
                  <ul><li>Nested point.</li></ul>
                </li>
              </ul>
              <p hidden>Secret draft.</p>
              <style>.x { color: red; }</style>
              <template><p>Template text.</p></template>
              <div><span>Last</span> <span>line</span></div>
            </section>
            """));

        Assert.Equal(
            "In a real Director, screenshotted:\n" +
            "- A session with a Mission shows the button.\n" +
            "- A session with none shows No mission.\n" +
            "- Nested point.\n" +
            "Last line",
            text.Goal);
    }

    [Fact]
    public void Read_CharacterReferences_AreDecoded()
    {
        var text = MissionDocumentSections.Read(Page("<section data-dev-report=\"why\"><p>Fast &amp; calm &lt;now&gt;</p></section>"));

        Assert.Equal("Fast & calm <now>", text.Why);
    }

    [Fact]
    public void Read_MarkerInsideTemplateOrComment_IsNotASection()
    {
        var text = MissionDocumentSections.Read(Page(
            "<template><section data-dev-report=\"goal\"><p>Not shown.</p></section></template>" +
            "<!-- <section data-dev-report=\"why\"><p>Commented.</p></section> -->"));

        Assert.Null(text.Goal);
        Assert.Null(text.Why);
    }

    [Fact]
    public void Read_SectionLongerThanTheLimit_IsCutAtTheLimit()
    {
        var text = MissionDocumentSections.Read(Page(
            $"<section data-dev-report=\"goal\"><p>{new string('a', MissionDocumentSections.MaxLength + 50)}</p></section>"));

        Assert.Equal(MissionDocumentSections.MaxLength, text.Goal!.Length);
    }
}
