using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// "Watch your factory", piece 5: the contract guard between the watch fold and the player. A fixed sample of
/// factories is folded through <see cref="FactoryWatchFold"/> for the owner and serialized exactly as the Gateway
/// answers (web JSON); the result must equal the committed fixture the player's own tests load
/// (packages/client-core/src/factory/watch/factoryWatchView.fixture.json). A field added to, renamed in or removed from
/// the watch DTOs fails here until the fixture - and with it the TypeScript types, whose tests read the same file - is
/// changed in the same pull request. So the C# and TypeScript shapes cannot drift apart silently.
///
/// To rewrite the fixture on purpose, run this test once with CC_WRITE_WATCH_FIXTURE=1, then update the TypeScript
/// types in factoryWatchTypes.ts until client-core's tests are green again.
/// </summary>
public sealed class FactoryWatchContractFixtureTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc);
    private static readonly FactoryWindow Day = new(FactoryAgentsFold.WindowLast24h, Now.AddHours(-24), Now);

    /// <summary>The Gateway's own answer settings: ASP.NET Core's web defaults (camelCase, numbers as numbers).</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, string? line = null) =>
        new() { Id = id, Name = name, Role = role, BriefFile = $"agents/{id}.yaml", Computer = "SOREN_NORTH", Line = line };

    private static int _nextId;

    /// <summary>The actor of a trigger's empty check, so consecutive ones fold into one run of checks.</summary>
    private const string MailCheck = "trigger:mail-check";

    private static FactoryActivityDto Row(string factory, string agent, string outcome, string what, DateTime at, string actor = "session:fixture") => new()
    {
        Id = new Guid($"00000000-0000-0000-0000-{++_nextId:D12}"),
        Factory = factory, FactoryAgent = agent, What = what, Outcome = outcome, Actor = actor,
        OccurredUtc = at, RecordedUtc = at,
    };

    /// <summary>A Boss and five workers, so a hub, with one step of every kind and a run of empty checks.</summary>
    private static FactoryWatchViewDto Hub()
    {
        var factory = new RegisteredFactoryDto
        {
            Factory = "website", Title = "Website factory", Folder = @"D:\factories\website", Computer = "SOREN_NORTH",
            BossSeat = "boss",
            Seats =
            {
                Seat("boss", "Boss", "Plans and hands out"),
                Seat("website-desk", "Website Desk", "Builds the site"),
                Seat("mail-desk", "Mail Desk", "Reads the mail"),
                Seat("photo-desk", "Photo Desk", "Prepares the photos"),
                Seat("checker", "Checker", "Checks the site"),
                Seat("writer", "Writer", "Writes the replies"),
            },
        };
        var t = Now.AddHours(-10);
        var rows = new List<FactoryActivityDto>
        {
            Row("website", "boss", FactoryActivityOutcome.Started, "Boss started the Website Desk: new photos arrived.", t),
            Row("website", "website-desk", FactoryActivityOutcome.Started, "The Website Desk started building version 21.", t.AddMinutes(2)),
            Row("website", "mail-desk", FactoryActivityOutcome.NothingToDo, "Checked the mail - nothing to do", t.AddMinutes(5), MailCheck),
            Row("website", "mail-desk", FactoryActivityOutcome.NothingToDo, "Checked the mail - nothing to do", t.AddMinutes(10), MailCheck),
            Row("website", "mail-desk", FactoryActivityOutcome.NothingToDo, "Checked the mail - nothing to do", t.AddMinutes(15), MailCheck),
            Row("website", "photo-desk", FactoryActivityOutcome.Talked, "The Photo Desk asked the Boss which photo goes first.", t.AddMinutes(20)),
            Row("website", "website-desk", FactoryActivityOutcome.Done, "The Website Desk finished: version 21 of the site is live.", t.AddMinutes(30)),
            Row("website", "checker", FactoryActivityOutcome.Failed, "The site check could not open the contact page.", t.AddMinutes(35)),
            Row("website", "writer", FactoryActivityOutcome.Done, "The Writer drafted the reply about version 21.", t.AddMinutes(40)),
            Row("website", "mail-desk", FactoryActivityOutcome.NothingToDo, "Checked the mail - nothing to do", t.AddMinutes(45), MailCheck),
        };
        return FactoryWatchFold.View(factory, rows, false, Day, TimeZoneInfo.Utc, FactoryWatchAudience.Owner);
    }

    /// <summary>A Boss and two production lines, so line halls; the window held more rows than one read returns.</summary>
    private static FactoryWatchViewDto Lines()
    {
        var factory = new RegisteredFactoryDto
        {
            Factory = "books", Title = "Book factory", Folder = @"D:\factories\books", Computer = "SOREN_NORTH",
            BossSeat = "boss",
            Seats =
            {
                Seat("boss", "Boss", "Plans and hands out"),
                Seat("drafter", "Drafter", "Drafts chapters", "Writing"),
                Seat("editor", "Editor", "Edits chapters", "Writing"),
                Seat("cover", "Cover Desk", "Draws covers", "Design"),
                Seat("layout", "Layout Desk", "Lays out pages", "Design"),
            },
        };
        var t = Now.AddHours(-6);
        var rows = new List<FactoryActivityDto>
        {
            Row("books", "drafter", FactoryActivityOutcome.Started, "The Drafter started chapter 3.", t),
            Row("books", "cover", FactoryActivityOutcome.Done, "The Cover Desk finished the front cover.", t.AddMinutes(12)),
            Row("books", "editor", FactoryActivityOutcome.SentBack, "The Editor sent chapter 2 back for a rewrite.", t.AddMinutes(30)),
        };
        return FactoryWatchFold.View(factory, rows, true, Day, TimeZoneInfo.Utc, FactoryWatchAudience.Owner);
    }

    /// <summary>Two seats and nothing in the window: a workshop with no steps, so the empty sentence.</summary>
    private static FactoryWatchViewDto Empty()
    {
        var factory = new RegisteredFactoryDto
        {
            Factory = "quiet", Title = "Quiet factory", Folder = @"D:\factories\quiet", Computer = "SOREN_NORTH",
            BossSeat = "boss",
            Seats = { Seat("boss", "Boss", "Plans and hands out"), Seat("helper", "Helper", "Helps") },
        };
        return FactoryWatchFold.View(factory, Array.Empty<FactoryActivityDto>(), false, Day, TimeZoneInfo.Utc, FactoryWatchAudience.Owner);
    }

    private static string Serialized()
    {
        _nextId = 0;
        var sample = new Dictionary<string, FactoryWatchViewDto>
        {
            ["hub"] = Hub(),
            ["lines"] = Lines(),
            ["empty"] = Empty(),
        };
        return JsonSerializer.Serialize(sample, Web).Replace("\r\n", "\n") + "\n";
    }

    private static string FixturePath([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Factory/<this>.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        var folder = Path.Combine(root, "packages", "client-core", "src", "factory", "watch");
        Assert.True(Directory.Exists(folder), $"Resolved the repository root to {root}, but it has no {folder}. Run the suite from a checkout.");
        return Path.Combine(folder, "factoryWatchView.fixture.json");
    }

    [Fact]
    public void WatchView_SerializedForThePlayer_EqualsTheCommittedFixture()
    {
        // Arrange
        var path = FixturePath();
        var actual = Serialized();
        if (Environment.GetEnvironmentVariable("CC_WRITE_WATCH_FIXTURE") == "1")
            File.WriteAllText(path, actual);

        // Act
        Assert.True(File.Exists(path), $"The player's fixture {path} is missing. Run this test once with CC_WRITE_WATCH_FIXTURE=1 to write it.");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n");

        // Assert
        Assert.True(committed == actual,
            "The watch view the Gateway serializes no longer matches the player's fixture " + path + ". " +
            "A watch DTO or the fold changed; run this test with CC_WRITE_WATCH_FIXTURE=1, then make factoryWatchTypes.ts " +
            "match and run client-core's tests. The Gateway now writes:\n" + actual);
    }

    [Fact]
    public void WatchFixture_CoversEveryStepKindAndEveryLayout()
    {
        // Arrange
        _nextId = 0;
        var views = new[] { Hub(), Lines(), Empty() };

        // Act
        var kinds = views.SelectMany(v => v.Steps).Select(s => s.Kind).Distinct().OrderBy(k => k).ToArray();
        var layouts = views.Select(v => v.Layout).Distinct().OrderBy(l => l).ToArray();

        // Assert: the player's tests draw every kind and layout from this one file, so it must hold them all.
        Assert.Equal(FactoryWatchStepKind.All.OrderBy(k => k).ToArray(), kinds);
        Assert.Equal(new[] { FactoryWatchLayout.Hub, FactoryWatchLayout.Lines, FactoryWatchLayout.Workshop }.OrderBy(l => l).ToArray(), layouts);
        Assert.NotNull(views[1].TruncatedText);
        Assert.NotNull(views[2].EmptyText);
    }
}
