using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using CcDirector.Gateway.Tests.Factory.Triggers;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE RIG FOR THE MAIL DESK HAND-OFF (phase 3, task F): the real stores over a throwaway database, the real
/// <see cref="TriggerService"/> handing to the real <see cref="MailDeskService"/>, and the Director's trigger routes and
/// the Mail Desk routes booted together on an in-memory test server, exactly as GatewayHost maps them. The model is a
/// scripted fake whose call log is the proof of what was asked; the session starter is a fake that records every start;
/// the clock is injected. No port, no network, no real Director, no sleep.
///
/// Machines and addresses are invented: <c>machine-a</c> (the start's own machine), <c>machine-b</c> (its backup), and
/// mailboxes at the reserved example domains.
/// </summary>
internal sealed class MailDeskHandOffRig : IAsyncDisposable
{
    public static readonly TenantId Account = new("acct-mail");
    public const string MachineA = "machine-a";
    public const string MachineB = "machine-b";
    public const string DirectorA = "dir-a";
    public const string DirectorB = "dir-b";
    public const string Person = "person";
    public const string P = MailDeskGate.Prefix;

    private static readonly Dictionary<string, string> Directors = new(StringComparer.OrdinalIgnoreCase)
    {
        [DirectorA] = MachineA, [DirectorB] = MachineB,
    };

    private readonly GatewayDbTestHarness _h = new();
    public GatewayDatabase Db { get; }
    public MailDeskSwitch Switch { get; }
    public MailDeskChannelStore Channels { get; }
    public MailDeskRulesStore Rules { get; }
    public MailDeskDecisionStore Decisions { get; }
    public MailDeskSettingsStore Settings { get; }
    public MailDeskChannelCheckStore Checks { get; }
    public TriggerStore TriggerStore { get; }
    public MailDeskService? MailDesk { get; }
    public TriggerService Triggers { get; }
    public CronRunResultService Problems { get; }
    public ScriptedMailDeskModel Model { get; } = new();
    public List<NewSessionRequest> Starts { get; } = new();
    public DateTime Now { get; set; } = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    public Guid TriggerId { get; private set; }

    private WebApplication? _app;
    private HttpClient? _client;

    /// <param name="wired">False builds the trigger service with no Mail Desk service, as a Gateway built without it.</param>
    /// <param name="switchOn">Whether the account's Mail Desk switch is on.</param>
    public MailDeskHandOffRig(bool wired = true, bool switchOn = true)
    {
        Db = _h.Open();
        Switch = new MailDeskSwitch(new TenantSettingsStore(Db));
        if (switchOn) Switch.Set(Account, true, "email@devthrottle.com", "the owner's account only", Now);
        Channels = new MailDeskChannelStore(Db);
        Rules = new MailDeskRulesStore(Db);
        Decisions = new MailDeskDecisionStore(Db);
        Settings = new MailDeskSettingsStore(Db);
        Checks = new MailDeskChannelCheckStore(Db);
        TriggerStore = new TriggerStore(Db);
        MailDesk = wired
            ? new MailDeskService(Channels, Rules, Decisions, Settings, Checks, _ => Model, () => Now)
            : null;

        var d = FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition!;
        var seat = d.Seats.Single(s => s.Seat == "outreach") with { Seat = "mail-desk", Name = "Mail Desk" };
        var published = new PublishedFactoryDefinition(1, d with { Factory = MailDeskTemplate.Factory, BossSeat = "mail-desk", Seats = new[] { seat } });
        Triggers = new TriggerService(TriggerStore, new FactoryActivityRecord(Db),
            (_, request, _) =>
            {
                lock (Starts) Starts.Add(request);
                return Task.FromResult(TriggerStartAttempt.Started("dddddddd-0000-4000-8000-000000000001"));
            },
            findSession: (_, _) => null, findSessionByName: (_, _) => null, timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true,
            definitionOf: (_, factory) => factory == MailDeskTemplate.Factory ? published : null,
            mailDeskOn: Switch.IsOn, mailDesk: MailDesk);

        var runs = new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        Problems = new CronRunResultService(runs,
            jobById: _ => null,
            allJobs: () => Array.Empty<CronJobDto>(),
            endingsOf: _ => new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal),
            zoneOf: _ => TimeZoneInfo.Utc,
            writeActivity: (tenant, run, row, correction) => runs.WriteActivityRow(tenant, run, row, correction),
            nowUtc: () => Now,
            triggers: TriggerStore,
            scheduleSessionsByName: (_, _, _) => Array.Empty<string>(),
            settleLastStatus: (_, _, _) => false,
            mailDesk: new MailDeskProblemSource(Switch.IsOn, Channels, Checks, t => MailDeskSpend.Of(t, Decisions, Settings)),
            lastKnownSession: (_, _, _) => null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    // ---------- the account ----------

    /// <summary>The Mail Desk start, as a publish writes it: on machine-a, every 5 minutes, with machine-b as its backup
    /// when <paramref name="backup"/>.</summary>
    public Guid AddMailDeskTrigger(bool backup = false, string checkPath = MailDeskTemplate.CheckPath)
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
            DefinitionVersion = 1,
            CheckPath = checkPath,
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        TriggerId = row.Id;
        return row.Id;
    }

    public MailDeskChannelEntity Channel(string address, params string[] machines)
        => Channels.Add(Account, address, "outlook", machines.Length == 0 ? new[] { MachineA } : machines,
            MailDeskWho.Person("seed"), "seeded for the test", Now).Channel!;

    /// <summary>Save a rules document as the account's newest version.</summary>
    public void SaveRules(MailDeskRulesDocument document)
    {
        var validation = MailDeskRulesValidator.Validate(document);
        Assert.True(validation.IsValid, validation.Refusal);
        var newest = Rules.Newest(Account);
        var change = new MailDeskChange(MailDeskWords.ChangeKinds.Route, MailDeskWords.ChangeActions.Add, "seed", "{}",
            MailDeskWho.Person("seed"), "seeded for the test");
        var write = Rules.Save(Account, newest.Version, validation.Document!, change, Now);
        Assert.NotNull(write);
    }

    /// <summary>A document with one route, an allow-list entry for each action, and a block entry.</summary>
    public static MailDeskRulesDocument Document() => new()
    {
        Rules = new[]
        {
            new MailDeskRule
            {
                Name = "citizenship",
                When = new MailDeskRuleWhen { SubjectContains = new[] { "citizen" } },
                Route = new MailDeskRouteTarget { Factory = "citizenship", Seat = "intake" },
            },
        },
        Allow = new[]
        {
            new MailDeskAllowEntry { From = "*@notifications.example.com", What = "build notifications", Do = MailDeskAllowDo.Archive },
            new MailDeskAllowEntry { From = "news@example.org", What = "a newsletter I read", Do = MailDeskAllowDo.KeepForOwner },
        },
        Block = new[] { new MailDeskBlockEntry { From = "*@spammy.example", Why = "a spam domain" } },
        Routes = new[] { new MailDeskRouteEntry { Factory = "devthrottle", Seat = "mail-desk", Handles = "customers of DevThrottle" } },
    };

    // ---------- reading back ----------

    public List<TriggerRunEntity> Runs()
    {
        using var ctx = Db.CreateContext(Account);
        return ctx.TriggerRuns.Where(r => r.TriggerId == TriggerId).OrderBy(r => r.RecordedUtc).ToList();
    }

    public string? KeptMemory()
        => TriggerStore.CheckStates.StateFor(Account, MailDeskTemplate.Factory, FactoryCheckStateStore.TriggerOwner(TriggerId));

    public int KeptMemoryRows()
    {
        using var ctx = Db.CreateContext(Account);
        var owner = FactoryCheckStateStore.TriggerOwner(TriggerId);
        return ctx.FactoryCheckStates.Count(s => s.OwnerKey == owner);
    }

    public List<MailDeskDecisionEntity> DecisionRows()
    {
        using var ctx = Db.CreateContext(Account);
        return ctx.MailDeskDecisions.OrderBy(d => d.ReceivedUtc).ThenBy(d => d.MessageKey).ToList();
    }

    public List<CronRunProblemDto> OpenProblems(string? factory = MailDeskWords.DefaultFactory)
        => Problems.Problems(Account, factory, includeClosed: false).Problems;

    /// <summary>One channel's entry in a memory, or null.</summary>
    public static JsonObject? Entry(string? memory, Guid channelId)
        => memory is null ? null
            : JsonNode.Parse(memory)!["channels"]!.AsArray().OfType<JsonObject>()
                .FirstOrDefault(e => e["id"]!.GetValue<string>() == channelId.ToString("D"));

    // ---------- the service path ----------

    /// <summary>Report through the service, as the route does, and wait for the screening when one began.</summary>
    public async Task<(TriggerReportResult Result, TriggerRunEntity? Run)> ReportAsync(TriggerCheckReport report,
        string director = DirectorA)
    {
        var result = await Triggers.ReportCheckAsync(Account, director, TriggerId.ToString("D"), report, CancellationToken.None,
            Directors[director]);
        var run = result.Starting is { } starting ? await starting : result.Run;
        return (result, run);
    }

    /// <summary>The memory a Director on <paramref name="director"/>'s machine is handed with the Mail Desk start.</summary>
    public string? Handed(string director = DirectorA)
        => Triggers.AssignmentsFor(Account, director, Directors[director]).SingleOrDefault(a => a.Id == TriggerId.ToString("D"))?.State;

    // ---------- the routes ----------

    public async Task<HttpClient> ClientAsync()
    {
        if (_client is not null) return _client;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // The authentication middleware's stand-in: a person's device for the Mail Desk routes.
            if (ctx.Request.Headers["X-Test-Caller"].ToString() == Person)
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("owner-browser-1", Account.Value, "browser", "active");
            }
            await next();
        });
        TenantId? Resolve(Microsoft.AspNetCore.Http.HttpContext _) => Account;
        TriggerEndpoints.Map(app, Resolve, Triggers, directorMachine: (_, id) => Directors.GetValueOrDefault(id),
            nowUtc: () => Now, sessionFactoryOf: _ => SessionFactoryLookup.NotKnown, isDefined: (_, _) => true,
            dueChecks: DueCheckTestParts.NoDueChecks(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".due.json")));
        var decisions = Decisions;
        MailDeskEndpoints.Map(MailDeskGate.Group(app, Switch, Resolve), Resolve, new MailDeskEndpoints.Parts(
            Channels, Rules, decisions, Settings,
            new MailDeskChannelFold(t => TriggerStore.List(t), _ => "UTC", decisions, Checks),
            _ => SessionFactoryLookup.NotKnown, _ => new[] { MachineA, MachineB }, () => Now,
            t => MailDeskSpend.Of(t, decisions, Settings)));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    /// <summary>The memory the route hands a Director with the Mail Desk start.</summary>
    public async Task<string?> PullAsync(string director = DirectorA)
    {
        var client = await ClientAsync();
        using var response = await client.GetAsync($"/directors/{director}/triggers");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var t in doc.RootElement.GetProperty("triggers").EnumerateArray())
            if (t.GetProperty("id").GetString() == TriggerId.ToString("D"))
                return t.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return null;
    }

    /// <summary>Report through the Director's route, then wait for the screening the route began.</summary>
    public async Task<(int Status, string Body, TriggerRunEntity? Run)> PostReportAsync(TriggerCheckReport report, string director = DirectorA)
    {
        var client = await ClientAsync();
        var before = Triggers.LastScreeningForTest(TriggerId);
        using var response = await client.PostAsync($"/directors/{director}/triggers/{TriggerId:D}/checks",
            new StringContent(JsonSerializer.Serialize(report), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        var after = Triggers.LastScreeningForTest(TriggerId);
        var run = after is not null && !ReferenceEquals(before, after) ? await after : null;
        return ((int)response.StatusCode, body, run);
    }

    public async Task<(int Status, JsonElement Body)> PersonPostAsync(string path, object body)
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-Caller", Person);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public async Task<JsonElement> PersonGetAsync(string path)
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Caller", Person);
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}

/// <summary>A model that answers by a script and logs every question it was asked, with the email's subject.</summary>
internal sealed class ScriptedMailDeskModel : IMailDeskModel
{
    public const string PersonOrSpam = "person-or-spam";
    public const string Route = "route";

    /// <summary>What a question about an email is answered with; by default a person at 0.99, routed to the first
    /// route offered at 0.99.</summary>
    public Func<string, MailDeskEmail, MailDeskModelResult> Answer { get; set; } = (question, _) => question == PersonOrSpam
        ? Label(ScreeningLabels.Person, 0.99)
        : Label("A", 0.99);

    /// <summary>When set, every question waits for it - how a test holds a screening open.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public List<(string Question, string Subject)> Calls { get; } = new();
    public List<MailDeskEmail> Emails { get; } = new();

    public static MailDeskModelResult Label(string label, double? confidence, decimal cost = 0.0001m)
        => new(MailDeskAnswerKind.Label, label, confidence, cost, 120, 1, null, null);

    public static MailDeskModelResult Failure(string kind = MailDeskModelErrorKinds.Timeout)
        => MailDeskModelResult.Failure(kind, "the hosted model did not answer in time");

    public async Task<MailDeskModelResult> AskPersonOrSpamAsync(MailDeskEmail email, CancellationToken ct = default)
    {
        lock (Calls) { Calls.Add((PersonOrSpam, email.Subject)); Emails.Add(email); }
        if (Hold is { } hold) await hold.Task;
        return Answer(PersonOrSpam, email);
    }

    public async Task<MailDeskModelResult> AskRouteAsync(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes, CancellationToken ct = default)
    {
        lock (Calls) { Calls.Add((Route, email.Subject)); Emails.Add(email); }
        if (Hold is { } hold) await hold.Task;
        var answer = Answer(Route, email);
        // The fake answers letters as the real model does; the label is the route's name at that letter.
        if (answer.Kind == MailDeskAnswerKind.Label && answer.Label is { Length: 1 } letter && char.IsUpper(letter[0]))
            answer = answer with { Label = routes[letter[0] - 'A'].Name };
        return answer;
    }
}

/// <summary>The labels of the person-or-spam question, as <see cref="MailDeskScreening"/> reads them.</summary>
internal static class ScreeningLabels
{
    public const string Person = MailDeskScreening.PersonLabel;
    public const string Spam = MailDeskScreening.SpamLabel;
}

/// <summary>One email the stand-in check reports.</summary>
internal sealed record StandInMail(string Key, string Sender, string Subject, DateTime ReceivedUtc, bool Known = false,
    string Preview = "Hello, I have a question about my account.");

/// <summary>
/// THE STAND-IN SCREENING CHECK: does to the handed memory what <c>checks/screen.py</c> does (phase 2 sections 3 and 4,
/// phase 3 section 6.1), without a mailbox. A channel told to fail gets a section with its failure and its memory entry
/// back exactly as handed; a channel told to read reports its mail, moves its cursor to the newest message and adds the
/// keys; every other channel is read with nothing new. The report is what a Director would send.
/// </summary>
internal static class StandInCheck
{
    public sealed record Plan(IReadOnlyDictionary<string, IReadOnlyList<StandInMail>> Mail, IReadOnlyDictionary<string, string> Failures);

    public static Plan Reads(params (string Address, StandInMail[] Mail)[] mail)
        => new(mail.ToDictionary(m => m.Address, m => (IReadOnlyList<StandInMail>)m.Mail), new Dictionary<string, string>());

    public static Plan With(this Plan plan, string failingAddress, string sentence)
        => plan with { Failures = new Dictionary<string, string>(plan.Failures) { [failingAddress] = sentence } };

    public static TriggerCheckReport Run(string? handed, Plan plan, DateTime checkedUtc)
    {
        var memory = JsonNode.Parse(handed ?? """{"channels":[]}""")!.AsObject();
        var sections = new JsonArray();
        var count = 0;
        foreach (var entry in memory["channels"]!.AsArray().OfType<JsonObject>())
        {
            var address = entry["address"]!.GetValue<string>();
            var section = new JsonObject
            {
                ["id"] = entry["id"]!.GetValue<string>(),
                ["address"] = address,
                ["provider"] = entry["provider"]!.GetValue<string>(),
                ["mode"] = entry["mode"]!.GetValue<string>(),
            };
            var messages = new JsonArray();
            if (plan.Failures.TryGetValue(address, out var sentence))
            {
                section["messages"] = messages;
                section["filing"] = new JsonArray();
                section["more"] = false;
                section["failure"] = new JsonObject { ["sentence"] = sentence, ["detail"] = "" };
                sections.Add(section);
                continue;
            }
            var mail = plan.Mail.TryGetValue(address, out var m) ? m : Array.Empty<StandInMail>();
            var keys = entry["keys"] as JsonObject ?? new JsonObject();
            foreach (var e in mail.OrderBy(x => x.ReceivedUtc))
            {
                messages.Add(new JsonObject
                {
                    ["messageKey"] = e.Key,
                    ["receivedUtc"] = e.ReceivedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["sender"] = e.Sender,
                    ["senderName"] = "Some One",
                    ["subject"] = e.Subject,
                    ["hasAttachments"] = false,
                    ["knownSender"] = e.Known,
                    ["preview"] = e.Preview,
                });
                keys[e.Key] = new JsonObject { ["id"] = "graph-id-" + e.Key, ["reportedUtc"] = checkedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") };
                count++;
            }
            if (mail.Count > 0)
            {
                var newest = mail.Max(x => x.ReceivedUtc);
                entry["cursor"] = new JsonObject
                {
                    ["newest"] = newest.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["idsAtNewest"] = new JsonArray(mail.Where(x => x.ReceivedUtc == newest).Select(x => (JsonNode)JsonValue.Create("graph-id-" + x.Key)!).ToArray()),
                };
                entry["keys"] = keys.DeepClone();
            }
            entry["sentCache"] ??= new JsonObject { ["refreshedUtc"] = checkedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["hashes"] = new JsonArray() };
            section["messages"] = messages;
            section["filing"] = new JsonArray();
            section["more"] = false;
            sections.Add(section);
        }
        var context = new JsonObject { ["version"] = 2, ["channels"] = sections }.ToJsonString();
        var output = JsonSerializer.Serialize(new { count, context });
        return new TriggerCheckReport { CheckedAtUtc = checkedUtc, ExitCode = 0, Output = output, State = memory.ToJsonString() };
    }

    /// <summary>A check that exited non-zero: the whole run failed (phase 3, section 6), as phase 2's "no channels".</summary>
    public static TriggerCheckReport Failed(DateTime checkedUtc, string handed)
        => new() { CheckedAtUtc = checkedUtc, ExitCode = 1, Output = "", ErrorOutput = "the memory has no channels to read", State = handed };
}
