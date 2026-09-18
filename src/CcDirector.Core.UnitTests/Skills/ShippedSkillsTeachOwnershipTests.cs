using Xunit;

namespace CcDirector.Core.UnitTests.Skills;

/// <summary>
/// A SHIPPED SKILL MUST NOT TEACH A COMMAND THE PRODUCT REFUSES.
///
/// The skills in <c>Skills/Content</c> are what every agent on every machine fetches from the Gateway to
/// learn how to drive the fleet. They are embedded resources, and they are SEPARATE FILES from the
/// <c>.claude/skills/*/SKILL.md</c> copies in this repository - nothing syncs the two, so they drift by
/// default and there was nothing to notice when they did.
///
/// WHAT THIS CAUGHT, on 2026-09-14. A session-initiated spawn has had to declare who will own the result
/// since v2.1.2 (2026-09-13), or it is refused. The repository's own skill copies were updated for that.
/// The SHIPPED ones were not:
///
///   - <c>fleet-comms.skill.md</c> showed FIVE spawn examples and mentioned neither
///     <c>--controlled-by</c> nor <c>--standalone</c> anywhere in the file. Every one of those examples
///     would have been refused.
///   - <c>dev-throttle.skill.md</c> pointed readers AT fleet-comms "for the full flag set
///     (... <c>--controlled-by</c> ...)" - a trail that ended at a document which never mentioned it.
///
/// So for a day the product shipped instructions that could not work, to every user, while the
/// repository copy read correctly to anyone checking. That is the failure this guard exists to make
/// impossible, and the reason it reads the SHIPPED files rather than the ones a developer sees.
/// </summary>
public sealed class ShippedSkillsTeachOwnershipTests
{
    private const string Spawn = "cc-devthrottle session spawn";

    /// <summary>A trailing backslash continues a shell example onto the next line.</summary>
    private const string Continuation = "\\";

    /// <summary>How a spawn may say who owns the result. Kept in one place so a fifth spelling has to be
    /// added deliberately rather than by a test quietly accepting it.</summary>
    private static readonly string[] Declarations = { "--controlled-by", "--standalone" };

    [Fact]
    public void Every_shipped_spawn_example_says_who_will_own_the_result()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.GetFiles(SkillContentDirectory(), "*.skill.md"))
        {
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(Spawn, StringComparison.Ordinal)) continue;

                // A spawn example is the command line plus ITS OWN continuations - and no further. The
                // first cut of this test read a fixed eight-line window, which swallowed the neighbouring
                // examples inside the same code block: strip the declaration off one example and the test
                // still PASSED, because the example below it had one. Caught by running the control.
                // A check that cannot fail for the case it exists for is not a check.
                var example = new List<string> { lines[i] };
                for (var j = i; j + 1 < lines.Length
                                && lines[j].TrimEnd().EndsWith(Continuation, StringComparison.Ordinal); j++)
                    example.Add(lines[j + 1]);
                var window = string.Join("\n", example);
                if (Declarations.Any(d => window.Contains(d, StringComparison.Ordinal))) continue;

                // Prose ABOUT the command rather than a command to run - "create sessions with
                // `cc-devthrottle session spawn`, not a raw HTTP call" - is not an example and has
                // nothing to declare. An example is a line that STARTS with the command.
                if (!lines[i].TrimStart().StartsWith(Spawn, StringComparison.Ordinal)) continue;

                offenders.Add($"{Path.GetFileName(path)}:{i + 1}  {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A shipped skill shows a spawn that declares no owner, and the product REFUSES those - so " +
            "this is an instruction that cannot work, published to every agent on every machine:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nAdd --controlled-by self (you collect the work), --controlled-by <id>, or " +
            "--standalone --why \"<reason>\" (the user owns it).");
    }

    [Fact]
    public void The_skill_an_agent_is_pointed_at_actually_explains_ownership()
    {
        // dev-throttle defers to fleet-comms for the flag set, so fleet-comms is where an agent lands
        // when it wants to know how to spawn. It is the one file that must carry the rule itself rather
        // than referring onward - a trail that ends in a document which never mentions the thing is how
        // this was broken, and a per-example check alone would not have caught it.
        var text = File.ReadAllText(Path.Combine(SkillContentDirectory(), "fleet-comms.skill.md"));

        Assert.True(text.Contains("--controlled-by", StringComparison.Ordinal),
            "fleet-comms.skill.md must explain --controlled-by. It is the skill dev-throttle points at " +
            "for the flag set, and on 2026-09-14 it did not mention ownership at all.");
        Assert.True(text.Contains("--standalone", StringComparison.Ordinal),
            "fleet-comms.skill.md must explain --standalone - the way a session says the USER owns the " +
            "result - so an agent knows the choice exists and is not simply refused.");
        Assert.True(text.Contains("--why", StringComparison.Ordinal),
            "fleet-comms.skill.md must say that --standalone states a reason (v2.1.3). An agent that " +
            "learns the flag but not the requirement gets refused and has to discover why on its own.");
    }

    /// <summary>
    /// THE FLEET MANAGER TAKES OVER A SESSION ONLY WHEN THE OWNER HAS ASKED (the Fleet Manager mission, step 8). Its
    /// session key may hand a session over, so what it is taught decides whether it quietens sessions nobody asked it
    /// to. Both texts it reads - the skill and the workflow - say so, and name the exact command in both directions.
    /// </summary>
    [Theory]
    [InlineData("Skills", "fleet-manager.skill.md")]
    [InlineData("Workflows", "fleet-manager.instructions.md")]
    public void The_Fleet_Manager_is_taught_to_take_over_only_when_the_owner_asked_and_how(string area, string file)
    {
        var path = Path.Combine(Path.GetDirectoryName(SkillContentDirectory())!, "..", area, "Content", file);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");

        Assert.Contains("only when the owner has asked you to", text, StringComparison.Ordinal);
        Assert.Contains("cc-devthrottle session hand-over <session> --to fleet-manager", text, StringComparison.Ordinal);
        Assert.Contains("cc-devthrottle session hand-over <session> --to owner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Until the product can change a session's", text, StringComparison.Ordinal);
        Assert.DoesNotContain("you cannot take a session yourself", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped skill directory, found from this source file's own path - the pattern the other
    /// document guards in this repository use, because the suites run from a checkout and a bin-relative
    /// path breaks under different runners.
    /// </summary>
    private static string SkillContentDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Gateway", "Skills", "Content");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find src/CcDirector.Gateway/Skills/Content above " + AppContext.BaseDirectory +
            ". This guard reads the SHIPPED skill files; without them it must fail loudly rather than " +
            "pass over nothing.");
    }
}
