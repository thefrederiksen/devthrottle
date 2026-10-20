using Xunit;

namespace CcDirector.Core.UnitTests.Skills;

/// <summary>
/// A MOVED SESSION KEEPS ITS CONNECTION (API mode mission #4070, the review of pull request #4277).
///
/// A spawn that names no <c>--connection</c> asks the Gateway for the agent's default, and that default is API mode for
/// Pi. The shipped move-session skill started the target with no <c>--connection</c>, so moving a Pi session that ran in
/// its terminal landed it in API mode - a mode nobody chose. These guards read the SHIPPED file, which is what every
/// agent is served.
/// </summary>
public sealed class MoveSessionKeepsTheConnectionTests
{
    private const string Spawn = "cc-devthrottle session spawn";

    private static string SkillText() =>
        File.ReadAllText(Path.Combine(ShippedSkillsTeachOwnershipTests.SkillContentDirectory(), "move-session.skill.md"))
            .Replace("\r\n", "\n");

    /// <summary>Each spawn example in the skill: the line that starts with the command plus its own continuations.</summary>
    private static List<string> SpawnExamples()
    {
        var lines = SkillText().Split('\n');
        var examples = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith(Spawn, StringComparison.Ordinal)) continue;
            var example = new List<string> { lines[i] };
            for (var j = i; j + 1 < lines.Length && lines[j].TrimEnd().EndsWith('\\'); j++)
                example.Add(lines[j + 1]);
            examples.Add(string.Join("\n", example));
        }
        return examples;
    }

    [Fact]
    public void MoveSessionSkill_EverySpawnExample_PassesTheSourcesConnection()
    {
        // Arrange
        var examples = SpawnExamples();

        // Assert - a presence first, so a skill whose spawn was reworded out of reach cannot pass on nothing.
        Assert.NotEmpty(examples);
        var missing = examples.Where(e => !e.Contains("--connection", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "move-session.skill.md starts the target with no --connection, so a Pi session that ran in its terminal " +
            "is moved into API mode (the Gateway's default for Pi):\n" + string.Join("\n---\n", missing));
    }

    [Fact]
    public void MoveSessionSkill_ReadsTheSourcesConnectionAndComparesTheTargets()
    {
        // Arrange
        var text = SkillText();

        // Assert - step 1 reads it as the word the spawn takes, and the read-back compares it.
        Assert.Contains("cc-devthrottle session list --fields id,name,agent,connection", text, StringComparison.Ordinal);
        Assert.Contains("cc-devthrottle session list --fields id,agent,connection", text, StringComparison.Ordinal);
        Assert.Contains("If the agent, the model or the connection differ, stop.", text, StringComparison.Ordinal);
    }
}
