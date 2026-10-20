using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE RIG FOR ROUTING IN LIVE MODE (phase 4, task B): the real stores over a throwaway database, the real
/// <see cref="MailDeskService"/>, the real <see cref="MailDeskRouter"/> and the real <see cref="TriggerService"/> that runs
/// it, with the session starter and the session list as fakes and the clock injected. No port, no network, no real
/// Director, no sleep.
///
/// Three factories are published: <c>mail-desk</c> (seat <c>mail-desk</c>), <c>qa-books</c> (seat <c>clerk</c>) and
/// <c>qa-support</c> (seat <c>desk</c>). Machines, Directors and mailboxes are invented, at the reserved example domains.
/// </summary>
internal sealed class MailDeskRoutingRig : IAsyncDisposable
{
    public static readonly TenantId Account = new("acct-route");
    public const string MachineA = "machine-a";
    public const string MachineB = "machine-b";
    public const string DirectorA = "dir-a";
    public const string DirectorB = "dir-b";
    public const string Books = "qa-books";
    public const string Clerk = "clerk";
    public const string Support = "qa-support";
    public const string Desk = "desk";
    public const string MailDeskFactory = MailDeskWords.DefaultFactory;
    public const string MailDeskSeat = MailDeskWords.DefaultSeat;

    private static readonly Dictionary<string, string> Directors = new(StringComparer.OrdinalIgnoreCase)
    {
        [DirectorA] = MachineA, [DirectorB] = MachineB,
    };

    private readonly GatewayDbTestHarness _h = new();
    private int _sessionNumber;

    public GatewayDatabase Db { get; }
    public MailDeskSwitch Switch { get; }
    public MailDeskChannelStore Channels { get; }
    public MailDeskRulesStore Rules { get; }
    public MailDeskDecisionStore Decisions { get; }
    public MailDeskRoutedStore Routed { get; }
    public TriggerStore TriggerStore { get; }
    public MailDeskService MailDesk { get; }
    public MailDeskRouter Router { get; }
    public TriggerService Triggers { get; }
    public ScriptedMailDeskModel Model { get; } = new();
    public DateTime Now { get; set; } = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    public Guid TriggerId { get; private set; }

    /// <summary>Every start asked of the starter: the machine it was sent to and the create.</summary>
    public List<(string Machine, NewSessionRequest Request)> Starts { get; } = new();

    /// <summary>What the starter answers; by default a new session id each time.</summary>
    public Func<NewSessionRequest, TriggerStartAttempt> StartAnswer { get; set; }

    /// <summary>The session list the fakes read: by id.</summary>
    public Dictionary<string, SessionDto> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The published definitions, by factory. Remove one to make a factory with no published definition.</summary>
    public Dictionary<string, PublishedFactoryDefinition> Published { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, the router's starter throws this instead of answering.</summary>
    public Exception? StartThrows { get; set; }

    public MailDeskRoutingRig()
    {
        StartAnswer = _ => TriggerStartAttempt.Started(NextSessionId());
        Db = _h.Open();
        Switch = new MailDeskSwitch(new TenantSettingsStore(Db));
        Switch.Set(Account, true, "email@devthrottle.com", "the owner's account only", Now);
        Channels = new MailDeskChannelStore(Db);
        Rules = new MailDeskRulesStore(Db);
        Decisions = new MailDeskDecisionStore(Db);
        Routed = new MailDeskRoutedStore(Db);
        var settings = new MailDeskSettingsStore(Db);
        TriggerStore = new TriggerStore(Db);
        MailDesk = new MailDeskService(Channels, Rules, Decisions, settings, new MailDeskChannelCheckStore(Db), _ => Model, () => Now);

        Publish(MailDeskFactory, MailDeskSeat);
        Publish(Books, Clerk);
        Publish(Support, Desk);

        TriggerSessionStarter starter = (machine, request, _) =>
        {
            lock (Starts) Starts.Add((machine, request));
            if (StartThrows is { } thrown) throw thrown;
            return Task.FromResult(StartAnswer(request));
        };
        SessionDto? Find(TenantId _, string id) => Sessions.GetValueOrDefault(id);
        SessionDto? FindByName(TenantId _, string name) => Sessions.Values.FirstOrDefault(s => s.Name == name);
        PublishedFactoryDefinition? DefinitionOf(TenantId _, string factory) => Published.GetValueOrDefault(factory);

        Router = new MailDeskRouter(Routed, Channels, Decisions, starter, Find, FindByName, DefinitionOf,
            _ => TimeZoneInfo.Utc, () => Now, CancellationToken.None);
        Triggers = new TriggerService(TriggerStore, new FactoryActivityRecord(Db),
            starter, findSession: Find, findSessionByName: (_, _) => null, timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true,
            definitionOf: DefinitionOf, mailDeskOn: Switch.IsOn, mailDesk: MailDesk, mailDeskRouter: Router);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---------- the account ----------

    /// <summary>Publish <paramref name="factory"/> as version 1 holding the seats named.</summary>
    public void Publish(string factory, params string[] seats)
    {
        var d = FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition!;
        var template = d.Seats.Single(s => s.Seat == "outreach");
        var held = seats.Select(s => template with { Seat = s, Name = s }).ToArray();
        Published[factory] = new PublishedFactoryDefinition(1, d with { Factory = factory, BossSeat = seats[0], Seats = held });
    }

    /// <summary>The Mail Desk start, on machine-a, with machine-b as its backup when <paramref name="backup"/>.</summary>
    public TriggerEntity AddMailDeskTrigger(bool backup = false)
    {
        using var ctx = Db.CreateContext(Account);
        var row = new TriggerEntity
        {
            Name = "mail-desk-screen",
            Factory = MailDeskTemplate.Factory,
            FactoryAgent = "mail-desk",
            Machine = MachineA,
            BackupMachinesJson = backup ? "[\"" + MachineB + "\"]" : "[]",
            RepoPath = "",
            CheckCommand = "",
            IntervalSeconds = 300,
            Prompt = "The Mail Desk screening handed you these messages. {context}",
            CreatedBy = "definition of mail-desk, published by email@devthrottle.com",
            CreatedUtc = Now.AddMinutes(-1),
            Seat = "mail-desk",
            DefinitionVersion = 4,
            CheckPath = MailDeskTemplate.CheckPath,
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        TriggerId = row.Id;
        return row;
    }

    public TriggerEntity Trigger()
    {
        using var ctx = Db.CreateContext(Account);
        return ctx.Triggers.Single(t => t.Id == TriggerId);
    }

    /// <summary>A channel on machine-a and machine-b, switched to live unless <paramref name="live"/> is false.</summary>
    public MailDeskChannelEntity Channel(string address, bool live = true)
    {
        var who = MailDeskWho.Person("seed");
        var added = Channels.Add(Account, address, "outlook", new[] { MachineA, MachineB }, who, "seeded for the test", Now).Channel!;
        if (!live) return added;
        return Channels.SetMode(Account, address, MailDeskWords.Modes.Live, who, "live for the test", Now).Channel!;
    }

    public void SetMode(string address, string mode)
        => Assert.True(Channels.SetMode(Account, address, mode, MailDeskWho.Person("seed"), "changed for the test", Now).Succeeded);

    /// <summary>Record one decision directly, as the screening would.</summary>
    public MailDeskDecisionEntity Decide(MailDeskChannelEntity channel, string key, string decision, string? factory = null,
        string? seat = null, string mode = MailDeskWords.Modes.Live, int minutesAgo = 10, string subject = "A question",
        string sender = "someone@example.net")
        => Decisions.Insert(Account, new MailDeskDecisionInput(channel.Id, key, Now.AddMinutes(-minutesAgo), sender, subject,
            decision == MailDeskWords.Decisions.Route ? MailDeskWords.Layers.Rule : MailDeskWords.Layers.RouteModel,
            decision, factory, seat, decision == MailDeskWords.Decisions.Route ? "rule-for-" + factory : "unsure", null, null,
            mode, null, null, Now.AddMinutes(-minutesAgo + 1))).Decision;

    /// <summary>The account's rules: invoices to qa-books/clerk, support to qa-support/desk.</summary>
    public void SaveRoutingRules()
    {
        var document = new MailDeskRulesDocument
        {
            Rules = new[]
            {
                new MailDeskRule
                {
                    Name = "invoices",
                    When = new MailDeskRuleWhen { SubjectContains = new[] { "invoice" } },
                    Route = new MailDeskRouteTarget { Factory = Books, Seat = Clerk },
                },
                new MailDeskRule
                {
                    Name = "support",
                    When = new MailDeskRuleWhen { SubjectContains = new[] { "support" } },
                    Route = new MailDeskRouteTarget { Factory = Support, Seat = Desk },
                },
            },
        };
        var validation = MailDeskRulesValidator.Validate(document);
        Assert.True(validation.IsValid, validation.Refusal);
        var change = new MailDeskChange(MailDeskWords.ChangeKinds.Rule, MailDeskWords.ChangeActions.Add, "seed", "{}",
            MailDeskWho.Person("seed"), "seeded for the test");
        Assert.NotNull(Rules.Save(Account, Rules.Newest(Account).Version, validation.Document!, change, Now));
    }

    // ---------- the sessions ----------

    public string NextSessionId() => $"eeeeeeee-0000-4000-8000-{Interlocked.Increment(ref _sessionNumber):D12}";

    /// <summary>The session list now has <paramref name="sessionId"/>, alive or ended.</summary>
    public void SessionIs(string sessionId, bool alive, string? name = null)
        => Sessions[sessionId] = new SessionDto
        {
            SessionId = sessionId,
            Name = name,
            MachineName = MachineA,
            ActivityState = alive ? "Working" : "Exited",
        };

    // ---------- running ----------

    /// <summary>Run the router as the trigger service does after a screened run, from <paramref name="director"/>.</summary>
    public Task<MailDeskRouterRun> RouteAsync(string director = DirectorA)
        => Router.RouteAsync(Account, Trigger(), director, Directors[director], Now);

    /// <summary>Report through the trigger service, as the route does, and wait for the screening and the router.</summary>
    public async Task<TriggerRunEntity?> ReportAsync(TriggerCheckReport report, string director = DirectorA)
    {
        var result = await Triggers.ReportCheckAsync(Account, director, TriggerId.ToString("D"), report, CancellationToken.None,
            Directors[director]);
        return result.Starting is { } starting ? await starting : result.Run;
    }

    /// <summary>The memory a Director on <paramref name="director"/>'s machine is handed with the Mail Desk start.</summary>
    public string? Handed(string director = DirectorA)
        => Triggers.AssignmentsFor(Account, director, Directors[director]).SingleOrDefault(a => a.Id == TriggerId.ToString("D"))?.State;

    // ---------- reading back ----------

    public List<MailDeskRoutedEntity> Rows()
    {
        using var ctx = Db.CreateContext(Account);
        return ctx.MailDeskRouted.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToList();
    }

    public MailDeskRoutedEntity Row(Guid decisionId) => Routed.ForDecision(Account, decisionId)!;

    public MailDeskDecisionEntity DecisionRow(Guid id) => Decisions.Get(Account, id)!;

    public List<TriggerRunEntity> Runs()
    {
        using var ctx = Db.CreateContext(Account);
        return ctx.TriggerRuns.Where(r => r.TriggerId == TriggerId).OrderBy(r => r.RecordedUtc).ToList();
    }
}
