using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// WHAT ELSE GOES LIVE WITH A DEFINED FACTORY'S BRIEF (Factory Definition, phase 6; phase 4 QA finding 5). A brief is
/// saved on the definition's newest version, so a person publishing a one-line brief edit also publishes every change
/// saved since the published version. The confirm names each one; the seat's own brief change is left out.
/// </summary>
public sealed class BriefPublishAlsoGoesLiveTests
{
    private static Dictionary<string, string> With(Dictionary<string, string> files, string path, string oldText, string newText)
    {
        var changed = new Dictionary<string, string>(files, StringComparer.Ordinal);
        Assert.Contains(oldText, changed[path]);
        changed[path] = changed[path].Replace(oldText, newText, StringComparison.Ordinal);
        return changed;
    }

    [Fact]
    public void AlsoGoesLive_OnlyThisSeatsBriefChanged_SaysItIsTheOnlyChange()
    {
        var published = DefinitionFixture.Publishable();
        var draft = With(published, "seats/boss.yaml", "Run the factory.", "Run it better.");

        var line = FactoryBriefPanelFold.AlsoGoesLive("boss", 4, published, draft);

        Assert.Equal("Nothing else in the definition has changed since version 4 was published, so this brief is the only change that goes live.", line);
    }

    [Fact]
    public void AlsoGoesLive_AnotherSeatRenamedAndAStartMoved_NamesBoth_ButNotThisSeatsBrief()
    {
        var published = DefinitionFixture.Publishable();
        var draft = With(published, "seats/writer.yaml", "name: Writer", "name: Author");
        draft = With(draft, "seats/boss.yaml", "Run the factory.", "Run it better.");
        draft = With(draft, "seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 9 * * 1-5\"");

        var line = FactoryBriefPanelFold.AlsoGoesLive("boss", 4, published, draft);

        Assert.StartsWith("It also puts live the 2 other changes saved in the definition since version 4 was published, not only this brief: ", line);
        Assert.Contains("Seat Author (writer) changed: name", line);
        Assert.Contains("Writer", line);
        Assert.Contains("start", line);
        Assert.DoesNotContain("brief changed", line);
    }

    [Fact]
    public void AlsoGoesLive_ThisSeatsOwnOtherChange_IsNamed_WithoutItsBrief()
    {
        var published = DefinitionFixture.Publishable();
        var draft = With(published, "seats/writer.yaml", "name: Writer", "name: Author");
        draft = With(draft, "seats/writer.yaml", "brief: Write the posts.", "brief: Write better posts.");

        var line = FactoryBriefPanelFold.AlsoGoesLive("writer", 2, published, draft);

        Assert.StartsWith("It also puts live the other change saved in the definition since version 2 was published, not only this brief: Seat Author (writer) changed: name", line);
        Assert.DoesNotContain("brief changed", line);
    }

    [Fact]
    public void AlsoGoesLive_ManyChanges_NamesTheFirstSix_AndSaysHowManyMore()
    {
        var published = DefinitionFixture.Publishable();
        var draft = new Dictionary<string, string>(published, StringComparer.Ordinal);
        for (var i = 0; i < 8; i++) draft[$"files/note{i}.md"] = $"note {i}\n";

        var line = FactoryBriefPanelFold.AlsoGoesLive("boss", 1, published, draft);

        Assert.StartsWith("It also puts live the 8 other changes saved in the definition since version 1 was published", line);
        Assert.Contains("files/note5.md", line);
        Assert.DoesNotContain("files/note6.md", line);
        Assert.EndsWith(" And 2 more; the Definition tab lists every change.", line);
    }
}
