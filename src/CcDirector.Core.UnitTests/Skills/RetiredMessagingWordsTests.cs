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
/// It also bans one COMMAND: <c>cc-devthrottle session prompt</c>, which the Gateway refuses to every
/// session key the owner has not raised, must not be taught to an agent as the way to answer a session. See
/// <see cref="RetiredTypingCommand"/> for why that one carries its own exemption list.
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
        "truncates at the first newline",
        "cut off at the first line break",
        "--controlled-by <session-id>",
        "--controlled-by takes any",
    };

    /// <summary>
    /// The typing command no shipped conduct may TEACH an agent to run (the Architect's ruling on the
    /// second merge with main, 17 September 2026). The Gateway refuses
    /// <c>POST /sessions/{id}/prompt</c> to every session key - <c>SessionKeyGuard.IsAgentInput</c>
    /// matches it before the allow list is consulted - and the Fleet Manager is a session, so its key
    /// is one of those keys. Main's new built-in Fleet Manager conduct nevertheless told it to run
    /// <c>cc-devthrottle session prompt</c> to pass the owner's answer on, which is a command it would
    /// be told 403 for. The owner's words reach a session as a queued message and one doorbell.
    ///
    /// This phrase is kept apart from the lists above because, unlike them, it has a LEGITIMATE use in
    /// text an agent reads: the command still exists in order to print the Gateway's refusal, and two
    /// skills name it to teach that typing is the owner's alone. Those files are named in
    /// <see cref="TypingCommandExemptions"/>, one by one, with the reason.
    ///
    /// THE PREMISE MOVED ON 20 SEPTEMBER 2026 (the Fleet Manager Improvement mission, phase 1, issue #3177).
    /// A session the owner has RAISED acts with the owner's permissions inside his account, and the guard
    /// lets its key through the typing routes - recorded each time. So the command is no longer refused
    /// to EVERY session key, and the Fleet Manager's own conduct must say what a raised Fleet Manager may
    /// do, by the command's name. Those two texts are exempted below for that reason and no other; every
    /// other text is still held to the ban, because every other session is still refused.
    ///
    /// AND AGAIN ON 26 SEPTEMBER 2026 (Parent Control, fix 1). Any session may now type into a session it
    /// OWNS, only when that session is waiting and never over the owner's unsent words; every other target
    /// is still refused. So the command may be named where that rule is stated - the Gateway's own refusal
    /// sentence and its test, the command line, and the skills that teach the rule - and still nowhere else,
    /// because a queued message remains the way to reach every session the caller does not own.
    /// </summary>
    private const string RetiredTypingCommand = "cc-devthrottle session prompt";

    /// <summary>
    /// Paths (relative, forward slashes) that may name <see cref="RetiredTypingCommand"/> because they
    /// document the refusal rather than teach the command. Nothing else in the inventory may.
    /// </summary>
    private static readonly (string Path, string Why)[] TypingCommandExemptions =
    {
        ("tools/cc-devthrottle/src/session_ops.py",
            "the command itself: its docstring and its blank-text usage name it, and running it prints the Gateway's refusal"),
        ("src/CcDirector.Gateway/Skills/Content/fleet-comms.skill.md",
            "teaches that typing into a session is the owner's, and a raised session's, by naming the command refused to everyone else"),
        ("src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md",
            "says what a Fleet Manager the owner has RAISED may do, which includes this command, and that an unraised one is refused it"),
        ("src/CcDirector.Gateway/Workflows/Content/fleet-manager.instructions.md",
            "the Fleet Manager's conduct: names the command only under the rule on what raised allows and forbids"),
        (".claude/skills/fleet-comms/SKILL.md",
            "the repository copy of that skill, which is the shipped body plus frontmatter"),
        ("src/CcDirector.Gateway/Util/SessionKeyGuard.cs",
            "the Gateway's refusal sentence, which names the command a session may use on a session it owns"),
        ("src/CcDirector.Gateway.UnitTests/SessionKeyGuardTests.cs",
            "asserts that refusal sentence names the command"),
        ("src/CcDirector.Gateway/Messaging/FleetDoorbell.cs",
            "the unreachable notice (issue 3289) tells a sender whose message could not ring that, if it owns the session, it may type into it - the fix 1 rule"),
        ("src/CcDirector.Gateway.UnitTests/Messaging/FleetDoorbellUnreachableTests.cs",
            "asserts that unreachable notice names the command"),
    };

    /// <summary>The phrases to scan <paramref name="relative"/> for: the retired words always, and the
    /// typing command unless this is one of the files that legitimately documents its refusal.</summary>
    private static IReadOnlyList<string> PhrasesFor(string relative, IReadOnlyList<string> basePhrases)
    {
        var exempt = TypingCommandExemptions.Any(e => string.Equals(relative, e.Path, StringComparison.Ordinal));
        if (exempt) return basePhrases;

        var phrases = new List<string>(basePhrases) { RetiredTypingCommand };
        return phrases;
    }

    [Fact]
    public void No_text_an_agent_reads_teaches_the_retired_messaging()
    {
        var files = TaughtFiles();
        var offenders = new List<string>();

        foreach (var file in files)
            offenders.AddRange(Scan(file, PhrasesFor(Relative(RepoRoot(), file), RetiredPhrases)));

        Assert.True(offenders.Count == 0,
            "Text agents read still teaches the messaging the Message Load mission retired (a message " +
            "interrupts, a blocking ask, one-line messages, naming another session as owner, or typing " +
            "into a session with 'cc-devthrottle session prompt', which the Gateway refuses to every " +
            "session key for a session it does not own). Rewrite it to the queue - the owner's words reach a session as a queued " +
            "message and one doorbell; see docs/FleetMessaging.md:\n  " + string.Join("\n  ", offenders));
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Every phrase in <paramref name="file"/>, matched with runs of whitespace collapsed to one space, so a
    /// sentence wrapped across two lines of prose is still found. Reports the line the match starts on. A
    /// phrase split by a comment marker (a wrapped "///" line) is not joined - a known limit.
    /// </summary>
    private static IEnumerable<string> Scan(string file, IReadOnlyList<string> phrases)
    {
        var text = File.ReadAllText(file);
        var collapsed = new System.Text.StringBuilder(text.Length);
        var lineOf = new List<int>(text.Length);
        var line = 1;
        var inSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace) { collapsed.Append(' '); lineOf.Add(line); inSpace = true; }
            }
            else
            {
                collapsed.Append(c); lineOf.Add(line); inSpace = false;
            }
            if (c == '\n') line++;
        }

        var flat = collapsed.ToString();
        foreach (var phrase in phrases)
        {
            var at = flat.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                yield return $"{Relative(RepoRoot(), file)}:{lineOf[at]}: \"{phrase}\"";
                at = flat.IndexOf(phrase, at + phrase.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
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

        // The Fleet Manager passes the owner's answer on, so its skill must teach the queue and the
        // doorbell rather than just not teach the typing command: an empty section would pass a
        // ban and teach nothing.
        var fleetManager = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Skills", "Content", "fleet-manager.skill.md"));
        Assert.Contains("## Answering a session", fleetManager, StringComparison.Ordinal);
        Assert.Contains("cc-devthrottle message send <session>", fleetManager, StringComparison.Ordinal);
        Assert.Contains("queued message and one doorbell at the next safe moment", fleetManager, StringComparison.Ordinal);
    }

    private static readonly string[] RequiredFiles =
    {
        "src/CcDirector.Core/Sessions/FleetPreambleTemplate.cs",
        "src/CcDirector.Core/Sessions/SessionManager.cs",
        "src/CcDirector.Gateway/Skills/Content/fleet-comms.skill.md",
        "src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md",
        "src/CcDirector.Gateway/Workflows/Content/mission.instructions.md",
        ".claude/skills/fleet-comms/SKILL.md",
        "plugins/devthrottle/skills/devthrottle-sessions/SKILL.md",
        "docs/FleetMessaging.md",
        "docs/cli-reference.md",
        "docs/public/tools/01-overview.md",
        "tools/cc-devthrottle/src/cli.py",
        "tools/cc-devthrottle/src/session_ops.py",
        "docs/new_architecture/sessions.html",
        "src/CcDirector.Gateway.Contracts/FleetMessaging.cs",
        "src/CcDirector.Gateway.Contracts/SessionTree.cs",
        "packages/client-core/src/sessions/tree.ts",
        "tools/cc-ship/src/fleet.py",
        "ARCHITECT-HANDOVER.md",
        "missions/stop-a-session/worker-e-director-window.md",
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
            // Added by the final fix round (inspection 11, ruling 3): the law page, and every file fixed there.
            Path.Combine(root, "docs", "new_architecture", "sessions.html"),
            Path.Combine(root, "src", "CcDirector.Gateway.Contracts", "FleetMessaging.cs"),
            Path.Combine(root, "src", "CcDirector.Gateway.Contracts", "SessionTree.cs"),
            Path.Combine(root, "packages", "client-core", "src", "sessions", "tree.ts"),
            Path.Combine(root, "tools", "cc-ship", "src", "fleet.py"),
            Path.Combine(root, "ARCHITECT-HANDOVER.md"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "missions", "stop-a-session"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, ".claude", "skills", "agent-expert", "agents"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "CcDirector.Gateway", "Skills", "Content"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "src", "CcDirector.Gateway", "Workflows", "Content"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "plugins"), "*.md", SearchOption.AllDirectories));
        // Everything published under docs/public EXCEPT the release notes: a release note is dated history
        // of what one released version did, not text that teaches an agent how to work today.
        files.AddRange(Directory.GetFiles(Path.Combine(root, "docs", "public"), "*.md", SearchOption.AllDirectories)
            .Where(f => !Relative(root, f).StartsWith("docs/public/release-notes/", StringComparison.Ordinal)));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "tools", "cc-devthrottle", "src"), "*.py"));

        // The repository copies of the shipped skills and of the mission workflow.
        foreach (var id in new[] { "fleet-comms", "terminology", "dev-throttle", "move-session" })
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
