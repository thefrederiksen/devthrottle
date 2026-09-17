using Xunit;

namespace CcDirector.Core.UnitTests.Skills;

/// <summary>
/// THE WORDS AGENTS READ MAY NOT TEACH THE RETIRED MESSAGING (Message Load mission, slice 5,
/// 17 September 2026).
///
/// Since the Message Load mission a fleet message is a queued record: nothing is typed into a working
/// session, one doorbell line tells it to read its inbox, and the blocking ask was removed. The text
/// every agent reads - the fleet preamble, the shipped skills and workflows, their repository copies,
/// the plugins, the command line's own help, and the command and messaging references - used to say the
/// opposite in several places. An agent does what it is told, so a sentence that survives there
/// brings the old behaviour back one session at a time. This guard fails if any of those sentences
/// returns.
///
/// It scans TEXT, not code: the Gateway still recognises the old ask from an older command line in
/// order to refuse it, and its comments and refusal sentence name it. Those are not taught to anyone.
///
/// Presence, not absence (skill checks-that-fail-open): the guard proves it read the files it names,
/// and that the preamble still teaches the inbox, so a scan that found nothing cannot pass.
/// </summary>
public sealed class RetiredMessagingWordsTests
{
    private static readonly string[] RetiredPhrases =
    {
        "message ask",
        "interrupts the receiving",
        "interrupts the session that receives",
        "truncate at the first newline",
        "--controlled-by <session-id>",
    };

    [Fact]
    public void No_text_an_agent_reads_teaches_the_retired_messaging()
    {
        var files = TaughtFiles();
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var phrase in RetiredPhrases)
                {
                    if (lines[i].Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}: \"{phrase}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Text agents read still teaches the messaging the Message Load mission retired (a message " +
            "interrupts, a blocking ask, one-line messages, or naming another session as owner). " +
            "Rewrite it to the queue - see docs/FleetMessaging.md:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_scan_reads_every_named_surface()
    {
        var files = TaughtFiles();
        var relative = files.Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/')).ToHashSet();

        foreach (var required in RequiredFiles)
            Assert.Contains(required, relative);

        Assert.Contains(relative, f => f.StartsWith("src/CcDirector.Gateway/Skills/Content/", StringComparison.Ordinal));
        Assert.Contains(relative, f => f.StartsWith("plugins/", StringComparison.Ordinal));
        Assert.Contains(relative, f => f.StartsWith("docs/public/", StringComparison.Ordinal));
        Assert.Contains(relative, f => f.StartsWith("tools/cc-devthrottle/src/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_preamble_and_the_skill_teach_the_inbox()
    {
        var preamble = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Core", "Sessions", "FleetPreambleTemplate.cs"));
        Assert.Contains("cc-devthrottle message inbox", preamble, StringComparison.Ordinal);
        Assert.Contains("MESSAGES ARE RARE", preamble, StringComparison.Ordinal);

        var skill = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Skills", "Content", "fleet-comms.skill.md"));
        Assert.Contains("cc-devthrottle message inbox", skill, StringComparison.Ordinal);
        Assert.Contains("--reply-wanted", skill, StringComparison.Ordinal);
        Assert.Contains("Most sessions can message nobody", skill, StringComparison.Ordinal);
    }

    private static readonly string[] RequiredFiles =
    {
        "src/CcDirector.Core/Sessions/FleetPreambleTemplate.cs",
        "src/CcDirector.Core/Sessions/SessionManager.cs",
        "src/CcDirector.Gateway/Skills/Content/fleet-comms.skill.md",
        "src/CcDirector.Gateway/Workflows/Content/mission.instructions.md",
        ".claude/skills/fleet-comms/SKILL.md",
        ".claude/skills/mission/SKILL.md",
        "plugins/devthrottle/skills/devthrottle-sessions/SKILL.md",
        "docs/FleetMessaging.md",
        "docs/cli-reference.md",
        "docs/public/tools/01-overview.md",
        "tools/cc-devthrottle/src/cli.py",
        "tools/cc-devthrottle/src/session_ops.py",
    };

    private static List<string> TaughtFiles()
    {
        var root = RepoRoot();
        var files = new List<string>
        {
            Path.Combine(root, "src", "CcDirector.Core", "Sessions", "FleetPreambleTemplate.cs"),
            Path.Combine(root, "src", "CcDirector.Core", "Sessions", "SessionManager.cs"),
            Path.Combine(root, "docs", "FleetMessaging.md"),
            Path.Combine(root, "docs", "cli-reference.md"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "CcDirector.Gateway", "Skills", "Content"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "CcDirector.Gateway", "Workflows", "Content"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "plugins"), "*.md", SearchOption.AllDirectories));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "docs", "public"), "*.md", SearchOption.AllDirectories));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "tools", "cc-devthrottle", "src"), "*.py"));

        // The repository copies of the shipped skills and of the mission workflow.
        foreach (var id in new[] { "fleet-comms", "mission", "terminology", "dev-throttle", "move-session" })
        {
            var copy = Path.Combine(root, ".claude", "skills", id, "SKILL.md");
            if (File.Exists(copy)) files.Add(copy);
        }

        foreach (var file in files)
            Assert.True(File.Exists(file), $"The retired-words guard expected to read {file}, and it is not there.");
        return files;
    }

    /// <summary>The repository root, walked up from the test binary. Fails loudly: a guard with nothing
    /// to read certifies a run that never happened.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Gateway", "Skills", "Content")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root above " + AppContext.BaseDirectory +
            ". This guard reads the text agents are taught; without the checkout there is nothing to read.");
    }
}
