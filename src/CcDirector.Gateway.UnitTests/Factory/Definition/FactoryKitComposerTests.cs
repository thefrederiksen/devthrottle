using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Records;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE KIT'S COMPOSITION, ON ITS OWN (Factory Definition, phase 5, design section 4). The composer is a pure function,
/// so every row of the design's table is pinned here against the fixture definition: which files a seat kit and a
/// rules-only kit hold, that <c>allow</c> never reaches a kit, the order and dedupe of the merged rules, and that the
/// plugin folder exists only for a Claude Code seat that names a factory skill.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitComposerTests
{
    private static readonly DateTime Built = new(2026, 10, 15, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The fixture's outreach seat, also naming the factory skill, with a third rule set that repeats one deny rule
    /// and asks about two things - so the merge has a duplicate to drop and an order to keep.
    /// </summary>
    private static Dictionary<string, string> Content(string? outreachAgent = null)
    {
        var content = DefinitionFixture.Valid();
        content["seats/outreach.yaml"] = content["seats/outreach.yaml"]
            .Replace("rules: [base, outreach]", "rules: [outreach, base, review]", StringComparison.Ordinal)
            .Replace("  fleet: [browsers]", "  fleet: [browsers]\n  factory: [mail-voice]", StringComparison.Ordinal);
        if (outreachAgent is not null)
            content["seats/outreach.yaml"] = content["seats/outreach.yaml"]
                .Replace("agent: ClaudeCode", $"agent: {outreachAgent}", StringComparison.Ordinal);
        content["rules/review.json"] = """
            {
              "permissions": {
                "allow": ["Bash(ls:*)"],
                "deny": ["Bash(git push:*)", "Bash(curl:*)"],
                "ask": ["Bash(gh pr merge:*)", "Bash(git push:*)", "Bash(gh pr merge:*)"]
              }
            }
            """.ReplaceLineEndings("\n");
        return content;
    }

    private static FactoryKitSource Source(Dictionary<string, string> content, string seat, string kind,
        IReadOnlyList<FactoryKitJournalEntry>? journal = null, int? journalTotal = null,
        IReadOnlyList<FactoryDocumentSummary>? documents = null, IReadOnlyList<FactoryKitAttachment>? attachments = null,
        int attachmentsTotal = 0)
    {
        var read = FactoryDefinitionCheck.Read(content);
        Assert.NotNull(read.Definition);
        return new FactoryKitSource(DefinitionFixture.FactoryId, 7, content, read.Definition!, seat, kind, Built,
            journal ?? Array.Empty<FactoryKitJournalEntry>(), journalTotal ?? journal?.Count ?? 0,
            documents ?? Array.Empty<FactoryDocumentSummary>(),
            attachments ?? Array.Empty<FactoryKitAttachment>(), attachmentsTotal);
    }

    private static Dictionary<string, string> Compose(FactoryKitSource source)
        => FactoryKitComposer.Compose(source).ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static List<string> Strings(JsonElement list) => list.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ---------- which files ----------

    [Fact]
    public void Compose_SeatKitForAClaudeSeatWithAFactorySkill_HoldsEveryFileOfTheTable()
    {
        var kit = Compose(Source(Content(), "outreach", FactoryKits.Seat));

        Assert.Equal(new[]
        {
            "brief.md",
            "claude-plugin/.claude-plugin/plugin.json",
            "claude-plugin/skills/mail-voice/SKILL.md",
            "claude-plugin/skills/mail-voice/examples.md",
            "files/outreach_send.py",
            "grants.md",
            "kit.json",
            "records/documents.md",
            "records/journal.md",
            "rules.json",
        }, kit.Keys.ToArray());
    }

    [Fact]
    public void Compose_RulesOnlyKit_HoldsKitJsonAndRulesJsonOnly()
    {
        var kit = Compose(Source(Content(), "outreach", FactoryKits.RulesOnly));

        Assert.Equal(new[] { "kit.json", "rules.json" }, kit.Keys.ToArray());
        // A child is not the seat: it carries none of the seat's secret names.
        Assert.False(Json(kit["kit.json"]).TryGetProperty("secrets", out _));
        Assert.Equal("rules-only", Json(kit["kit.json"]).GetProperty("kind").GetString());
    }

    [Fact]
    public void Compose_RulesOnlyKit_CarriesTheSameRulesAsTheSeatKit()
    {
        var seat = Compose(Source(Content(), "outreach", FactoryKits.Seat));
        var child = Compose(Source(Content(), "outreach", FactoryKits.RulesOnly));

        Assert.Equal(seat["rules.json"], child["rules.json"]);
    }

    // ---------- the rules ----------

    [Fact]
    public void Rules_DenyAndAskUnionedInRuleSetOrder_DuplicatesRemoved_AllowNever()
    {
        // The seat names [outreach, base, review]: outreach denies rm, base denies git push (and allows git status),
        // review denies git push again and curl, asks about merge twice and git push once, and allows ls.
        var rules = Json(Compose(Source(Content(), "outreach", FactoryKits.Seat))["rules.json"]);

        var permissions = rules.GetProperty("permissions");
        Assert.Equal(new[] { "Bash(rm:*)", "Bash(git push:*)", "Bash(curl:*)" }, Strings(permissions.GetProperty("deny")));
        Assert.Equal(new[] { "Bash(gh pr merge:*)", "Bash(git push:*)" }, Strings(permissions.GetProperty("ask")));
        Assert.False(permissions.TryGetProperty("allow", out _));
        Assert.DoesNotContain("git status", rules.GetRawText());
        Assert.DoesNotContain("Bash(ls:*)", rules.GetRawText());
    }

    [Fact]
    public void Rules_ASeatWithNoRuleSets_HasEmptyListsAndNoAllow()
    {
        var rules = Json(Compose(Source(Content(), "writer", FactoryKits.Seat))["rules.json"]).GetProperty("permissions");

        Assert.Empty(Strings(rules.GetProperty("deny")));
        Assert.Empty(Strings(rules.GetProperty("ask")));
        Assert.False(rules.TryGetProperty("allow", out _));
    }

    // ---------- the plugin ----------

    [Fact]
    public void Compose_ACodexSeatThatNamesAFactorySkill_GetsNoPlugin()
    {
        var kit = Compose(Source(Content(outreachAgent: "Codex"), "outreach", FactoryKits.Seat));

        Assert.DoesNotContain(kit.Keys, p => p.StartsWith("claude-plugin/", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_AClaudeSeatThatNamesNoFactorySkill_GetsNoPlugin()
    {
        // The boss is Claude Code by the factory's default and names no factory skill.
        var kit = Compose(Source(Content(), "boss", FactoryKits.Seat));

        Assert.DoesNotContain(kit.Keys, p => p.StartsWith("claude-plugin/", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_ThePlugin_IsNamedFactoryKitAndCarriesTheSkillFilesAsStored()
    {
        var content = Content();
        var kit = Compose(Source(content, "outreach", FactoryKits.Seat));

        Assert.Equal("factory-kit", Json(kit["claude-plugin/.claude-plugin/plugin.json"]).GetProperty("name").GetString());
        Assert.Equal(content["skills/mail-voice/SKILL.md"], kit["claude-plugin/skills/mail-voice/SKILL.md"]);
        Assert.Equal(content["skills/mail-voice/examples.md"], kit["claude-plugin/skills/mail-voice/examples.md"]);
    }

    // ---------- kit.json, the brief, the grants, the files ----------

    [Fact]
    public void KitJson_SaysWhatTheKitIsTheSecretNamesAndWhereEachFileCameFrom()
    {
        var kit = Json(Compose(Source(Content(), "outreach", FactoryKits.Seat))["kit.json"]);

        Assert.Equal("devthrottle", kit.GetProperty("factory").GetString());
        Assert.Equal("outreach", kit.GetProperty("seat").GetString());
        Assert.Equal(7, kit.GetProperty("version").GetInt32());
        Assert.Equal("seat", kit.GetProperty("kind").GetString());
        Assert.Equal("2026-10-15T09:30:00Z", kit.GetProperty("builtAtUtc").GetString());
        Assert.Equal(new[] { "outreach-smtp-password" }, Strings(kit.GetProperty("secrets")));

        var from = kit.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("path").GetString()!, f => Strings(f.GetProperty("from")));
        Assert.Equal(new[] { "seats/outreach.yaml" }, from["brief.md"]);
        Assert.Equal(new[] { "grants.yaml" }, from["grants.md"]);
        Assert.Equal(new[] { "rules/outreach.json", "rules/base.json", "rules/review.json" }, from["rules.json"]);
        Assert.Equal(new[] { "files/outreach_send.py" }, from["files/outreach_send.py"]);
        Assert.Equal(new[] { "skills/mail-voice/SKILL.md" }, from["claude-plugin/skills/mail-voice/SKILL.md"]);
        Assert.Empty(from["records/journal.md"]);
        Assert.DoesNotContain("kit.json", from.Keys);
    }

    [Fact]
    public void Compose_TheBriefAndTheFiles_AreTheDefinitionsTextAsStored()
    {
        var content = Content();
        var kit = Compose(Source(content, "outreach", FactoryKits.Seat));

        Assert.Equal("Find prospects.\n", kit["brief.md"]);
        Assert.Equal(content["files/outreach_send.py"], kit["files/outreach_send.py"]);
    }

    [Fact]
    public void Grants_ListsEachGrantTheSeatUsesWithWhatItAllowsAndItsLimits()
    {
        var grants = Compose(Source(Content(), "outreach", FactoryKits.Seat))["grants.md"];

        Assert.Contains("## send-outreach-email", grants);
        Assert.Contains("- Allows: Send email to prospects", grants);
        Assert.Contains("- Limits: At most 20 a day", grants);
        Assert.Contains("This seat uses no grant.", Compose(Source(Content(), "writer", FactoryKits.Seat))["grants.md"]);
    }

    // ---------- the records ----------

    private static FactoryKitJournalEntry Entry(int n, string text) =>
        new(Guid.Parse($"75000000-0000-4000-8000-{n:D12}"), $"2026-10-{(n % 28) + 1:D2}", text, "session", "s-1",
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(n));

    [Fact]
    public void Journal_CarriesAtMostThirtyEntriesNewestFirstAndSaysHowManyAreLeftOut()
    {
        var entries = Enumerable.Range(1, 30).Select(n => Entry(100 - n, $"entry number {100 - n}")).ToList();
        var journal = Compose(Source(Content(), "outreach", FactoryKits.Seat, journal: entries, journalTotal: 45))["records/journal.md"];

        Assert.Contains("entry number 99", journal);
        Assert.Contains("entry number 70", journal);
        Assert.True(journal.IndexOf("entry number 99", StringComparison.Ordinal) < journal.IndexOf("entry number 70", StringComparison.Ordinal));
        Assert.Contains("15 older entries are not shown here", journal);
    }

    [Fact]
    public void Journal_StopsBeforeTheEntryThatWouldPass256Kilobytes()
    {
        // Three entries of 100 KB: two fit in 256 KB, the third would not, so two are carried, whole.
        var big = new string('a', 100 * 1024);
        var entries = new[] { Entry(3, "third " + big), Entry(2, "second " + big), Entry(1, "first " + big) };
        var journal = Compose(Source(Content(), "outreach", FactoryKits.Seat, journal: entries))["records/journal.md"];

        Assert.Contains("third ", journal);
        Assert.Contains("second ", journal);
        Assert.DoesNotContain("first ", journal);
        Assert.Contains("1 older entry is not shown here", journal);
        Assert.True(Encoding.UTF8.GetByteCount(journal) < 256 * 1024);
    }

    [Fact]
    public void Journal_ASeatWithNoEntries_SaysSo()
    {
        var journal = Compose(Source(Content(), "outreach", FactoryKits.Seat))["records/journal.md"];

        Assert.Contains("This seat has no journal entries yet.", journal);
        Assert.Contains("cc-devthrottle factory journal add --factory devthrottle --seat outreach --file <file>", journal);
    }

    [Fact]
    public void Documents_ListsEachDocumentWithTheCommandThatReadsItAndTheAttachmentsListedOnly()
    {
        var docs = new[] { new FactoryDocumentSummary("outreach/ledger.json", 4, 1200, "session", "s-1", Built) };
        var attachment = new FactoryKitAttachment(Guid.Parse("76000000-0000-4000-8000-000000000001"), "proof.png", "outreach", 5000, Built);
        var text = Compose(Source(Content(), "outreach", FactoryKits.Seat, documents: docs,
            attachments: new[] { attachment }, attachmentsTotal: 3))["records/documents.md"];

        Assert.Contains("| outreach/ledger.json | 4 | 1200 | `cc-devthrottle factory doc get --factory devthrottle --name outreach/ledger.json` |", text);
        Assert.Contains("`cc-devthrottle factory attach get --factory devthrottle --id 76000000-0000-4000-8000-000000000001 --out <file>`", text);
        Assert.Contains("2 more attachments are not listed", text);
    }

    // ---------- what the composer refuses ----------

    [Fact]
    public void Compose_ASeatTheVersionDoesNotHold_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => FactoryKitComposer.Compose(Source(Content(), "nobody", FactoryKits.Seat)));

        Assert.Contains("no seat 'nobody'", ex.Message);
    }
}
