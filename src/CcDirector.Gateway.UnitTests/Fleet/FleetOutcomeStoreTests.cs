using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The Fleet Manager's stored news (the Fleet Manager mission, step 3), against a real database file.
///
/// What matters most here: a record stays open until it is answered, across a restart; answering it takes it
/// off the open list and cannot be done twice; another account can neither see nor answer it; and a record
/// the Cockpit could not draw is refused with a message that names what is wrong.
/// </summary>
public sealed class FleetOutcomeStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("acct-fleet-a");
    private static readonly TenantId TenantB = new("acct-fleet-b");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private const string FleetManagerId = "7a0e4c55-1111-4222-8333-944455556666";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private FleetOutcomeStore NewStore() => new(_harness.Open());

    internal static FleetOutcomeFileRequest Ready(string title = "Roster sort fix is ready") => new()
    {
        Kind = "ready",
        Title = title,
        Ready = new FleetReadyDetails
        {
            PullRequest = "https://github.com/example/product/pull/12",
            Risk = "low",
            Checks = "passed",
            Tested = "Unit tests, and the roster checked by hand.",
            ReviewedBy = "A second, independent session",
            Change = "The roster keeps its order when a session is renamed.",
        },
    };

    internal static FleetOutcomeFileRequest Finding(string title = "Why the nightly run was slow") => new()
    {
        Kind = "finding",
        Title = title,
        Finding = new FleetFindingDetails
        {
            Answer = "The database index was missing.",
            Reason = "The plan shows a full scan.",
            Links = new List<string> { "https://example.com/report/1" },
        },
    };

    internal static FleetOutcomeFileRequest Decision(string title = "Which release channel") => new()
    {
        Kind = "decision",
        Title = title,
        Decision = new FleetDecisionDetails
        {
            Question = "Ship to the beta channel or the stable channel?",
            Options = new List<string> { "Beta", "Stable" },
            Recommended = "Beta",
            Why = "It has not been tried on a second computer yet.",
        },
    };

    [Fact]
    public void File_Ready_ReturnsTheRecordTypedAndOpen()
    {
        var store = NewStore();

        var filed = store.File(TenantA, Ready(), FleetManagerId, Now);

        Assert.True(Guid.TryParse(filed.Id, out _));
        Assert.Equal("ready", filed.Kind);
        Assert.Equal("open", filed.Status);
        Assert.Equal(FleetManagerId, filed.FiledBy);
        Assert.Equal(Now, filed.CreatedAtUtc);
        Assert.NotNull(filed.Ready);
        Assert.Equal("low", filed.Ready!.Risk);
        Assert.Equal("passed", filed.Ready.Checks);
        Assert.Equal("https://github.com/example/product/pull/12", filed.Ready.PullRequest);
        Assert.Null(filed.Finding);
        Assert.Null(filed.Decision);
        Assert.Null(filed.AnsweredAtUtc);
    }

    [Fact]
    public void File_OpenRecord_SurvivesAStoreReopen()
    {
        var filed = NewStore().File(TenantA, Decision(), FleetManagerId, Now);

        // A second store over the same file is what a Gateway restart is.
        var reopened = NewStore();
        var open = reopened.List(TenantA, "open", kind: null, count: 50);

        var row = Assert.Single(open);
        Assert.Equal(filed.Id, row.Id);
        Assert.Equal("open", row.Status);
        Assert.Equal(new[] { "Beta", "Stable" }, row.Decision!.Options);
        Assert.Equal("Beta", row.Decision.Recommended);
    }

    [Fact]
    public void Answer_OpenRecord_LeavesTheOpenListAndIsFinal()
    {
        var store = NewStore();
        var filed = store.File(TenantA, Ready(), FleetManagerId, Now);

        var first = store.Answer(TenantA, Guid.Parse(filed.Id), "Merge it.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now.AddMinutes(5));
        var second = store.Answer(TenantA, Guid.Parse(filed.Id), "Actually, wait.", FleetManagerId, FleetOutcomeStore.RoleFleetManager, Now.AddMinutes(6));

        Assert.Equal(FleetOutcomeAnswerStatus.Answered, first.Status);
        Assert.Equal("answered", first.Outcome!.Status);
        Assert.Equal("Merge it.", first.Outcome.Answer);
        Assert.Equal("owner", first.Outcome.AnsweredBy);
        Assert.Equal("owner", first.Outcome.AnsweredByRole);
        Assert.Equal(Now.AddMinutes(5), first.Outcome.AnsweredAtUtc);
        Assert.Null(first.Outcome.AnswerMatchedOption);

        // The second answer is refused and changes nothing.
        Assert.Equal(FleetOutcomeAnswerStatus.AlreadyAnswered, second.Status);
        var stored = NewStore().Get(TenantA, Guid.Parse(filed.Id))!;
        Assert.Equal("Merge it.", stored.Answer);
        Assert.Equal("owner", stored.AnsweredBy);

        Assert.Empty(store.List(TenantA, "open", null, 50));
        Assert.Single(store.List(TenantA, "answered", null, 50));
        Assert.Single(store.List(TenantA, "all", null, 50));
    }

    [Fact]
    public void Answer_Decision_RecordsWhetherTheAnswerWasAnOption()
    {
        var store = NewStore();
        var matching = store.File(TenantA, Decision("first"), FleetManagerId, Now);
        var other = store.File(TenantA, Decision("second"), FleetManagerId, Now);

        var matched = store.Answer(TenantA, Guid.Parse(matching.Id), "  stable ", FleetManagerId, FleetOutcomeStore.RoleFleetManager, Now);
        var unmatched = store.Answer(TenantA, Guid.Parse(other.Id), "Neither - hold it a week.", FleetManagerId, FleetOutcomeStore.RoleFleetManager, Now);

        Assert.True(matched.Outcome!.AnswerMatchedOption);
        // The words are kept exactly as given, spaces and all.
        Assert.Equal("  stable ", matched.Outcome.Answer);
        Assert.Equal(FleetManagerId, matched.Outcome.AnsweredBy);
        Assert.False(unmatched.Outcome!.AnswerMatchedOption);
    }

    [Fact]
    public void AnotherAccount_CannotReadListOrAnswerTheRecord()
    {
        var store = NewStore();
        var filed = store.File(TenantA, Finding(), FleetManagerId, Now);
        var id = Guid.Parse(filed.Id);

        Assert.Null(store.Get(TenantB, id));
        Assert.Empty(store.List(TenantB, "all", null, 50));
        var answer = store.Answer(TenantB, id, "Not yours.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now);
        Assert.Equal(FleetOutcomeAnswerStatus.NotFound, answer.Status);

        // And the owner's record is untouched by the attempt.
        Assert.Equal("open", store.Get(TenantA, id)!.Status);
    }

    [Fact]
    public void List_FiltersByKind_NewestFirst_AndHonoursCount()
    {
        var store = NewStore();
        store.File(TenantA, Ready("oldest"), FleetManagerId, Now);
        store.File(TenantA, Finding("middle"), FleetManagerId, Now.AddMinutes(1));
        store.File(TenantA, Ready("newest"), FleetManagerId, Now.AddMinutes(2));

        var ready = store.List(TenantA, "open", "ready", 50);
        var one = store.List(TenantA, "open", null, 1);

        Assert.Equal(new[] { "newest", "oldest" }, ready.Select(r => r.Title));
        Assert.Equal("newest", Assert.Single(one).Title);
    }

    /// <summary>
    /// EVERY RECORD IS REACHABLE: more than the largest page, many filed in the same instant (so only the id orders
    /// them), and following the cursor serves each exactly once and then says nothing remains.
    /// </summary>
    [Fact]
    public void ListPage_FollowingTheCursor_ServesEveryRecordOnce_BeyondTheLargestPage()
    {
        var store = NewStore();
        var filed = new List<string>();
        for (var i = 0; i < FleetOutcomeStore.MaxCount + 5; i++)
            filed.Add(store.File(TenantA, Ready($"Ready {i}"), FleetManagerId, Now.AddSeconds(i / 50)).Id);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = store.ListPage(TenantA, "all", null, 60, cursor);
            seen.AddRange(page.Outcomes.Select(o => o.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(4, pages);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(filed.OrderBy(x => x), seen.OrderBy(x => x));
        // Newest first across page boundaries.
        var times = seen.Select(id => store.Get(TenantA, Guid.Parse(id))!.CreatedAtUtc).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    /// <summary>
    /// AN ANSWER BETWEEN PAGES SKIPS AND REPEATS NOTHING. Records answered after the first page leave the open
    /// list; every record still open is served exactly once, and none is served twice.
    /// </summary>
    [Fact]
    public void ListPage_AnsweringBetweenPages_SkipsAndRepeatsNothing()
    {
        var store = NewStore();
        for (var i = 0; i < 10; i++) store.File(TenantA, Ready($"Ready {i}"), FleetManagerId, Now);

        var first = store.ListPage(TenantA, "open", null, 4, cursor: null);
        // Answer one record already served and one not yet reached.
        var notYetReached = store.List(TenantA, "open", null, 10)[7].Id;
        foreach (var id in new[] { first.Outcomes[0].Id, notYetReached })
            Assert.Equal(FleetOutcomeAnswerStatus.Answered,
                store.Answer(TenantA, Guid.Parse(id), "Done.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now).Status);

        var seen = first.Outcomes.Select(o => o.Id).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = store.ListPage(TenantA, "open", null, 4, cursor);
            seen.AddRange(page.Outcomes.Select(o => o.Id));
            cursor = page.NextCursor;
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        var stillOpen = store.List(TenantA, "open", null, 50).Select(o => o.Id).ToList();
        Assert.Equal(8, stillOpen.Count);
        Assert.All(stillOpen, id => Assert.Contains(id, seen));
        Assert.DoesNotContain(notYetReached, seen);

        // Across both statuses the answer moves nothing: all ten, once each.
        var all = new List<string>();
        var page1 = store.ListPage(TenantA, "all", null, 3, null);
        all.AddRange(page1.Outcomes.Select(o => o.Id));
        store.Answer(TenantA, Guid.Parse(stillOpen[^1]), "Also done.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now);
        for (var c = page1.NextCursor; c is not null;)
        {
            var page = store.ListPage(TenantA, "all", null, 3, c);
            all.AddRange(page.Outcomes.Select(o => o.Id));
            c = page.NextCursor;
        }
        Assert.Equal(10, all.Distinct().Count());
        Assert.Equal(10, all.Count);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("djE6MTIzOm5vdC1hLWd1aWQ")] // "v1:123:not-a-guid"
    [InlineData("djI6MTIzOjAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAw")] // another version
    public void ListPage_ACursorThisGatewayDidNotIssue_IsRefused(string cursor)
    {
        var ex = Assert.Throws<ArgumentException>(() => NewStore().ListPage(TenantA, "all", null, 5, cursor));
        Assert.Equal($"cursor '{cursor}' is not one this Gateway issued; list again without a cursor to start from the newest", ex.Message);
    }

    [Theory]
    [InlineData("open", "news", 5, "kind 'news' is not valid; use one of: ready, finding, decision")]
    [InlineData("closed", null, 5, "status 'closed' is not valid; use one of: open, answered, all")]
    [InlineData("open", null, 0, "count must be between 1 and 200")]
    public void List_BadFilter_IsRefusedWithTheValidValues(string status, string? kind, int count, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => NewStore().List(TenantA, status, kind, count));
        Assert.StartsWith(expected, ex.Message);
    }

    [Fact]
    public void File_BadKind_IsRefusedNamingTheValidKinds()
    {
        var request = Ready();
        request.Kind = "update";

        var ex = Assert.Throws<ArgumentException>(() => NewStore().File(TenantA, request, FleetManagerId, Now));

        Assert.Equal("kind 'update' is not valid; use one of: ready, finding, decision", ex.Message);
    }

    public static TheoryData<string, Action<FleetOutcomeFileRequest>, string> MissingFields => new()
    {
        { "ready", r => r.Title = " ", "title is required" },
        { "ready", r => r.Title = "two\nlines", "title must be one line" },
        { "ready", r => r.Ready = null, "a ready record needs a 'ready' block" },
        { "ready", r => r.Ready!.PullRequest = "", "ready.pullRequest is required" },
        { "ready", r => r.Ready!.PullRequest = "pull 12", "ready.pullRequest 'pull 12' is not a full link" },
        { "ready", r => r.Ready!.Risk = "tiny", "ready.risk 'tiny' is not valid; use one of: low, medium, high" },
        { "ready", r => r.Ready!.Checks = "", "ready.checks is required; use one of: passed, failed, none" },
        { "ready", r => r.Ready!.Tested = "", "ready.tested is required" },
        { "ready", r => r.Ready!.ReviewedBy = "", "ready.reviewedBy is required" },
        { "ready", r => r.Ready!.Change = "", "ready.change is required" },
        { "ready", r => r.Decision = new FleetDecisionDetails(), "a ready record carries only the 'ready' block" },
        { "finding", r => r.Finding!.Answer = "", "finding.answer is required" },
        { "finding", r => r.Finding!.Links = new List<string> { "not a link" }, "finding.links[0] 'not a link' is not a full link" },
        { "decision", r => r.Decision!.Question = "", "decision.question is required" },
        { "decision", r => r.Decision!.Options = new List<string> { "Only" }, "decision.options needs at least two options, got 1" },
        { "decision", r => r.Decision!.Options = new List<string> { "Beta", "beta" }, "decision.options lists 'Beta' more than once" },
        { "decision", r => r.Decision!.Recommended = "Nightly", "decision.recommended 'Nightly' is not one of the options" },
        { "decision", r => r.Decision!.Recommended = "beta", "decision.recommended 'beta' is not one of the options" },
    };

    [Theory]
    [MemberData(nameof(MissingFields))]
    public void File_MissingOrWrongField_IsRefusedAndNothingIsStored(
        string kind, Action<FleetOutcomeFileRequest> spoil, string expected)
    {
        var request = kind switch { "ready" => Ready(), "finding" => Finding(), _ => Decision() };
        spoil(request);
        var store = NewStore();

        var ex = Assert.Throws<ArgumentException>(() => store.File(TenantA, request, FleetManagerId, Now));

        Assert.StartsWith(expected, ex.Message);
        Assert.Empty(store.List(TenantA, "all", null, 50));
    }

    [Fact]
    public void Answer_Blank_IsRefusedAndTheRecordStaysOpen()
    {
        var store = NewStore();
        var filed = store.File(TenantA, Ready(), FleetManagerId, Now);

        var ex = Assert.Throws<ArgumentException>(
            () => store.Answer(TenantA, Guid.Parse(filed.Id), "   ", FleetManagerId, FleetOutcomeStore.RoleFleetManager, Now));

        Assert.StartsWith("answer is required", ex.Message);
        Assert.Equal("open", store.Get(TenantA, Guid.Parse(filed.Id))!.Status);
    }

    [Fact]
    public void Answer_UnknownRole_IsRefusedAndTheRecordStaysOpen()
    {
        var store = NewStore();
        var filed = store.File(TenantA, Ready(), FleetManagerId, Now);

        var ex = Assert.Throws<ArgumentException>(
            () => store.Answer(TenantA, Guid.Parse(filed.Id), "Merge it.", FleetManagerId, "worker", Now));

        Assert.Equal("answeredByRole 'worker' is not valid; use one of: owner, fleet-manager", ex.Message);
        Assert.Equal("open", store.Get(TenantA, Guid.Parse(filed.Id))!.Status);
    }

    [Fact]
    public void Answer_ByTheFleetManager_RecordsTheRole()
    {
        var store = NewStore();
        var filed = store.File(TenantA, Finding(), FleetManagerId, Now);

        var result = store.Answer(TenantA, Guid.Parse(filed.Id), "Thanks.", FleetManagerId,
            FleetOutcomeStore.RoleFleetManager, Now);

        Assert.Equal(FleetManagerId, result.Outcome!.AnsweredBy);
        Assert.Equal("fleet-manager", NewStore().Get(TenantA, Guid.Parse(filed.Id))!.AnsweredByRole);
    }

    /// <summary>
    /// AN ANSWER IS FINAL ACROSS INSTANCES. Two stores over the SAME database file stand in for two Gateway
    /// instances; many callers answer the same record at the same moment, split across both. Exactly one wins,
    /// every other caller is told the record was already answered, and the stored answer is the winner's.
    /// Repeated over several records, because a race that is lost only sometimes is still a race.
    /// </summary>
    [Fact]
    public void Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins()
    {
        var instances = new[] { NewStore(), NewStore() };
        const int rounds = 12;
        const int callers = 8;

        for (var round = 0; round < rounds; round++)
        {
            var filed = instances[0].File(TenantA, Ready($"Round {round}"), FleetManagerId, Now);
            var id = Guid.Parse(filed.Id);
            using var start = new Barrier(callers);
            var results = new FleetOutcomeAnswerResult[callers];

            var threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                results[i] = instances[i % 2].Answer(TenantA, id, $"answer {i}",
                    i % 2 == 0 ? FleetOutcomeStore.OwnerCaller : FleetManagerId,
                    i % 2 == 0 ? FleetOutcomeStore.RoleOwner : FleetOutcomeStore.RoleFleetManager, Now);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            var winners = results.Select((r, i) => (r, i)).Where(x => x.r.Status == FleetOutcomeAnswerStatus.Answered).ToList();
            Assert.Single(winners);
            Assert.Equal(callers - 1, results.Count(r => r.Status == FleetOutcomeAnswerStatus.AlreadyAnswered));

            var stored = NewStore().Get(TenantA, id)!;
            Assert.Equal($"answer {winners[0].i}", stored.Answer);
            // Every loser was shown the winner's answer, not its own.
            Assert.All(results.Where(r => r.Status == FleetOutcomeAnswerStatus.AlreadyAnswered),
                r => Assert.Equal(stored.Answer, r.Outcome!.Answer));
        }
    }

    /// <summary>
    /// NOTHING OPEN IS CUT SHORT. More open records than one list page holds all come back from the open read,
    /// and the counts are the database's, including every kind.
    /// </summary>
    [Fact]
    public void ListOpen_MoreThanAPage_ReturnsEveryOpenRecord_AndCountOpenAgrees()
    {
        var store = NewStore();
        var over = FleetOutcomeStore.MaxCount + 3;
        for (var i = 0; i < over; i++)
            store.File(TenantA, Ready($"Ready {i}"), FleetManagerId, Now.AddSeconds(i));
        store.File(TenantA, Finding(), FleetManagerId, Now);
        store.File(TenantA, Decision(), FleetManagerId, Now);
        var answered = store.File(TenantA, Finding("answered one"), FleetManagerId, Now);
        store.Answer(TenantA, Guid.Parse(answered.Id), "Seen.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now);
        store.File(TenantB, Finding("another account's"), FleetManagerId, Now);

        var open = store.ListOpen(TenantA);
        var counts = store.CountOpen(TenantA);

        Assert.Equal(over + 2, open.Count);
        Assert.All(open, o => Assert.Equal("open", o.Status));
        Assert.Equal($"Ready {over - 1}", open[0].Title);
        Assert.Equal(over, counts.Ready);
        Assert.Equal(1, counts.Finding);
        Assert.Equal(1, counts.Decision);
        Assert.Equal(over + 2, counts.Total);
        Assert.Equal(over + 2, store.Count(TenantA, "open", null));
        Assert.Equal(over + 3, store.Count(TenantA, "all", null));
        Assert.Equal(1, store.Count(TenantA, "answered", "finding"));
    }
}
