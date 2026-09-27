using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// The library's only store reader, over the real EF store on a throwaway SQLite file: it reads the
/// ROUTE-resolved tenant's turn-submitted rows and nothing else, it honours the window, it reports where
/// the tenant's record begins, and it joins session history for the repository split. The rows are written
/// through the real <see cref="ActivityEventStore"/> and <see cref="SessionHistoryStore"/>, the same way the
/// Gateway writes them, so the projection the reader selects is proven against the columns the writers fill.
/// </summary>
public sealed class ThrottleLedgerReaderTests : IDisposable
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime From = new(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private ActivityEventStore LedgerFor(TenantId tenant) => new(_harness.Open(new FixedTenantContext(tenant)));

    private SessionHistoryStore HistoryFor(TenantId tenant) => new(_harness.Open(new FixedTenantContext(tenant)));

    private ThrottleLedgerReader Reader() => new(_harness.Open(new AsyncLocalTenantContext()));

    private static ActivityEventRecord Submission(DateTime at, string session, string? origin, string? source,
        string? agent = "ClaudeCode", long sequence = 1) => new()
    {
        EventId = Guid.NewGuid(),
        DirectorSequence = sequence,
        OccurredUtc = at,
        DirectorId = "dir-1",
        SessionId = session,
        Machine = "SOREN_NORTH",
        AgentKind = agent,
        EventType = ActivityEventTypes.TurnSubmitted,
        Cause = source == "Agent" ? ActivityCauses.AgentSubmit : ActivityCauses.OwnerSubmit,
        InputOrigin = origin,
        SendSource = source,
    };

    private static ActivityEventRecord Transition(DateTime at, string session) => new()
    {
        EventId = Guid.NewGuid(),
        DirectorSequence = 99,
        OccurredUtc = at,
        DirectorId = "dir-1",
        SessionId = session,
        AgentKind = "ClaudeCode",
        EventType = ActivityEventTypes.ActivityTransition,
        PreviousState = "WaitingForInput",
        NewState = "Working",
        Cause = ActivityCauses.OwnerSubmit,
        // A transition row carrying an origin-shaped token must NOT be counted: the predicate reads
        // turn-submitted rows only, and this is how the reader is caught if it ever stops filtering.
        InputOrigin = "voice/desktop",
        SendSource = "UserInput",
    };

    /// <summary>Records sessions in a tenant's history as started by a person, the way the Director's history
    /// push does, so their turns are in the headline (only sessions a person started are).</summary>
    private void StartedByAPerson(TenantId tenant, params string[] sessionIds)
    {
        var history = HistoryFor(tenant);
        var now = DateTime.UtcNow;
        foreach (var id in sessionIds)
            history.UpsertLive("dir-1", new SessionDto
            {
                SessionId = id, Agent = "ClaudeCode", CreatedAt = now.AddHours(-1), ActivityState = "Working",
                Status = "Running", OriginKind = "human",
            }, now);
    }

    [Fact]
    public void Reads_only_the_named_tenants_turn_submitted_rows_inside_the_window()
    {
        StartedByAPerson(TenantA, "s1");
        StartedByAPerson(TenantB, "s-b");
        var ledgerA = LedgerFor(TenantA);
        ledgerA.AppendBatch(new[]
        {
            Submission(From.AddHours(1), "s1", "typed/desktop", null, sequence: 1),
            Submission(From.AddHours(2), "s1", "voice/desktop", "UserInput", sequence: 2),
            Submission(From.AddHours(3), "s1", null, "UserInput", sequence: 3),
            Submission(From.AddHours(4), "s1", null, "Agent", sequence: 4),
            Submission(From.AddDays(-1), "s1", "typed/desktop", null, sequence: 5),   // before the window
            Submission(To, "s1", "typed/desktop", null, sequence: 6),                 // at the exclusive end
            Transition(From.AddHours(5), "s1"),
        });
        // Tenant B has its own rows under its own key space; none of them may reach tenant A's figure.
        LedgerFor(TenantB).AppendBatch(new[]
        {
            Submission(From.AddHours(1), "s-b", "voice/phone", "Delivery", sequence: 1),
            Submission(From.AddHours(2), "s-b", "voice/phone", "Delivery", sequence: 2),
        });

        var figure = Reader().Compute(TenantA, From, To);

        Assert.Equal(2, figure.Turns);
        Assert.Equal(1, figure.VoiceTurns);
        Assert.Equal(1, figure.TypedTurns);
        Assert.Equal(1, figure.Excluded.Unresolved);
        Assert.Equal(1, figure.AgentDrivenTurns);
        Assert.DoesNotContain(figure.Buckets, b => b.Surface == "phone");
        Assert.Equal(1, figure.Sessions);

        // And tenant B, read through the same reader, sees only its own.
        var figureB = Reader().Compute(TenantB, From, To);
        Assert.Equal(2, figureB.Turns);
        Assert.Equal("phone", Assert.Single(figureB.Buckets).Surface);
    }

    [Fact]
    public void Reports_where_the_tenants_record_begins_and_the_retention()
    {
        var reader = Reader();

        var empty = reader.Compute(TenantA, From, To);
        Assert.Null(empty.Ledger.EarliestUtc);
        Assert.Equal(30, empty.Ledger.RetentionDays);

        LedgerFor(TenantA).AppendBatch(new[]
        {
            Submission(From.AddDays(2), "s1", "typed/desktop", null, sequence: 1),
            Submission(From.AddDays(1), "s1", "typed/desktop", null, sequence: 2),
        });

        var figure = reader.Compute(TenantA, From, To);
        // The oldest turn-submitted row the tenant holds, whether or not it is in the window asked for.
        Assert.Equal(From.AddDays(1), figure.Ledger.EarliestUtc);
        Assert.Equal(From, figure.Window.FromUtc);
        Assert.Equal(To, figure.Window.ToUtc);
    }

    [Fact]
    public void Joins_session_history_for_the_repository_split_and_discloses_the_unplaced()
    {
        var history = HistoryFor(TenantA);
        var now = DateTime.UtcNow;
        history.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "named", RepoName = "thefrederiksen/devthrottle", RepoPath = @"D:\ReposFred\devthrottle",
            Agent = "ClaudeCode", CreatedAt = now.AddHours(-1), ActivityState = "Working", Status = "Running",
            OriginKind = "human",
        }, now);
        history.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "path-only", RepoPath = @"D:\ReposFred\mindzieWeb",
            Agent = "ClaudeCode", CreatedAt = now.AddHours(-1), ActivityState = "Working", Status = "Running",
            OriginKind = "human",
        }, now);
        history.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "no-repo", Agent = "ClaudeCode", CreatedAt = now.AddHours(-1), ActivityState = "Working",
            Status = "Running", OriginKind = "human",
        }, now);
        history.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "a-seat", RepoPath = @"D:\ReposFred\devthrottle", Agent = "Codex", CreatedAt = now.AddHours(-1),
            ActivityState = "Working", Status = "Running", OriginKind = "agent", ParentSessionId = "named",
        }, now);
        // Tenant B's history holds a row for the SAME session id with a different repository. The join
        // must read tenant A's history, so this name must not appear.
        HistoryFor(TenantB).UpsertLive("dir-9", new SessionDto
        {
            SessionId = "named", RepoName = "someone-else/private-repo", RepoPath = @"C:\other",
            Agent = "Codex", CreatedAt = now.AddHours(-1), ActivityState = "Working", Status = "Running",
        }, now);

        LedgerFor(TenantA).AppendBatch(new[]
        {
            Submission(From.AddHours(1), "named", "typed/desktop", null, sequence: 1),
            Submission(From.AddHours(2), "named", "voice/desktop", "UserInput", sequence: 2),
            Submission(From.AddHours(3), "path-only", "typed/desktop", null, sequence: 3),
            Submission(From.AddHours(4), "no-repo", "typed/desktop", null, sequence: 4),
            // Neither of these is a session a person is known to have started: out of the figure, in the Starters.
            Submission(From.AddHours(5), "unknown-to-history", "typed/desktop", null, sequence: 5),
            Submission(From.AddHours(6), "a-seat", "voice/desktop", "UserInput", sequence: 6),
        });

        var figure = Reader().Compute(TenantA, From, To);

        Assert.Equal(4, figure.Turns);
        Assert.Equal(2, figure.Excluded.NotStartedByYou);
        Assert.Equal(new[] { 3, 1, 0, 1 }, figure.Starters.Groups.Select(g => g.Sessions));
        Assert.Equal(2, figure.Repos.Count);
        Assert.Equal("thefrederiksen/devthrottle", figure.Repos[0].Repo);
        Assert.Equal("devthrottle", figure.Repos[0].RepoName);
        Assert.Equal(2, figure.Repos[0].Turns);
        Assert.Equal(new[] { @"D:\ReposFred\devthrottle" }, figure.Repos[0].Checkouts);
        Assert.Equal("mindzieWeb", figure.Repos[1].Repo);
        Assert.Equal(1, figure.ReposUnattributedTurns);
        Assert.DoesNotContain(figure.Repos, r => r.Repo.Contains("private-repo"));
    }

    [Fact]
    public void Refuses_an_invalid_tenant_and_an_empty_window_loudly()
    {
        var reader = Reader();
        Assert.Throws<ArgumentException>(() => reader.Compute(default, From, To));
        Assert.Throws<ArgumentException>(() => reader.Compute(TenantA, To, From));
        Assert.Throws<ArgumentException>(() => reader.Compute(TenantA, From, From));
    }
}
