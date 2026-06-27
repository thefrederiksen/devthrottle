using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Covers skill-to-tool discovery: a skill mentioning a known tool links to it, the namesake/head
/// heuristic decides Drives vs Uses, unknown tools are never linked, and the embedded override file
/// adds links discovery cannot see - each tagged Discovered vs Declared honestly.
/// </summary>
public class SkillToolLinkerTests : IDisposable
{
    private readonly string _root;

    public SkillToolLinkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SkillLinkerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    private void Skill(string name, string body)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    private SkillToolLinker Linker(params string[] knownTools) => new(new[] { _root }, knownTools);

    [Fact]
    public void BuildLinks_SkillMentionsKnownTool_LinksDiscovered()
    {
        Skill("myskill", "This skill calls cc-vault to read data.");

        var links = Linker("cc-vault").BuildLinks();

        var matching = links.Where(l => l.ToolName == "cc-vault" && l.SkillName == "myskill").ToList();
        Assert.Single(matching);
        Assert.Equal(SkillLinkSource.Discovered, matching[0].Source);
    }

    [Fact]
    public void BuildLinks_NamesakeSkill_RelationDrives()
    {
        // Skill dir "vault" matches the tool minus its cc- prefix; mention is buried past the head.
        Skill("vault", new string(' ', 500) + " mentions cc-vault once deep in the body.");

        var links = Linker("cc-vault").BuildLinks();

        var matching = links.Where(l => l.ToolName == "cc-vault" && l.SkillName == "vault").ToList();
        Assert.Single(matching);
        Assert.Equal(SkillLinkRelation.Drives, matching[0].Relation);
    }

    [Fact]
    public void BuildLinks_SingleBodyMention_RelationUses()
    {
        // Not a namesake, single mention, only after the 400-char head region -> "uses".
        Skill("unrelated", new string('x', 500) + " incidental cc-vault reference.");

        var links = Linker("cc-vault").BuildLinks();

        var matching = links.Where(l => l.ToolName == "cc-vault" && l.SkillName == "unrelated").ToList();
        Assert.Single(matching);
        Assert.Equal(SkillLinkRelation.Uses, matching[0].Relation);
    }

    [Fact]
    public void BuildLinks_ToolNotInKnownSet_NotLinked()
    {
        Skill("myskill", "mentions cc-imaginary which is not a real tool.");

        var links = Linker("cc-vault").BuildLinks();

        Assert.DoesNotContain(links, l => l.ToolName == "cc-imaginary");
    }

    [Fact]
    public void BuildLinks_OverrideForUndiscoveredPair_AddsDeclared()
    {
        // No skill file mentions cc-devthrottle here; the embedded override declares
        // cc-settings-api drives its settings subcommands.
        var links = Linker("cc-devthrottle").BuildLinks();

        var matching = links.Where(l => l.ToolName == "cc-devthrottle" && l.SkillName == "cc-settings-api").ToList();
        Assert.Single(matching);
        Assert.Equal(SkillLinkSource.Declared, matching[0].Source);
        Assert.Equal(SkillLinkRelation.Drives, matching[0].Relation);
    }

    [Fact]
    public void GetLinksForTool_FiltersToThatTool()
    {
        Skill("myskill", "uses cc-vault here.");

        var links = Linker("cc-vault").GetLinksForTool("cc-vault");

        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.Equal("cc-vault", l.ToolName));
    }
}
