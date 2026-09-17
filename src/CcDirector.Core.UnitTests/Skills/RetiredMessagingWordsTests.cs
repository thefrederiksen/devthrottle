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
        "truncates at the first newline",
        "cut off at the first line break",
        "--controlled-by <session-id>",
        "--controlled-by takes any",
    };

    [Fact]
    public void No_text_an_agent_reads_teaches_the_retired_messaging()
    {
        var files = TaughtFiles();
        var offenders = new List<string>();

        foreach (var file in files)
            offenders.AddRange(Scan(file, RetiredPhrases));

        Assert.True(offenders.Count == 0,
            "Text agents read still teaches the messaging the Message Load mission retired (a message " +
            "interrupts, a blocking ask, one-line messages, or naming another session as owner). " +
            "Rewrite it to the queue - see docs/FleetMessaging.md:\n  " + string.Join("\n  ", offenders));
    }

    // =================================================================================================
    // THE WHOLE TREE (inspection 11, ruling 3). The inventory above is the text agents are TAUGHT; the
    // inspector's search found the old words outside it - a code comment documenting a live parameter,
    // repository instructions, the briefs of a mission still marked active - and the inventory stayed
    // green. This is that search, over every text file in the repository, with the exemptions written
    // down one by one instead of left to a narrower inventory.
    // =================================================================================================

    /// <summary>The inspector's search expression, word for word, plus the two wrappings of the newline
    /// sentence found while fixing its hits.</summary>
    private static readonly string[] TreePhrases =
    {
        "message ask",
        "interrupts the session that receives",
        "truncate at the first newline",
        "--controlled-by takes any",
        "truncates at the first newline",
        "cut off at the first line break",
    };

    /// <summary>
    /// Paths (relative, forward slashes) the tree scan does not read. A path ending in "/" exempts everything
    /// under it. Every entry says why. Adding one is a decision a reviewer should see, which is the point of
    /// keeping them here.
    /// </summary>
    private static readonly (string Path, string Why)[] TreeExemptions =
    {
        // ---- The mission record and history: what was decided and done at the time, in the words of the time.
        ("docs/missions/", "mission records, including this mission's own, which quote the retired words to retire them"),
        ("docs/plans/", "dated plans and QA reports of work that shipped before the Message Load mission"),
        ("docs/reviews/", "dated reviews of the command line as it was when reviewed"),
        ("docs/MISSION-source-control-tab-2026-07-23.md", "a dated mission document, 23 July 2026"),
        ("docs/MISSION-cockpit-fix-2026-07-23.md", "a dated mission document, 23 July 2026"),
        ("docs/MISSION-multilingual.md", "a finished mission, merged 30 July 2026 (#2295)"),
        ("docs/MISSION-multilingual-INSPECTION.md", "the inspection brief of that finished mission"),
        ("PHASE-2-REPORT.md", "a phase report of the remove-the-network-port mission, August 2026"),
        ("research/multi-vs-single-token-study/", "a recorded study: its plan and raw session transcripts are evidence, not instructions"),

        // ---- Code and tests that name the retired words in order to refuse or forbid them.
        ("src/CcDirector.Gateway/Api/GatewayEndpoints.cs", "refuses an older command line's ask, and says so in its refusal"),
        ("src/CcDirector.Gateway.Contracts/FleetMessageRequests.cs", "documents the retired field it still reads in order to refuse it"),
        ("src/CcDirector.Gateway.Tests/FleetMessageRouteTests.cs", "asserts the refusal sentence"),
        ("src/CcDirector.Core.Tests/Sessions/FleetPreambleTests.cs", "asserts the preamble does NOT contain the retired verb"),
        ("src/CcDirector.Core.Tests/Pi/PiPreambleWriterTests.cs", "asserts the Pi preamble does NOT contain the retired verb"),
        ("src/CcDirector.Core.UnitTests/Skills/RetiredMessagingWordsTests.cs", "this guard, which lists the phrases"),
        ("tools/cc-devthrottle/tests/test_message_queue.py", "asserts the retired verb is gone from the commands and the catalogue"),
        ("tools/cc-devthrottle/tests/test_help_and_errors_axi.py", "records in a docstring that the verb was removed"),
        ("tools/cc-devthrottle/tests/test_axi_step_6b_recheck5.py", "records in a docstring that the verb was removed"),
        ("tools/cc-devthrottle/tests/test_stale_answer_caution.py", "a docstring naming the commands the helper once served"),
    };

    private static readonly HashSet<string> TreeSkippedDirectories = new(StringComparer.Ordinal)
    {
        ".git", "bin", "obj", "node_modules", "dist", ".venv", "venv", "__pycache__", ".pytest_cache", "TestResults",
    };

    private static readonly HashSet<string> TreeTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".html", ".cs", ".ts", ".tsx", ".py", ".txt", ".json", ".yml", ".yaml", ".ps1", ".sh", ".axaml", ".css", ".js", ".toml",
    };

    [Fact]
    public void Nothing_in_the_repository_outside_the_named_history_uses_the_retired_messaging_words()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var read = 0;

        foreach (var file in TreeFiles(root))
        {
            read++;
            offenders.AddRange(Scan(file, TreePhrases));
        }

        // Presence, not absence: a walk that read almost nothing would pass. The repository holds thousands
        // of text files, and the files this round fixed must have been among those read.
        Assert.True(read > 1000, $"The tree scan read only {read} files under {root}; it is not reading the repository.");
        Assert.True(offenders.Count == 0,
            "The retired messaging words are back outside the named history (a blocking ask, a message that " +
            "interrupts, one-line messages, or naming any session as owner). Rewrite the text to the queue - " +
            "see docs/FleetMessaging.md - or, if it is genuinely a dated record, add it to TreeExemptions with " +
            "the reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_tree_scan_reads_the_files_this_round_fixed_and_every_exemption_exists()
    {
        var root = RepoRoot();
        var read = TreeFiles(root).Select(f => Relative(root, f)).ToHashSet(StringComparer.Ordinal);

        foreach (var fixedFile in FixedInTheFinalRound)
            Assert.Contains(fixedFile, read);

        // An exemption for a path that is gone is a hole waiting for a new file of that name.
        foreach (var (path, _) in TreeExemptions)
            Assert.True(path.EndsWith('/') ? Directory.Exists(Path.Combine(root, path)) : File.Exists(Path.Combine(root, path)),
                $"The exemption '{path}' names nothing in the repository; remove it.");
    }

    /// <summary>Files inspection 11 found with the old words and the final fix round rewrote.</summary>
    private static readonly string[] FixedInTheFinalRound =
    {
        "docs/new_architecture/sessions.html",
        "src/CcDirector.Gateway.Contracts/FleetMessaging.cs",
        "src/CcDirector.Gateway.Contracts/SessionTree.cs",
        "packages/client-core/src/sessions/tree.ts",
        "tools/cc-ship/src/fleet.py",
        "ARCHITECT-HANDOVER.md",
        "missions/stop-a-session/worker-e-director-window.md",
        ".claude/skills/agent-expert/agents/README.md",
    };

    private static IEnumerable<string> TreeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (TreeSkippedDirectories.Contains(Path.GetFileName(sub))) continue;
                if (IsExempt(Relative(root, sub) + "/")) continue;
                pending.Push(sub);
            }
            foreach (var file in Directory.GetFiles(dir))
            {
                if (!TreeTextExtensions.Contains(Path.GetExtension(file))) continue;
                if (IsExempt(Relative(root, file))) continue;
                yield return file;
            }
        }
    }

    private static bool IsExempt(string relative)
        => TreeExemptions.Any(e => e.Path.EndsWith('/')
            ? relative.StartsWith(e.Path, StringComparison.Ordinal)
            : string.Equals(relative, e.Path, StringComparison.Ordinal));

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
