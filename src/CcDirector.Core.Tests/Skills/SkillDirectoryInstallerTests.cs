using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// Installing the fleet's skills where each agent looks for them.
///
/// The properties worth pinning are the ones that go wrong SILENTLY: overwriting one of the owner's
/// own skills, leaving a withdrawn skill on disk, writing a SKILL.md no agent will actually accept,
/// and - now that there is one real copy with links pointing at it - deleting through a link and
/// emptying the copy every other agent reads.
/// </summary>
public sealed class SkillDirectoryInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "skill-install-tests-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "store");

    /// <summary>Stands in for <c>~/.agents/skills</c> - the one directory a skill is written into.</summary>
    private string Shared => Path.Combine(_root, "shared");

    /// <summary>Stands in for <c>~/.claude/skills</c> - an agent's own directory, which gets links.</summary>
    private string LinkRoot => Path.Combine(_root, "agent-own");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        // Every link is unlinked before the sweep. A recursive delete over a tree containing a
        // junction fails outright on Windows, which is worth knowing in its own right: it is the same
        // reason the installer removes one of its links as a link and never recursively.
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static SkillBundle Bundle(
        string id = "demo-skill",
        string summary = "Does a useful thing.",
        IReadOnlyList<SkillFileBytes>? files = null) =>
        new(
            Id: id,
            Version: 3,
            ContentHash: "bundle-hash-3",
            Summary: summary,
            Triggers: new[] { "do the thing", "demo" },
            BodyMarkdown: "# Demo\n\nDo the thing.\n",
            Files: files ?? Array.Empty<SkillFileBytes>());

    [Fact]
    public void A_materialized_skill_is_a_standard_skill_directory()
    {
        Directory.CreateDirectory(Store);
        var raw = new byte[] { 0x00, 0xFF, 0x10 };

        var directory = SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/tracing.md", Encoding.UTF8.GetBytes("# Tracing\n"), false),
            new SkillFileBytes("assets/logo.png", raw, false),
            new SkillFileBytes("scripts/build.sh", Encoding.UTF8.GetBytes("echo hi\n"), true),
        }));

        // SKILL.md at the root and the files at their own paths - that IS the standard, and it is what
        // every agent this product supervises reads.
        var skillMd = File.ReadAllText(Path.Combine(directory, "SKILL.md"));
        Assert.StartsWith("---\n", skillMd);
        Assert.Contains("name: demo-skill", skillMd);
        Assert.Contains("Does a useful thing.", skillMd);
        Assert.Contains("# Demo", skillMd);
        Assert.Equal("# Tracing\n", File.ReadAllText(Path.Combine(directory, "references", "tracing.md")));
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(directory, "assets", "logo.png")));
        Assert.True(File.Exists(Path.Combine(directory, "scripts", "build.sh")));
    }

    [Fact]
    public void The_frontmatter_name_is_the_id_because_that_is_what_agents_validate()
    {
        // The standard requires a lowercase slug that MATCHES THE DIRECTORY NAME. A display name like
        // "Move a session" would fail validation in every agent, so the id is what goes in the file
        // even though it is not what a person would call the skill.
        var skillMd = SkillMarkdown.Compose(
            "move-session", "Relocate a live session: through the Gateway.", new[] { "move session" },
            "# Move\n");

        Assert.Contains("name: move-session", skillMd);
        // The summary contains a colon, which is why every scalar is quoted rather than only the ones
        // that look like they need it - unquoted, this line would change the document's meaning.
        Assert.Contains("description: \"Relocate a live session: through the Gateway.", skillMd);
        Assert.Contains("Use when the task involves: move session.", skillMd);
    }

    [Fact]
    public void Installing_puts_the_skill_where_the_agent_looks_and_marks_it_as_ours()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        var placement = Install();

        Assert.Equal(1, placement.Reachable);
        Assert.True(File.Exists(Path.Combine(Shared, "demo-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Shared, "demo-skill", SkillDirectoryInstaller.MarkerFileName)));
    }

    [Fact]
    public void There_is_ONE_copy_and_the_agents_own_directory_holds_a_link_to_it()
    {
        // THE PLACEMENT DECISION, PINNED. A skill is materialized once, into the shared directory six
        // of the eight agent families read natively; the two that do not get a link per skill pointing
        // at that one copy. Three copies is three things that can drift. The check that matters is that
        // the entry in the agent's own directory is a LINK - a copy would read identically today and
        // diverge the first time one side is refreshed and the other is not.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        Assert.Equal(1, Install().Reachable);

        var link = Path.Combine(LinkRoot, "demo-skill");
        Assert.True(Directory.Exists(link));
        Assert.NotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
        // The one copy is reached through it, byte for byte - this is the whole claim.
        Assert.Equal(
            File.ReadAllText(Path.Combine(Shared, "demo-skill", "SKILL.md")),
            File.ReadAllText(Path.Combine(link, "SKILL.md")));
    }

    [Fact]
    public void Installing_twice_leaves_one_working_link_and_not_a_broken_one()
    {
        // Every session launch runs this. A link scheme that only works the first time would break on
        // the second session of the day, which is the launch nobody tests.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        Install();
        Assert.Equal(1, Install().Reachable);

        Assert.True(File.Exists(Path.Combine(LinkRoot, "demo-skill", "SKILL.md")));
        Assert.Single(Directory.GetDirectories(LinkRoot));
    }

    [Fact]
    public void A_copy_left_by_the_previous_scheme_becomes_a_link()
    {
        // Machines already carry full copies in the agent's own directory, put there by the scheme this
        // replaced. Left alone they would be a second copy that never refreshes again - the exact drift
        // this change exists to remove - so an entry of ours that is not a link is rebuilt as one.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());
        var stale = Path.Combine(LinkRoot, "demo-skill");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "SKILL.md"), "# OLD COPY\n");
        File.WriteAllText(Path.Combine(stale, SkillDirectoryInstaller.MarkerFileName), "demo-skill\n1\nold\n");

        Install();

        Assert.NotEqual(0, (int)(File.GetAttributes(stale) & FileAttributes.ReparsePoint));
        Assert.Contains("# Demo", File.ReadAllText(Path.Combine(stale, "SKILL.md")));
    }

    [Fact]
    public void A_skill_the_machine_already_had_is_never_overwritten()
    {
        // THE RULE THIS FEATURE MUST NOT BREAK. The library is an ADDITIONAL source of skills; a
        // machine's own skill wins a name clash. Copying over one of the owner's own skills would be
        // silent data loss dressed up as a feature. It holds in BOTH directories: the shared one is
        // also a place the owner may keep skills of their own.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        var mineShared = Path.Combine(Shared, "demo-skill");
        Directory.CreateDirectory(mineShared);
        File.WriteAllText(Path.Combine(mineShared, "SKILL.md"), "# MY OWN VERSION\n");

        var mineOwn = Path.Combine(LinkRoot, "demo-skill");
        Directory.CreateDirectory(mineOwn);
        File.WriteAllText(Path.Combine(mineOwn, "SKILL.md"), "# MY OWN CLAUDE VERSION\n");

        Install();

        Assert.Equal("# MY OWN VERSION\n", File.ReadAllText(Path.Combine(mineShared, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(mineShared, SkillDirectoryInstaller.MarkerFileName)));
        Assert.Equal("# MY OWN CLAUDE VERSION\n", File.ReadAllText(Path.Combine(mineOwn, "SKILL.md")));
        Assert.Equal(0, (int)(File.GetAttributes(mineOwn) & FileAttributes.ReparsePoint));
    }

    [Fact]
    public void A_withdrawn_skill_is_removed_and_a_skill_that_is_not_ours_is_left_alone()
    {
        // Reconcile, never add: a skill switched off on the Gateway must not keep working from disk.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "keeper"));
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "withdrawn"));
        Install();
        Assert.True(Directory.Exists(Path.Combine(Shared, "withdrawn")));
        Assert.True(Directory.Exists(Path.Combine(LinkRoot, "withdrawn")));

        // Somebody else's skill, sitting in the same directory. It is not ours to delete.
        var theirs = Path.Combine(LinkRoot, "not-ours");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "# theirs\n");

        // The Gateway stops serving 'withdrawn'.
        Directory.Delete(Path.Combine(Store, "withdrawn"), recursive: true);
        Install();

        Assert.False(Directory.Exists(Path.Combine(Shared, "withdrawn")));
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "withdrawn")));
        Assert.True(Directory.Exists(Path.Combine(Shared, "keeper")));
        Assert.True(File.Exists(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(theirs, "SKILL.md")));
    }

    [Fact]
    public void Removing_a_withdrawn_link_does_not_empty_the_one_real_copy()
    {
        // A recursive delete applied to a link deletes what the link POINTS AT. Done here it would
        // empty the single copy every other agent family reads, and the only symptom would be those
        // agents quietly losing a skill they never had anything to do with.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "keeper"));
        Install();

        // 'keeper' stays in the store but the agent's link is reconciled away and back, which is what
        // happens whenever a name leaves and rejoins the register.
        Directory.Delete(Path.Combine(LinkRoot, "keeper"), recursive: false);
        Install();

        Assert.True(File.Exists(Path.Combine(Shared, "keeper", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
    }

    [Fact]
    public void A_file_removed_upstream_does_not_survive_inside_an_installed_skill()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/old.md", Encoding.UTF8.GetBytes("old"), false),
        }));
        Install();
        Assert.True(File.Exists(Path.Combine(LinkRoot, "demo-skill", "references", "old.md")));

        SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/new.md", Encoding.UTF8.GetBytes("new"), false),
        }));
        Install();

        Assert.False(File.Exists(Path.Combine(Shared, "demo-skill", "references", "old.md")));
        Assert.False(File.Exists(Path.Combine(LinkRoot, "demo-skill", "references", "old.md")));
        Assert.True(File.Exists(Path.Combine(LinkRoot, "demo-skill", "references", "new.md")));
    }

    [Fact]
    public void A_path_that_escapes_the_skill_directory_is_refused()
    {
        // The Gateway validates paths on write; this is the same rule enforced again at the point
        // where bytes hit this disk, because a store that was ever wrong must not be able to write
        // wherever it likes.
        Directory.CreateDirectory(Store);
        Assert.Throws<InvalidOperationException>(() => SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("../../escape.md", Encoding.UTF8.GetBytes("bad"), false),
        })));
    }

    [Fact]
    public void Nothing_is_installed_when_the_store_has_never_been_filled()
    {
        // A Director that has never reached the Gateway installs nothing and says so. It does NOT
        // fail, because a session must still launch - but it must not read as healthy either.
        var placement = SkillDirectoryInstaller.InstallFor(AgentKind.ClaudeCode, Path.Combine(_root, "missing"));

        Assert.Equal(0, placement.Reachable);
        Assert.True(placement.StoreMissing);
        Assert.False(placement.IsComplete);
        Assert.Contains("the Gateway has not been reached", placement.Describe());
    }

    [Fact]
    public void An_agent_with_no_skills_directory_installs_nothing()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        var placement = SkillDirectoryInstaller.InstallFor(AgentKind.RawCli, Store);

        Assert.Equal(0, placement.Reachable);
        // Nothing was EXPECTED of it, so it is not a fault and must not warn - a raw terminal has
        // nowhere to put a skill, and warning about it every launch would train people to ignore the
        // warning that matters.
        Assert.True(placement.NothingExpected);
    }

    [Fact]
    public void A_skill_that_cannot_reach_the_agent_is_reported_not_swallowed()
    {
        // THE FAILURE THIS WHOLE RESULT TYPE EXISTS FOR. Measured on a real machine: every built-in
        // name was occupied in the Claude Code directory, so NOTHING was linked, and the only symptom
        // was an agent reading a two-month-old copy. Everything looked healthy. A placement that falls
        // short has to be able to say so.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "keeper"));

        // The owner's own skill of the same name, with more than one file so the one-time reclaim
        // leaves it strictly alone.
        var theirs = Path.Combine(LinkRoot, "keeper");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "# MINE\n");
        File.WriteAllText(Path.Combine(theirs, "notes.md"), "mine too\n");

        var placement = Install();

        Assert.Equal(1, placement.Held);
        Assert.Equal(0, placement.Reachable);
        Assert.True(placement.IsTotalFailure);
        Assert.False(placement.IsComplete);
        var problem = Assert.Single(placement.Problems);
        Assert.Equal("keeper", problem.SkillId);
        Assert.Equal(SkillPlacementFault.Shadowed, problem.Fault);
        // The sentence a human reads has to name the count, the skill and what to do about it.
        var said = placement.Describe();
        Assert.Contains("only 0 of 1", said);
        Assert.Contains("keeper", said);
        Assert.Contains("rename or remove it", said);
        // ...and the owner's own skill is still completely untouched.
        Assert.Equal("# MINE\n", File.ReadAllText(Path.Combine(theirs, "SKILL.md")));
    }

    [Fact]
    public void A_complete_placement_says_nothing_is_wrong()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        var placement = Install();

        Assert.True(placement.IsComplete);
        Assert.Empty(placement.Problems);
        Assert.Equal(placement.Held, placement.Reachable);
    }

    [Fact]
    public void The_retired_installers_leftover_is_moved_aside_ONCE_so_the_fleet_copy_can_land()
    {
        // The retired setup wizard wrote ~/.claude/skills/<name>/SKILL.md with no marker, so the
        // ownership rule cannot tell it from a hand-written skill and refuses to replace it - which
        // left the central library unable to reach Claude Code on every machine that ever ran it.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "move-session"));

        var leftover = Path.Combine(LinkRoot, "move-session");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "SKILL.md"), "# STALE INSTALLER COPY\n");

        var placement = SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot), Stamp, Personal);

        // The fleet copy is now reachable...
        Assert.True(placement.IsComplete);
        Assert.Contains("# Demo", File.ReadAllText(Path.Combine(LinkRoot, "move-session", "SKILL.md")));
        // ...and NOTHING was destroyed - the old copy is on disk OUTSIDE the skills root.
        var aside = Directory.GetDirectories(SkillDirectoryInstaller.SupersededRootFor(LinkRoot), "move-session-*");
        Assert.Single(aside);
        Assert.Equal("# STALE INSTALLER COPY\n", File.ReadAllText(Path.Combine(aside[0], "SKILL.md")));
        // THE FLAW THIS PINS, found by watching it happen on a real machine: moved aside WITHIN the
        // skills root, the old copy is still somewhere the agent scans and comes straight back as a
        // skill under a mangled name. Three of them appeared in a live session's skill list.
        Assert.Empty(Directory.GetDirectories(LinkRoot, "*superseded*"));
    }

    [Fact]
    public void The_reclaim_runs_once_and_never_takes_a_name_again()
    {
        // "One-time" has to mean it. After the migration has run for a directory, a skill the owner
        // puts there later wins the name for good - otherwise the reclaim is not a migration, it is a
        // standing licence to take names, and the promise that a machine's own skill wins is worthless.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "demo-skill"));
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot), Stamp, Personal);

        // The owner now writes their OWN skill of that name, in the same single-file shape.
        var link = Path.Combine(LinkRoot, "demo-skill");
        Directory.Delete(link, recursive: false);
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "SKILL.md"), "# WRITTEN BY THE OWNER\n");

        var placement = SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot), Stamp, Personal);

        Assert.Equal("# WRITTEN BY THE OWNER\n", File.ReadAllText(Path.Combine(link, "SKILL.md")));
        Assert.Single(placement.Problems);
        Assert.Equal(SkillPlacementFault.Shadowed, placement.Problems[0].Fault);
    }

    [Fact]
    public void The_reclaim_leaves_anything_that_is_not_the_retired_installers_shape()
    {
        // Narrow on purpose: no marker, exactly one file, named SKILL.md. A directory with anything
        // else in it is somebody's real work and is never moved, migration or not.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "demo-skill"));

        var mine = Path.Combine(LinkRoot, "demo-skill");
        Directory.CreateDirectory(Path.Combine(mine, "references"));
        File.WriteAllText(Path.Combine(mine, "SKILL.md"), "# MINE\n");
        File.WriteAllText(Path.Combine(mine, "references", "notes.md"), "mine\n");

        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot), Stamp, Personal);

        Assert.Equal("# MINE\n", File.ReadAllText(Path.Combine(mine, "SKILL.md")));
        Assert.False(Directory.Exists(SkillDirectoryInstaller.SupersededRootFor(LinkRoot)));
    }

    [Fact]
    public void A_SessionManager_does_not_touch_the_running_users_skills_unless_told_to()
    {
        // THE DEFAULT THAT STOPS A TEST RUN EDITING A REAL MACHINE. Placement writes into the home
        // directory of whoever is running the process. Several tests in this suite call CreateSession,
        // so while placement was unconditional a full test run reorganised a developer's own
        // ~/.claude/skills - links created, leftovers moved aside - with nobody asking it to. The app
        // opts in; a plain SessionManager must not.
        var manager = new CcDirector.Core.Sessions.SessionManager(
            new CcDirector.Core.Configuration.AgentOptions());

        Assert.False(manager.PlacesSkillsOnLaunch);
    }

    [Fact]
    public void Every_agent_kind_writes_to_the_one_shared_directory()
    {
        // Claude Code is the exception that makes this table necessary: it reads ~/.claude/skills and
        // does NOT read the shared ~/.agents/skills path that the others do. Getting this backwards
        // would install everything into a directory Claude Code never looks in - and nothing would
        // fail, it would just silently not work.
        foreach (var kind in new[]
                 {
                     AgentKind.ClaudeCode, AgentKind.Cursor,
                     AgentKind.Codex, AgentKind.Gemini, AgentKind.Grok,
                     AgentKind.Pi, AgentKind.Copilot, AgentKind.OpenCode,
                 })
        {
            var paths = SkillInstallTargets.For(kind);
            Assert.NotNull(paths);
            Assert.Contains(Path.Combine(".agents", "skills"), paths!.SharedRoot);
        }

        // Only the two that do not read the shared path get a directory of their own to link into.
        Assert.Contains(Path.Combine(".claude", "skills"),
            SkillInstallTargets.For(AgentKind.ClaudeCode)!.LinkRoot!);
        Assert.Contains(Path.Combine(".cursor", "skills"),
            SkillInstallTargets.For(AgentKind.Cursor)!.LinkRoot!);

        foreach (var kind in new[]
                 {
                     AgentKind.Codex, AgentKind.Gemini, AgentKind.Grok,
                     AgentKind.Pi, AgentKind.Copilot, AgentKind.OpenCode,
                 })
        {
            Assert.Null(SkillInstallTargets.For(kind)!.LinkRoot);
        }

        Assert.Null(SkillInstallTargets.For(AgentKind.RawCli));
    }

    /// <summary>Run the REAL installer against explicit directories. The paths are overridden rather
    /// than the algorithm reimplemented: a test that reimplements what it is testing proves only that
    /// the copy agrees with itself. The override exists so a test never writes into the home directory
    /// of whoever is running it.</summary>
    private SkillPlacement Install() =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot), Stamp, Personal);

    /// <summary>The library these tests install from, given explicitly so no test reads the Gateway
    /// configuration of whoever is running it.</summary>
    private static readonly SkillSource Personal = SkillSource.On("https://gateway.test", teamId: null);

    /// <summary>The one-time reclaim's record, per test, so one test's migration never silences
    /// another's - and so no test writes the real stamp into the developer's own storage.</summary>
    private string Stamp => Path.Combine(_root, "reclaimed.txt");
}
