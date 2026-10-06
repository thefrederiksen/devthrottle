using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// Two Directors on one computer share its skill folders (devthrottle_internal#2311, live proof F7).
///
/// The folders are per USER: every Director writes into the same <c>~/.agents/skills</c> and
/// <c>~/.claude/skills</c>. Observed live: two test Directors on a Gateway serving a different skill set
/// removed six of the person's skills, and the person's own Directors put them back half an hour later -
/// because the marker said only "DevThrottle installed this", and each Director removed every marked folder
/// its own library did not hold.
///
/// These run the REAL installer twice over ONE pair of folders, once per library, the way two Directors do.
/// </summary>
public sealed class SkillSourceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "skill-source-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>The person's own personal account, on the hosted Gateway.</summary>
    private static readonly SkillSource Personal = SkillSource.On("https://gateway.test", teamId: null);

    /// <summary>A team, on the same Gateway.</summary>
    private static readonly SkillSource TeamA = SkillSource.On("https://gateway.test", "team-a");

    /// <summary>A second team.</summary>
    private static readonly SkillSource TeamB = SkillSource.On("https://gateway.test", "team-b");

    /// <summary>Stands in for <c>~/.agents/skills</c>, shared by every Director on the computer.</summary>
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");

    /// <summary>Stands in for <c>~/.claude/skills</c>, also shared.</summary>
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");

    /// <summary>Each Director has its OWN store, in its own storage home.</summary>
    private string StoreOf(SkillSource source) =>
        Path.Combine(_root, "director-" + new string((source.GatewayUrl + "-" + (source.TeamId ?? "personal"))
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()), "skills", "installed");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Two_sources_never_remove_each_others_skills_in_any_order_and_repeatedly()
    {
        // THE F7 CASE. A personal Director and a team Director, each with its own library, one name in both.
        Holds(Personal, "mine-one", "mine-two", "both");
        Holds(TeamA, "team-one", "team-two", "both");

        // Every order, more than once: a rule that only holds on the first launch breaks on the second
        // session of the day, which is the launch nobody tests.
        var orders = new[]
        {
            new[] { Personal, TeamA, Personal, TeamA },
            new[] { TeamA, Personal, TeamA, Personal },
            new[] { TeamA, TeamA, Personal, Personal, TeamA },
        };
        var namesOf = new Dictionary<SkillSource, string[]>
        {
            [Personal] = new[] { "mine-one", "mine-two", "both" },
            [TeamA] = new[] { "team-one", "team-two", "both" },
        };
        foreach (var order in orders)
        {
            var installedSoFar = new HashSet<string>();
            foreach (var source in order)
            {
                Install(source);
                installedSoFar.UnionWith(namesOf[source]);

                // Everything either library has installed so far is still there, after every launch.
                foreach (var name in installedSoFar)
                {
                    Assert.True(File.Exists(Path.Combine(Shared, name, "SKILL.md")),
                        $"'{name}' is missing from the shared folder after {source.Describe()} installed");
                    Assert.True(File.Exists(Path.Combine(LinkRoot, name, "SKILL.md")),
                        $"'{name}' is missing from the agent's folder after {source.Describe()} installed");
                }
                // The person's own library holds the name both serve, once it has installed at all.
                if (order.TakeWhile(s => s != source).Contains(Personal) || source == Personal)
                    Assert.Contains("from the personal account", File.ReadAllText(Path.Combine(LinkRoot, "both", "SKILL.md")));
            }
            Reset();
            Holds(Personal, "mine-one", "mine-two", "both");
            Holds(TeamA, "team-one", "team-two", "both");
        }
    }

    [Fact]
    public void The_personal_account_wins_a_name_even_when_the_team_installed_it_first()
    {
        Holds(Personal, "both");
        Holds(TeamA, "both");

        Install(TeamA);
        Assert.Contains("from team team-a", Body("both"));

        Install(Personal);
        Assert.Contains("from the personal account", Body("both"));

        // The team comes back, and yields - recorded, the way an owner's own skill is.
        var teamAgain = Install(TeamA);
        Assert.Contains("from the personal account", Body("both"));
        var problem = Assert.Single(teamAgain.Problems);
        Assert.Equal("both", problem.SkillId);
        Assert.Equal(SkillPlacementFault.HeldByAnotherSource, problem.Fault);
        Assert.Contains("kept by another Director's library", teamAgain.Describe());
    }

    [Fact]
    public void A_withdrawal_removes_only_the_withdrawing_sources_own_skills()
    {
        Holds(Personal, "mine-one", "mine-two", "both");
        Holds(TeamA, "team-one", "team-two", "both");
        Install(Personal);
        Install(TeamA);

        // The team withdraws one of its own and the name it shares with the person.
        Holds(TeamA, "team-two");
        Install(TeamA);

        Assert.False(Directory.Exists(Path.Combine(Shared, "team-one")));
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "team-one")));
        foreach (var name in new[] { "mine-one", "mine-two", "both", "team-two" })
            Assert.True(File.Exists(Path.Combine(LinkRoot, name, "SKILL.md")), $"'{name}' was removed");

        // The person withdraws one of theirs; the team's remaining skill stays.
        Holds(Personal, "mine-two", "both");
        Install(Personal);

        Assert.False(Directory.Exists(Path.Combine(Shared, "mine-one")));
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "mine-one")));
        foreach (var name in new[] { "mine-two", "both", "team-two" })
            Assert.True(File.Exists(Path.Combine(LinkRoot, name, "SKILL.md")), $"'{name}' was removed");
    }

    [Fact]
    public void Between_two_teams_the_first_installed_keeps_the_name_until_it_withdraws_it()
    {
        Holds(TeamA, "both");
        Holds(TeamB, "both");

        Install(TeamA);
        var second = Install(TeamB);

        Assert.Contains("from team team-a", Body("both"));
        Assert.Equal(SkillPlacementFault.HeldByAnotherSource, Assert.Single(second.Problems).Fault);

        // Team B withdrawing a name it never got does not take it from team A.
        Holds(TeamB);
        Install(TeamB);
        Assert.Contains("from team team-a", Body("both"));

        // Team A withdraws it; team B, still serving it, now gets it.
        Holds(TeamB, "both");
        Holds(TeamA);
        Install(TeamA);
        Assert.False(Directory.Exists(Path.Combine(Shared, "both")));
        Install(TeamB);
        Assert.Contains("from team team-b", Body("both"));
    }

    [Fact]
    public void A_marker_written_before_sources_were_recorded_belongs_to_the_personal_account()
    {
        // THE UPGRADE. Every skill on a machine today carries the old three-line marker. A team Director
        // running the new code must not read that as "not anybody's" and delete the person's skills.
        var legacy = OldFormatSkill("dev-throttle");

        // A team that does not serve it: kept.
        Holds(TeamA, "team-one");
        Install(TeamA);
        Assert.Equal("# OLD COPY\n", File.ReadAllText(Path.Combine(legacy, "SKILL.md")));

        // A team that DOES serve it: yields, and says so.
        Holds(TeamA, "team-one", "dev-throttle");
        var placement = Install(TeamA);
        Assert.Equal("# OLD COPY\n", File.ReadAllText(Path.Combine(legacy, "SKILL.md")));
        Assert.Contains(placement.Problems, p => p.SkillId == "dev-throttle" && p.Fault == SkillPlacementFault.HeldByAnotherSource);

        // The personal account that does not serve it: kept - which personal library wrote it is not
        // recorded, so a withdrawal would be a guess.
        Holds(Personal, "mine-one");
        Install(Personal);
        Assert.Equal("# OLD COPY\n", File.ReadAllText(Path.Combine(legacy, "SKILL.md")));

        // The personal account that serves it: takes it over and stamps it as its own.
        Holds(Personal, "mine-one", "dev-throttle");
        Install(Personal);
        Assert.Contains("from the personal account", Body("dev-throttle"));
        Assert.Contains("account=personal", File.ReadAllText(Path.Combine(legacy, SkillDirectoryInstaller.MarkerFileName)));
    }

    [Fact]
    public void A_folder_with_no_marker_is_never_touched_by_any_source()
    {
        // Today's behaviour, still pinned for both libraries: a skill DevThrottle did not write wins.
        foreach (var root in new[] { Shared, LinkRoot })
        {
            var hand = Path.Combine(root, "hand-made");
            Directory.CreateDirectory(Path.Combine(hand, "references"));
            File.WriteAllText(Path.Combine(hand, "SKILL.md"), "# BY HAND\n");
            File.WriteAllText(Path.Combine(hand, "references", "notes.md"), "mine\n");
        }

        foreach (var source in new[] { Personal, TeamA, Personal, TeamA })
        {
            Holds(source);
            Install(source);
            Holds(source, "hand-made");
            var placement = Install(source);
            Assert.Contains(placement.Problems, p => p.SkillId == "hand-made" && p.Fault == SkillPlacementFault.Shadowed);
        }

        foreach (var root in new[] { Shared, LinkRoot })
        {
            var hand = Path.Combine(root, "hand-made");
            Assert.Equal("# BY HAND\n", File.ReadAllText(Path.Combine(hand, "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(hand, SkillDirectoryInstaller.MarkerFileName)));
            Assert.Equal(0, (int)(File.GetAttributes(hand) & FileAttributes.ReparsePoint));
        }
    }

    [Fact]
    public void A_source_recognises_its_own_stamp_under_any_address_it_knows_its_Gateway_by()
    {
        // A self-hosted Gateway is reachable by machine name, Tailscale and local network address (#1233),
        // and the active one can change. A Director that stopped recognising its own skills after that would
        // never refresh or withdraw them again.
        var byName = new SkillSource("http://gateway-box:7878", null, new[] { "http://gateway-box:7878", "http://100.64.0.5:7878" });
        var byTailscale = new SkillSource("http://100.64.0.5:7878", null, new[] { "http://100.64.0.5:7878", "http://gateway-box:7878" });
        Holds(byName, "mine-one", "mine-two");
        Install(byName);

        Holds(byTailscale, "mine-two");
        var placement = Install(byTailscale);

        Assert.Empty(placement.Problems);
        Assert.False(Directory.Exists(Path.Combine(Shared, "mine-one")));
        Assert.True(File.Exists(Path.Combine(LinkRoot, "mine-two", "SKILL.md")));
    }

    [Fact]
    public void The_personal_account_on_another_Gateway_is_another_source()
    {
        // The F7 rig exactly: the person's own Directors on the hosted Gateway, and a test Director on its
        // own Gateway - both "personal". Neither may remove the other's skills.
        var hosted = SkillSource.On("https://hosted.test", null);
        var testGateway = SkillSource.On("http://localhost:7999", null);
        Holds(hosted, "mine-one", "both");
        Holds(testGateway, "rig-one", "both");

        Install(hosted);
        Install(testGateway);
        Install(hosted);

        foreach (var name in new[] { "mine-one", "rig-one", "both" })
            Assert.True(File.Exists(Path.Combine(LinkRoot, name, "SKILL.md")), $"'{name}' was removed");
        Assert.Contains("on https://hosted.test", Body("both"));
    }

    [Fact]
    public void A_stamp_with_no_source_lines_reads_as_unrecorded_and_a_team_stamp_reads_back()
    {
        Assert.Null(SkillSource.ReadStamp(new[] { "demo", "3", "hash" }));
        Assert.Equal(new SkillSourceStamp("https://gateway.test", null),
            SkillSource.ReadStamp(new[] { "demo", "3", "hash", "gateway=https://Gateway.test/", "account=personal" }));
        Assert.Equal(new SkillSourceStamp("https://gateway.test", "team-a"),
            SkillSource.ReadStamp(new[] { "demo", "3", "hash", "gateway=https://gateway.test", "account=team:team-a" }));
    }

    /// <summary>Make <paramref name="source"/>'s Director store hold exactly <paramref name="names"/>, the
    /// way <see cref="SkillStoreRefresh"/> leaves it. Each body names its source, so a test can see whose
    /// copy won.</summary>
    private void Holds(SkillSource source, params string[] names)
    {
        var store = StoreOf(source);
        if (Directory.Exists(store))
            Directory.Delete(store, recursive: true);
        Directory.CreateDirectory(store);
        foreach (var name in names)
        {
            SkillDirectoryInstaller.Materialize(store, new SkillBundle(
                name, 1, "hash-1", "A skill.", new[] { name }, $"# {name}\n\nfrom {source.Describe()}\n",
                Array.Empty<SkillFileBytes>()));
        }
    }

    private SkillPlacement Install(SkillSource source) =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, StoreOf(source), new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, "reclaimed.txt"), source);

    /// <summary>The skill as Claude Code reads it: through its link.</summary>
    private string Body(string name) => File.ReadAllText(Path.Combine(LinkRoot, name, "SKILL.md"));

    /// <summary>A skill installed by the code before sources were recorded: three-line marker, a link.</summary>
    private string OldFormatSkill(string name)
    {
        var copy = Path.Combine(Shared, name);
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "SKILL.md"), "# OLD COPY\n");
        File.WriteAllText(Path.Combine(copy, SkillDirectoryInstaller.MarkerFileName), $"{name}\n2\nold-hash\n");
        return copy;
    }

    /// <summary>Empty both shared folders, links first, so the next order starts from nothing.</summary>
    private void Reset()
    {
        foreach (var root in new[] { LinkRoot, Shared })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var entry in Directory.GetDirectories(root))
            {
                var isLink = (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0;
                Directory.Delete(entry, recursive: !isLink);
            }
        }
    }
}
