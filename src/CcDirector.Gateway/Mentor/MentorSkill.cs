using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The mentor's skill (SKILL.md) and rubric (rubric.md) as the Gateway carries them: embedded resources copied
/// VERBATIM from the internal repository, each with ONE leading comment line naming the commit they were copied at
/// (<c>&lt;!-- copied verbatim from ... at commit &lt;hash&gt; ... --&gt;</c>). The skill is single-sourced there; the
/// product carries this copy the way it carries workflow instructions. The comment line is stripped before the text
/// reaches a prompt, so what the model reads is the reference's file byte for byte.
/// </summary>
public static class MentorSkill
{
    public const string SkillResource = "CcDirector.Gateway.Mentor.Skill.SKILL.md";
    public const string RubricResource = "CcDirector.Gateway.Mentor.Skill.rubric.md";
    private static readonly Regex CommitRe = new(@"at commit ([0-9a-f]{7,40})", RegexOptions.CultureInvariant);
    private static readonly Regex TableToolRe = new(@"^\|\s*`([a-z_]+)", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>One resource: the commit line it was copied at and its body without that line.</summary>
    public sealed record Text(string Commit, string Body);

    public static Text Skill() => Read(SkillResource);

    public static Text Rubric() => Read(RubricResource);

    /// <summary>The tool names in SKILL.md's tool table, in the table's order: the first backticked token of every
    /// row that starts with a backtick.</summary>
    public static List<string> TableToolNames(string skillBody)
        => TableToolRe.Matches(skillBody).Select(m => m.Groups[1].Value).ToList();

    private static Text Read(string resource)
    {
        using var stream = typeof(MentorSkill).GetTypeInfo().Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The embedded resource " + resource + " is missing from the Gateway assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        if (newline < 0 || !text.StartsWith("<!--", StringComparison.Ordinal))
            throw new InvalidOperationException(resource + " must begin with the one comment line naming the commit it was copied at.");
        var first = text.Substring(0, newline);
        var commit = CommitRe.Match(first);
        if (!commit.Success)
            throw new InvalidOperationException(resource + "'s first line names no commit: " + first);
        var body = text.Substring(newline + 1);
        foreach (var rune in body.EnumerateRunes())
            if (rune.Value > 127)
                throw new InvalidOperationException(resource + " is not ASCII (code " + rune.Value + ").");
        return new Text(commit.Groups[1].Value, body);
    }
}
