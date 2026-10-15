using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE EXPORT OF A FACTORY THAT HAS NO DEFINITION (Factory Definition, phase 3; decision A8): the folder written from
/// the registry entry, the published briefs, the schedules and the triggers. What it can express is written exactly,
/// what it cannot is reported with the file it would have gone in - never dropped - and the same parts always give the
/// same bytes.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionExportTests
{
    private static RegisteredFactoryDto Registry(params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = "devthrottle",
        Title = "DevThrottle",
        Folder = @"D:\ReposFred\devthrottle-factory",
        Computer = "SOREN_NORTH",
        BossSeat = "boss",
        GoalText = "Ship the product.\n",
        GoalApprovedOn = "2026-10-10",
        Purpose = "Builds DevThrottle",
        Seats = seats.ToList(),
    };

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, string? computer = null, string? line = null,
        params string[] schedules) => new()
    {
        Id = id,
        Name = name,
        Role = role,
        BriefFile = $"briefs/{id}.md",
        Computer = computer ?? "SOREN_NORTH",
        Line = line,
        Schedules = schedules.ToList(),
    };

    private static CronJobDto Schedule(string id, string seat, string cron = "0 8 * * 1-5", string seed = "Morning round.",
        bool enabled = true, string kind = "recurring") => new()
    {
        Id = id,
        Name = $"{seat} schedule",
        Enabled = enabled,
        ScheduleKind = kind,
        CronExpression = cron,
        TimeZoneId = "Europe/Copenhagen",
        Factory = "devthrottle",
        Seat = seat,
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { Seed = seed },
    };

    private static FactoryLiveParts Clean() => new(
        Registry(Seat("boss", "Boss", "Boss", schedules: "cj_boss"), Seat("writer", "Writer", "Writer", line: "Posts")),
        new Dictionary<string, string> { ["boss"] = "Run the factory.\nRead what the writer made.\n", ["writer"] = "Write one post." },
        new[] { Schedule("cj_boss", "boss") },
        Array.Empty<LiveTrigger>());

    [Fact]
    public void A_FACTORY_THAT_IS_ALL_EXPRESSIBLE_IS_WRITTEN_AS_A_FOLDER_THE_CHECK_ACCEPTS()
    {
        var result = FactoryDefinitionExport.FromLive(Clean());

        Assert.Equal(new[] { "factory.yaml", "seats/boss.yaml", "seats/writer.yaml" }, result.Files.Keys);
        Assert.Equal(
            "factory: devthrottle\n" +
            "title: DevThrottle\n" +
            "purpose: Builds DevThrottle\n" +
            "goalText: |\n" +
            "  Ship the product.\n" +
            "goalApprovedOn: 2026-10-10\n" +
            "bossSeat: boss\n" +
            "defaults:\n" +
            "  machine: SOREN_NORTH\n" +
            "  agent: ClaudeCode\n",
            result.Files["factory.yaml"]);
        Assert.Equal(
            "seat: boss\n" +
            "name: Boss\n" +
            "role: Boss\n" +
            "brief: |\n" +
            "  Run the factory.\n" +
            "  Read what the writer made.\n" +
            "starts:\n" +
            "  - cron: \"0 8 * * 1-5\"\n" +
            "    timeZone: Europe/Copenhagen\n" +
            "    prompt: Morning round.\n",
            result.Files["seats/boss.yaml"]);
        Assert.Equal("seat: writer\nname: Writer\nrole: Writer\nline: Posts\nbrief: Write one post.\n", result.Files["seats/writer.yaml"]);

        var check = FactoryDefinitionCheck.Check(result.Files, "devthrottle", Array.Empty<string>(), new[] { "SOREN_NORTH" });
        Assert.DoesNotContain(check.Findings, f => f.IsError);
    }

    [Fact]
    public void WHAT_A_DEFINITION_CANNOT_HOLD_IS_REPORTED_WITH_ITS_FILE_NOT_DROPPED()
    {
        var registry = Registry(
            Seat("boss", "Boss", "Boss", schedules: new[] { "cj_boss", "cj_gone" }),
            Seat("outreach", "Outreach", "Sales", computer: "SOREN_SOUTH"));
        registry.GoalFile = "GOAL.md";
        var off = Schedule("cj_off", "boss", enabled: false);
        var random = Schedule("cj_random", "boss", cron: "window=07:00-01:00 perDay=4", kind: "random");
        var workList = Schedule("cj_list", "boss");
        workList.Action.WorkListName = "nightly";
        var elsewhere = Schedule("cj_boss", "boss");
        elsewhere.Target.Machine = "SOREN_SOUTH";
        elsewhere.Action.RepoPath = @"D:\ReposFred\devthrottle-factory";
        elsewhere.NotifyOn = CronNotify.Failure;
        elsewhere.Action.AutoDismiss = false;
        elsewhere.PreventOverlap = false;
        var noSeat = Schedule("cj_orphan", "boss");
        noSeat.Seat = "ghost";
        var trigger = new LiveTrigger("new-mail", "outreach", "SOREN_SOUTH", @"D:\work", "python check.py", 600, "Answer the mail.", true);
        var live = new FactoryLiveParts(registry, new Dictionary<string, string> { ["boss"] = "Run it." },
            new[] { off, random, workList, elsewhere, noSeat }, new[] { trigger });

        var result = FactoryDefinitionExport.FromLive(live);

        string Reported(string file, string words) =>
            Assert.Single(result.Reported, r => r.File == file && r.Message.Contains(words)).Message;
        Reported("factory.yaml", "The goal is the file 'GOAL.md'");
        Reported("factory.yaml", @"The factory's folder 'D:\ReposFred\devthrottle-factory'");
        Reported("factory.yaml", "No agent or model is recorded");
        Reported("seats/boss.yaml", "'boss schedule' (cj_off) is not a start here: it is switched off");
        Reported("seats/boss.yaml", "(cj_random) is not a start here: it is a random schedule");
        Reported("seats/boss.yaml", "(cj_list) is not a start here: it drains the work list 'nightly'");
        Reported("seats/boss.yaml", "(cj_boss) is a start here, but it runs on SOREN_SOUTH and the seat on SOREN_NORTH");
        Reported("seats/boss.yaml", @"(cj_boss) is a start here, but its working folder 'D:\ReposFred\devthrottle-factory' is not kept");
        Reported("seats/boss.yaml", "(cj_boss) is a start here, but its notification ('failure') is not kept");
        Reported("seats/boss.yaml", "(cj_boss) is a start here, but it keeps its session open");
        Reported("seats/boss.yaml", "The registry lists the schedule 'cj_gone' for this seat, but no schedule of this factory has that id");
        Reported("seats/outreach.yaml", "The seat has no published brief on the Gateway; its brief is the file 'briefs/outreach.md'");
        Assert.Contains("its check is the command 'python check.py'", Reported("seats/outreach.yaml", "The trigger 'new-mail' (paused)"));
        Reported("seats/", "names the seat 'ghost', which the factory does not have");

        // The schedule that IS a start keeps what a start holds, preventOverlap included.
        Assert.Contains("  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n    prompt: Morning round.\n    preventOverlap: false\n",
            result.Files["seats/boss.yaml"]);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Files["seats/boss.yaml"], "- cron:"));
        // A seat on another machine says so; an empty brief is left for the import's check to name.
        Assert.Contains("machine: SOREN_SOUTH\nbrief: \"\"\n", result.Files["seats/outreach.yaml"]);
        // Reports come in file order.
        Assert.Equal(result.Reported.Select(r => r.File).OrderBy(f => f, StringComparer.Ordinal), result.Reported.Select(r => r.File));
    }

    [Fact]
    public void A_SEAT_WITH_NO_PUBLISHED_BRIEF_IS_REFUSED_BY_THE_CHECK_ON_ITS_FILE_AND_LINE()
    {
        var clean = Clean();
        var live = clean with { PublishedBriefs = new Dictionary<string, string> { ["boss"] = "Run it.\n" } };

        var files = FactoryDefinitionExport.FromLive(live).Files;

        var check = FactoryDefinitionCheck.Check(files, "devthrottle", Array.Empty<string>(), new[] { "SOREN_NORTH" });
        var error = Assert.Single(check.Findings, f => f.IsError);
        Assert.Equal("seats/writer.yaml", error.File);
        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void THE_SAME_PARTS_IN_ANY_ORDER_GIVE_THE_SAME_BYTES()
    {
        var one = Clean();
        var registry = Registry(Seat("writer", "Writer", "Writer", line: "Posts"), Seat("boss", "Boss", "Boss", schedules: "cj_boss"));
        var two = one with
        {
            Registry = registry,
            Schedules = new[] { Schedule("cj_z", "boss", seed: "Late."), Schedule("cj_boss", "boss") },
        };
        one = one with { Schedules = new[] { Schedule("cj_boss", "boss"), Schedule("cj_z", "boss", seed: "Late.") } };

        var a = FactoryDefinitionExport.FromLive(one);
        var b = FactoryDefinitionExport.FromLive(two);

        Assert.Equal(a.Files, b.Files);
        Assert.Equal(a.Reported, b.Reported);
        Assert.True(a.Files["seats/boss.yaml"].IndexOf("Morning round.", StringComparison.Ordinal)
                    < a.Files["seats/boss.yaml"].IndexOf("Late.", StringComparison.Ordinal));
    }
}
