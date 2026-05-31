using System.Text.Json.Serialization;

namespace CcDirector.Core.Tools;

/// <summary>How strongly a skill is tied to a tool.</summary>
public enum SkillLinkRelation
{
    /// <summary>The skill is primarily about this tool.</summary>
    Drives,

    /// <summary>The skill uses this tool among others.</summary>
    Uses,
}

/// <summary>Whether a link was inferred by scanning skill files or declared in the override file.</summary>
public enum SkillLinkSource
{
    /// <summary>Inferred from a cc-* mention in the skill's file.</summary>
    Discovered,

    /// <summary>Hand-curated in skill-tool-overrides.json.</summary>
    Declared,
}

/// <summary>A single skill-to-tool link surfaced in the Skills tab.</summary>
public sealed class SkillToolLink
{
    public SkillToolLink(string toolName, string skillName, SkillLinkRelation relation, SkillLinkSource source)
    {
        ToolName = toolName;
        SkillName = skillName;
        Relation = relation;
        Source = source;
    }

    public string ToolName { get; }
    public string SkillName { get; }
    public SkillLinkRelation Relation { get; }
    public SkillLinkSource Source { get; }
}

/// <summary>Deserialized shape of skill-tool-overrides.json.</summary>
internal sealed class SkillToolOverrides
{
    [JsonPropertyName("overrides")]
    public Dictionary<string, List<OverrideEntry>> Overrides { get; set; } = new();

    internal sealed class OverrideEntry
    {
        [JsonPropertyName("skill")]
        public string Skill { get; set; } = "";

        [JsonPropertyName("relation")]
        public string Relation { get; set; } = "uses";
    }
}
