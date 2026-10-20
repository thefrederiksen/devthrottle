using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE MAIL DESK ROUTES (Mail Desk mission, issue #4213, phase 1 contract, sections 4 and 5). Every route is mapped into
/// <see cref="MailDeskGate.Group"/>, so for an account Mail Desk is not switched on for, each one answers 404 with no
/// body, exactly as if it were not mapped.
///
///   GET  /gateway/mail-desk/status               the switch, the rules version, every channel row, and today's counts
///   GET  /gateway/mail-desk/channels             the channel rows (?all=true also lists removed ones), and the Mail Desk
///                                                page's title, summary and empty sentence (phase 7)
///   POST /gateway/mail-desk/channels             add a channel: { address, provider, machines, why }
///   POST /gateway/mail-desk/channels/remove      remove one, keeping it and its decisions: { address, why }
///   POST /gateway/mail-desk/channels/mode        switch its mode: { address, mode, why }
///   GET  /gateway/mail-desk/rules                one version of the rules (?version=N, default the newest)
///   GET  /gateway/mail-desk/rules/history        the recorded changes, newest first (?n=, ?kind=)
///   POST /gateway/mail-desk/rules/{list}/add     add an entry to allow, block, rule or route, with why
///   POST /gateway/mail-desk/rules/{list}/remove  remove the entry with that key: { key, why }
///   GET  /gateway/mail-desk/decisions            the decisions, newest first (?since=, ?layer=, ?decision=, ?channel=, ?n=)
///   GET  /gateway/mail-desk/settings             the thresholds, the spend limit, what was spent, and whether it stopped
///   POST /gateway/mail-desk/settings             change one setting: { name, value, why } (phase 3, section 7)
///
/// WHO MAY DO WHAT (section 4). Every caller reads. A rules change needs an author - a person, or any session, a
/// factory's own included (D7: "freely, every change recorded"). A channel change - adding, removing, switching mode -
/// - and a settings change (phase 3) - is a person's, or a session in no factory's (the owner told it to); a factory's own session is refused 403
/// <c>FactorySessionRefused</c>. The account's machine token and a Director are refused every change: a change needs
/// an author. Who is asking is decided by <see cref="GatewayCaller"/>, the one place the factory definition routes
/// decide it too.
///
/// EVERY CHANGE CARRIES A WHY, required and not blank, or it is refused 400 <c>why_required</c>. NOTHING IS RECORDED ON
/// ANY REFUSAL: every check runs before the store is asked to write.
///
/// A RULES CHANGE IS ONE CHANGE, MADE AGAIN WHEN ANOTHER WRITER SAVED FIRST. The store refuses a save made on a version
/// that is no longer the newest (<see cref="MailDeskRulesOutcome.Stale"/>) and writes nothing; this route reads the
/// newest document again, makes its one change on that, checks it, and saves once more - at most
/// <see cref="RulesSaveAttempts"/> times, then 409 <c>rules_conflict</c>.
///
/// Every channel value a client shows is folded once, in <see cref="MailDeskChannelFold"/> (repository rule 7).
/// </summary>
internal static class MailDeskEndpoints
{
    /// <summary>How many times a rules change is made before the route gives up on a conflict.</summary>
    public const int RulesSaveAttempts = 3;

    /// <summary>The lists of the rules document a change names in its path, by the word the path uses.</summary>
    public static readonly IReadOnlyList<string> Lists = new[]
    {
        MailDeskWords.ChangeKinds.Allow, MailDeskWords.ChangeKinds.Block, MailDeskWords.ChangeKinds.Rule, MailDeskWords.ChangeKinds.Route,
    };

    /// <summary>The outcome word of a factory's own session refused a channel change.</summary>
    public const string FactorySessionRefusedOutcome = "FactorySessionRefused";

    /// <summary>The help lines <c>GET /status</c> ends with.</summary>
    public static readonly IReadOnlyList<string> StatusHelp = new[]
    {
        "cc-devthrottle maildesk channel list --all     every channel, removed ones too",
        "cc-devthrottle maildesk decisions -n 20        the newest decisions",
        "cc-devthrottle maildesk rules show             the rules every email is screened by",
        "cc-devthrottle maildesk rules history          who changed what, and why",
        "cc-devthrottle maildesk settings               the thresholds and the spend limit",
    };

    private const string Tag = nameof(MailDeskEndpoints);

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What the routes read and write. <see cref="KnownMachinesOf"/> is every machine the account's Directors have
    /// reported; when it holds none, a channel's machines cannot be checked and are taken as given.
    /// </summary>
    internal sealed record Parts(
        MailDeskChannelStore Channels,
        MailDeskRulesStore Rules,
        MailDeskDecisionStore Decisions,
        MailDeskSettingsStore Settings,
        MailDeskChannelFold Fold,
        Func<string, SessionFactoryLookup> SessionFactoryOf,
        Func<TenantId, IReadOnlyCollection<string>> KnownMachinesOf,
        Func<DateTime> NowUtc,
        Func<TenantId, MailDeskSpend> SpendOf)
    {
        /// <summary>Where a rules change reads the newest document from. The store, unless a test stands between
        /// the read and the save to play a second writer.</summary>
        internal Func<TenantId, MailDeskRulesVersion>? ReadNewestRules { get; init; }

        internal MailDeskRulesVersion NewestRules(TenantId tenant) => (ReadNewestRules ?? Rules.Newest)(tenant);
    }

    /// <summary>Map every Mail Desk route into <paramref name="gate"/>, the group <see cref="MailDeskGate.Group"/> made.</summary>
    public static void Map(RouteGroupBuilder gate, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(parts);

        const string p = MailDeskGate.Prefix;
        // Every handler takes a second parameter, so none can be read as a bare request delegate that drops its answer.
        gate.MapGet(p + "/status", (HttpContext ctx, CancellationToken _) => Status(ctx, resolveTenant, parts));
        gate.MapGet(p + "/channels", (HttpContext ctx, CancellationToken _) => ListChannels(ctx, resolveTenant, parts));
        gate.MapPost(p + "/channels", (HttpContext ctx, CancellationToken ct) => AddChannelAsync(ctx, resolveTenant, parts, ct));
        gate.MapPost(p + "/channels/remove", (HttpContext ctx, CancellationToken ct) => RemoveChannelAsync(ctx, resolveTenant, parts, ct));
        gate.MapPost(p + "/channels/mode", (HttpContext ctx, CancellationToken ct) => SetModeAsync(ctx, resolveTenant, parts, ct));
        gate.MapGet(p + "/rules", (HttpContext ctx, CancellationToken _) => ReadRules(ctx, resolveTenant, parts));
        gate.MapGet(p + "/rules/history", (HttpContext ctx, CancellationToken _) => History(ctx, resolveTenant, parts));
        gate.MapPost(p + "/rules/{list}/add", (HttpContext ctx, string list, CancellationToken ct) => AddEntryAsync(ctx, list, resolveTenant, parts, ct));
        gate.MapPost(p + "/rules/{list}/remove", (HttpContext ctx, string list, CancellationToken ct) => RemoveEntryAsync(ctx, list, resolveTenant, parts, ct));
        gate.MapGet(p + "/decisions", (HttpContext ctx, CancellationToken _) => ListDecisions(ctx, resolveTenant, parts));
        gate.MapGet(p + "/settings", (HttpContext ctx, CancellationToken _) => ReadSettings(ctx, resolveTenant, parts));
        gate.MapPost(p + "/settings", (HttpContext ctx, CancellationToken ct) => SetSettingAsync(ctx, resolveTenant, parts, ct));

        FileLog.Write($"[{Tag}] mapped {p}/status, channels (list, add, remove, mode), rules (read, history, add, remove), decisions and settings");
    }

    // ---------- reads ----------

    internal static IResult Status(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        var tenant = Tenant(ctx, resolveTenant);
        FileLog.Write($"[{Tag}] GET /status: account={tenant.ToLogString()}");
        var folded = parts.Fold.Fold(tenant, parts.Channels.List(tenant), parts.NowUtc());
        var rules = parts.Rules.Newest(tenant);
        var spend = Spend(tenant, parts);
        FileLog.Write($"[{Tag}] GET /status: {folded.Channels.Count} channel(s), rules version {rules.Version}, {folded.Today.Total} decision(s) today, spend stopped={spend.Stopped}");
        return Results.Json(new
        {
            enabled = true,
            rulesVersion = rules.Version,
            channels = folded.Channels,
            today = folded.Today,
            spend = SpendRow(spend),
            help = StatusHelp,
        });
    }

    internal static IResult ListChannels(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        const string route = "GET /channels";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryFlag(ctx, "all", route, out var all, out var bad)) return bad!;
        var folded = parts.Fold.Fold(tenant, parts.Channels.List(tenant, includeRemoved: all), parts.NowUtc());
        FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} all={all} count={folded.Channels.Count}");
        return Results.Json(new
        {
            count = folded.Channels.Count,
            channels = folded.Channels,
            title = MailDeskChannelFold.PageTitle,
            summary = MailDeskChannelFold.PageSummary,
            emptyLabel = MailDeskChannelFold.PageEmptyLabel,
        });
    }

    internal static IResult ReadRules(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        const string route = "GET /rules";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryWhole(ctx, "version", route, 0, int.MaxValue, out var asked, out var bad)) return bad!;
        var found = asked is { } version ? parts.Rules.Read(tenant, version) : parts.Rules.Newest(tenant);
        if (found is null)
            return Refuse(route, StatusCodes.Status404NotFound, "no_such_version",
                $"the rules have no version {asked}; 'maildesk rules history' shows the versions there are");
        FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} version={found.Version}");
        return Results.Json(new
        {
            version = found.Version,
            savedUtc = Utc(found.SavedUtc),
            savedByKind = found.SavedByKind,
            savedBy = found.SavedBy,
            document = JsonDocument.Parse(found.Document.ToJson()).RootElement,
        });
    }

    internal static IResult History(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        const string route = "GET /rules/history";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryWhole(ctx, "n", route, 1, MailDeskRulesStore.MaxHistory, out var n, out var bad)) return bad!;
        if (!TryWord(ctx, "kind", MailDeskWords.ChangeKinds.All, "a kind of change", route, out var kind, out bad)) return bad!;
        var history = parts.Rules.History(tenant, n ?? MailDeskRulesStore.DefaultHistory, kind);
        FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} kind={kind ?? "any"} count={history.Changes.Count} total={history.Total}");
        return Results.Json(new { count = history.Changes.Count, total = history.Total, changes = history.Changes.Select(ChangeRow).ToList() });
    }

    internal static IResult ListDecisions(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        const string route = "GET /decisions";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryWhole(ctx, "n", route, 1, MailDeskDecisionStore.MaxList, out var n, out var bad)) return bad!;
        if (!TryWord(ctx, "layer", MailDeskWords.Layers.All, "a Mail Desk layer", route, out var layer, out bad)) return bad!;
        if (!TryWord(ctx, "decision", MailDeskWords.Decisions.All, "a Mail Desk decision", route, out var decision, out bad)) return bad!;

        DateTime? since = null;
        if (Query(ctx, "since") is { } sinceText)
        {
            if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return Refuse(route, StatusCodes.Status400BadRequest, "bad_filter",
                    $"since '{sinceText}' is not a time; give an ISO time such as 2026-10-18T07:00:00Z");
            since = parsed.UtcDateTime;
        }
        string? channel = null;
        if (Query(ctx, "channel") is { } channelText)
        {
            if (!MailDeskAddresses.TryAddress(channelText, "channel", out var address, out var refusal))
                return Refuse(route, StatusCodes.Status400BadRequest, "bad_filter", refusal!);
            channel = address;
        }

        var list = parts.Decisions.List(tenant, new MailDeskDecisionFilter(since, layer, decision, channel, n ?? MailDeskDecisionStore.DefaultList));
        var addresses = parts.Channels.List(tenant, includeRemoved: true).ToDictionary(c => c.Id, c => c.Address);
        FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} count={list.Decisions.Count} total={list.Total}");
        return Results.Json(new
        {
            count = list.Decisions.Count,
            total = list.Total,
            byDecision = list.ByDecision.ByDecision,
            decisions = list.Decisions.Select(d => DecisionRow(d, addresses)).ToList(),
        });
    }

    // ---------- channel changes ----------

    internal static async Task<IResult> AddChannelAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts, CancellationToken ct)
    {
        const string route = "POST /channels";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryAuthor(ctx, parts, route, channelChange: true, out var who, out var error)) return error!;
        var (body, bad) = await ReadBodyAsync<AddChannelRequest>(ctx, route, ct);
        if (body is null) return bad!;
        if (WhyRefused(body.Why, route) is { } noWhy) return noWhy;

        if (MailDeskChannelStore.WhyNotMachines(body.Machines, out var machines) is { } machineRefusal)
            return Refuse(route, StatusCodes.Status400BadRequest, "bad_machines", machineRefusal);
        var known = parts.KnownMachinesOf(tenant);
        if (known.Count > 0)
        {
            for (var i = 0; i < machines.Count; i++)
            {
                var spelling = known.FirstOrDefault(k => string.Equals(k, machines[i], StringComparison.OrdinalIgnoreCase));
                if (spelling is null)
                    return Refuse(route, StatusCodes.Status400BadRequest, "unknown_machine",
                        $"machine '{machines[i]}' is not one this account's Directors have reported; the machines known are " +
                        string.Join(", ", known.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
                // The machine as its Director spells it, so the check's machine and the channel's agree exactly.
                machines[i] = spelling;
            }
        }

        var write = parts.Channels.Add(tenant, body.Address, body.Provider, machines, who, body.Why, parts.NowUtc());
        return ChannelAnswer(tenant, parts, route, write, StatusCodes.Status201Created);
    }

    internal static async Task<IResult> RemoveChannelAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts, CancellationToken ct)
    {
        const string route = "POST /channels/remove";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryAuthor(ctx, parts, route, channelChange: true, out var who, out var error)) return error!;
        var (body, bad) = await ReadBodyAsync<RemoveChannelRequest>(ctx, route, ct);
        if (body is null) return bad!;
        if (WhyRefused(body.Why, route) is { } noWhy) return noWhy;
        var write = parts.Channels.Remove(tenant, body.Address, who, body.Why, parts.NowUtc());
        return ChannelAnswer(tenant, parts, route, write, StatusCodes.Status200OK);
    }

    internal static async Task<IResult> SetModeAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts, CancellationToken ct)
    {
        const string route = "POST /channels/mode";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryAuthor(ctx, parts, route, channelChange: true, out var who, out var error)) return error!;
        var (body, bad) = await ReadBodyAsync<SetModeRequest>(ctx, route, ct);
        if (body is null) return bad!;
        if (WhyRefused(body.Why, route) is { } noWhy) return noWhy;
        var write = parts.Channels.SetMode(tenant, body.Address, body.Mode, who, body.Why, parts.NowUtc());
        return ChannelAnswer(tenant, parts, route, write, StatusCodes.Status200OK);
    }

    private static IResult ChannelAnswer(TenantId tenant, Parts parts, string route, MailDeskChannelWrite write, int successStatus)
    {
        if (write.Succeeded)
        {
            var row = parts.Fold.Fold(tenant, new[] { write.Channel! }, parts.NowUtc()).Channels[0];
            FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} {write.Outcome} channel {row.Id}");
            return Results.Json(row, statusCode: successStatus);
        }
        var (status, code) = write.Outcome switch
        {
            MailDeskChannelOutcome.BadAddress => (StatusCodes.Status400BadRequest, "bad_address"),
            MailDeskChannelOutcome.BadProvider => (StatusCodes.Status400BadRequest, "bad_provider"),
            MailDeskChannelOutcome.BadMachines => (StatusCodes.Status400BadRequest, "bad_machines"),
            MailDeskChannelOutcome.BadMode => (StatusCodes.Status400BadRequest, "bad_mode"),
            MailDeskChannelOutcome.AlreadyActive => (StatusCodes.Status409Conflict, "channel_exists"),
            MailDeskChannelOutcome.NoSuchChannel => (StatusCodes.Status404NotFound, "no_such_channel"),
            _ => throw new InvalidOperationException($"A refused channel write has the outcome {write.Outcome}, which is not a refusal."),
        };
        return Refuse(route, status, code, write.Refusal!);
    }

    // ---------- rules changes ----------

    internal static async Task<IResult> AddEntryAsync(HttpContext ctx, string list, Func<HttpContext, TenantId?> resolveTenant, Parts parts,
        CancellationToken ct)
    {
        var route = $"POST /rules/{list}/add";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryList(list, route, out var kind, out var noList)) return noList!;
        if (!TryAuthor(ctx, parts, route, channelChange: false, out var who, out var error)) return error!;
        var (body, bad) = await ReadBodyAsync<JsonObject>(ctx, route, ct);
        if (body is null) return bad!;
        var why = body["why"] is JsonValue w && w.TryGetValue<string>(out var whyText) ? whyText : null;
        if (WhyRefused(why, route) is { } noWhy) return noWhy;

        // A block entry's own why IS the change's why: one reason, said once. Every other entry has no why of its own.
        if (kind != MailDeskWords.ChangeKinds.Block) body.Remove("why");
        if (!TryEntry(kind, body, route, out var add, out bad)) return bad!;

        return SaveRulesChange(tenant, parts, route, document =>
        {
            var changed = add(document);
            var validation = MailDeskRulesValidator.Validate(changed);
            if (!validation.IsValid) return RulesStep.Invalid(validation);
            var (subject, detail) = Added(kind, validation.Document!);
            return RulesStep.Ready(validation.Document!, new MailDeskChange(kind, MailDeskWords.ChangeActions.Add, subject, detail, who, why));
        });
    }

    internal static async Task<IResult> RemoveEntryAsync(HttpContext ctx, string list, Func<HttpContext, TenantId?> resolveTenant, Parts parts,
        CancellationToken ct)
    {
        var route = $"POST /rules/{list}/remove";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryList(list, route, out var kind, out var noList)) return noList!;
        if (!TryAuthor(ctx, parts, route, channelChange: false, out var who, out var error)) return error!;
        var (body, bad) = await ReadBodyAsync<RemoveEntryRequest>(ctx, route, ct);
        if (body is null) return bad!;
        if (WhyRefused(body.Why, route) is { } noWhy) return noWhy;
        var key = (body.Key ?? "").Trim();
        if (key.Length == 0)
            return Refuse(route, StatusCodes.Status400BadRequest, "key_required",
                "key is needed: the pattern, the rule's name, or factory/seat of the entry to remove");

        return SaveRulesChange(tenant, parts, route, document =>
        {
            if (Removed(kind, document, key) is not { } removed) return RulesStep.NoSuchEntry(kind, key);
            var validation = MailDeskRulesValidator.Validate(removed.Document);
            if (!validation.IsValid) return RulesStep.Invalid(validation);
            return RulesStep.Ready(validation.Document!,
                new MailDeskChange(kind, MailDeskWords.ChangeActions.Remove, removed.Subject, removed.Detail, who, body.Why));
        });
    }

    /// <summary>One attempt at a rules change, made on one version of the document: ready to save, or why not.</summary>
    private sealed record RulesStep(MailDeskRulesDocument? Document, MailDeskChange? Change, IResult? Refusal)
    {
        public static RulesStep Ready(MailDeskRulesDocument document, MailDeskChange change) => new(document, change, null);

        public static RulesStep Invalid(MailDeskRulesValidation validation) => new(null, null, Results.Json(
            new { error = "invalid_rules", detail = validation.Refusal, problems = validation.Problems },
            statusCode: StatusCodes.Status400BadRequest));

        public static RulesStep NoSuchEntry(string kind, string key) => new(null, null, Results.Json(
            new { error = "no_such_entry", detail = $"the {kind} list has no entry '{key}'; 'maildesk rules show' lists every entry" },
            statusCode: StatusCodes.Status404NotFound));
    }

    // ---------- the settings (phase 3, section 7) ----------

    internal static IResult ReadSettings(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts)
    {
        var tenant = Tenant(ctx, resolveTenant);
        var spend = Spend(tenant, parts);
        FileLog.Write($"[{Tag}] GET /settings: account={tenant.ToLogString()} stopped={spend.Stopped}");
        return Results.Json(SpendRow(spend));
    }

    internal static async Task<IResult> SetSettingAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Parts parts, CancellationToken ct)
    {
        const string route = "POST /settings";
        var tenant = Tenant(ctx, resolveTenant);
        if (!TryAuthor(ctx, parts, route, channelChange: true, out var who, out var error, SettingFactorySessionDetail)) return error!;
        var (body, bad) = await ReadBodyAsync<SetSettingRequest>(ctx, route, ct);
        if (body is null) return bad!;
        if (WhyRefused(body.Why, route) is { } noWhy) return noWhy;
        if (body.Value is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDecimal(out var value))
            return Refuse(route, StatusCodes.Status400BadRequest, "bad_value",
                "'value' is needed as a number, such as 0.9 for a threshold or 10 for a spend limit in dollars");
        var write = parts.Settings.Set(tenant, body.Name, value, who, body.Why, parts.NowUtc());
        switch (write.Outcome)
        {
            case MailDeskSettingOutcome.UnknownName:
                return Refuse(route, StatusCodes.Status400BadRequest, "unknown_setting", write.Refusal!);
            case MailDeskSettingOutcome.OutOfRange:
                return Refuse(route, StatusCodes.Status400BadRequest, "bad_value", write.Refusal!);
            case MailDeskSettingOutcome.Set:
                var spend = Spend(tenant, parts);
                FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} set {write.Change!.Subject}; stopped={spend.Stopped}");
                return Results.Json(new { settings = SpendRow(spend), change = ChangeRow(write.Change) });
            default:
                throw new InvalidOperationException($"A settings change answered {write.Outcome}, which this route does not know.");
        }
    }

    /// <summary>The account's settings and spend, folded once (<see cref="MailDeskSpendView"/>).</summary>
    private static MailDeskSpendView Spend(TenantId tenant, Parts parts)
        => MailDeskSpendView.Fold(parts.Settings.Read(tenant), parts.SpendOf(tenant));

    private static object SpendRow(MailDeskSpendView spend) => new
    {
        routeThreshold = spend.RouteThreshold,
        spamThreshold = spend.SpamThreshold,
        spendLimitUsd = spend.SpendLimitUsd,
        spentUsd = spend.SpentUsd,
        stopped = spend.Stopped,
        label = spend.Label,
    };

    /// <summary>The detail a factory's own session gets when it tries to change a setting.</summary>
    private const string SettingFactorySessionDetail =
        "a session of a factory does not change the Mail Desk's thresholds or its spend limit; the owner does, or a session " +
        "in no factory the owner tells to";

    /// <summary>
    /// Make one rules change and save it: read the newest document, make the change on it, save it as made on that
    /// version. When another writer saved first, read again and make the change again - at most
    /// <see cref="RulesSaveAttempts"/> times.
    /// </summary>
    private static IResult SaveRulesChange(TenantId tenant, Parts parts, string route, Func<MailDeskRulesDocument, RulesStep> change)
    {
        for (var attempt = 1; attempt <= RulesSaveAttempts; attempt++)
        {
            var current = parts.NewestRules(tenant);
            var step = change(current.Document);
            if (step.Refusal is not null)
            {
                FileLog.Write($"[{Tag}] {route}: REFUSED on version {current.Version} - nothing recorded");
                return step.Refusal;
            }
            var write = parts.Rules.Save(tenant, current.Version, step.Document!, step.Change!, parts.NowUtc());
            switch (write.Outcome)
            {
                case MailDeskRulesOutcome.Saved:
                    FileLog.Write($"[{Tag}] {route}: account={tenant.ToLogString()} saved version {write.Version!.Version} on attempt {attempt}");
                    return Results.Json(new { version = write.Version.Version, change = ChangeRow(write.Change!) });
                case MailDeskRulesOutcome.Invalid:
                    return Results.Json(new { error = "invalid_rules", detail = write.Refusal, problems = write.Problems },
                        statusCode: StatusCodes.Status400BadRequest);
                case MailDeskRulesOutcome.Stale:
                    FileLog.Write($"[{Tag}] {route}: attempt {attempt} was made on version {current.Version}; another writer saved version {write.NewestVersion} first");
                    continue;
                default:
                    throw new InvalidOperationException($"A rules save answered {write.Outcome}, which this route does not know.");
            }
        }
        return Refuse(route, StatusCodes.Status409Conflict, "rules_conflict",
            $"the rules kept changing while this change was being made: it was made {RulesSaveAttempts} times, and each time " +
            "another writer had saved a newer version first. Nothing was saved; read the rules and send the change again");
    }

    private static bool TryList(string list, string route, out string kind, out IResult? error)
    {
        kind = (list ?? "").Trim().ToLowerInvariant();
        error = null;
        if (MailDeskWords.IsOneOf(kind, Lists)) return true;
        error = Refuse(route, StatusCodes.Status404NotFound, "no_such_list",
            $"'{list}' is not a list of the Mail Desk rules; it is one of {MailDeskWords.InWords(Lists)}");
        return false;
    }

    /// <summary>Read the entry of a <paramref name="kind"/> add from <paramref name="body"/>, as the change that appends it.
    /// A field the entry does not have is refused, so a misspelt field is never quietly dropped.</summary>
    private static bool TryEntry(string kind, JsonObject body, string route, out Func<MailDeskRulesDocument, MailDeskRulesDocument> add,
        out IResult? error)
    {
        add = d => d;
        error = null;
        var json = body.ToJsonString();
        try
        {
            switch (kind)
            {
                case MailDeskWords.ChangeKinds.Allow:
                    var allow = JsonSerializer.Deserialize<MailDeskAllowEntry>(json, MailDeskRulesDocument.Json)!;
                    add = d => d.WithAllow(allow);
                    return true;
                case MailDeskWords.ChangeKinds.Block:
                    var block = JsonSerializer.Deserialize<MailDeskBlockEntry>(json, MailDeskRulesDocument.Json)!;
                    add = d => d.WithBlock(block);
                    return true;
                case MailDeskWords.ChangeKinds.Rule:
                    var rule = JsonSerializer.Deserialize<MailDeskRule>(json, MailDeskRulesDocument.Json)!;
                    add = d => d.WithRule(rule);
                    return true;
                default:
                    var routeEntry = JsonSerializer.Deserialize<MailDeskRouteEntry>(json, MailDeskRulesDocument.Json)!;
                    add = d => d.WithRoute(routeEntry);
                    return true;
            }
        }
        catch (JsonException ex)
        {
            error = Refuse(route, StatusCodes.Status400BadRequest, "bad_entry",
                $"the {kind} entry cannot be read ({ex.Message}); send it as the rules document holds it, plus 'why'");
            return false;
        }
    }

    /// <summary>What an add appended, from the document in its one spelling: the entry's key, and the entry as JSON.</summary>
    private static (string Subject, string Detail) Added(string kind, MailDeskRulesDocument folded) => kind switch
    {
        MailDeskWords.ChangeKinds.Allow => (folded.Allow[^1].From, Json(folded.Allow[^1])),
        MailDeskWords.ChangeKinds.Block => (folded.Block[^1].From, Json(folded.Block[^1])),
        MailDeskWords.ChangeKinds.Rule => (folded.Rules[^1].Name, Json(folded.Rules[^1])),
        _ => (folded.Routes[^1].Key, Json(folded.Routes[^1])),
    };

    private sealed record Removal(MailDeskRulesDocument Document, string Subject, string Detail);

    /// <summary>The document without the <paramref name="kind"/> entry <paramref name="key"/> names, and that entry; null
    /// when there is none.</summary>
    private static Removal? Removed(string kind, MailDeskRulesDocument document, string key)
    {
        static bool Same(string? a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);
        switch (kind)
        {
            case MailDeskWords.ChangeKinds.Allow:
                var allow = document.Allow.FirstOrDefault(a => Same(a.From, key));
                return allow is null ? null : new Removal(document.WithoutAllow(key)!, allow.From, Json(allow));
            case MailDeskWords.ChangeKinds.Block:
                var block = document.Block.FirstOrDefault(b => Same(b.From, key));
                return block is null ? null : new Removal(document.WithoutBlock(key)!, block.From, Json(block));
            case MailDeskWords.ChangeKinds.Rule:
                var rule = document.Rules.FirstOrDefault(r => Same(r.Name, key));
                return rule is null ? null : new Removal(document.WithoutRule(key)!, rule.Name, Json(rule));
            default:
                var routeEntry = document.Routes.FirstOrDefault(r => Same(r.Key, key));
                return routeEntry is null ? null : new Removal(document.WithoutRoute(key)!, routeEntry.Key, Json(routeEntry));
        }
    }

    private static string Json<T>(T entry) => JsonSerializer.Serialize(entry, MailDeskRulesDocument.Json);

    // ---------- who is asking ----------

    /// <summary>
    /// MAY THIS CALLER MAKE THIS CHANGE, AND WHO IS IT (contract section 4). A person and a session may change the rules;
    /// a person and a session in no factory may change a channel. Neither a person nor a session may change nothing.
    /// </summary>
    private static bool TryAuthor(HttpContext ctx, Parts parts, string route, bool channelChange, out MailDeskWho who, out IResult? error,
        string? factorySessionDetail = null)
    {
        who = null!;
        if (!GatewayCaller.TryIdentify(ctx, parts.SessionFactoryOf, Tag, route, out var caller, out error)) return false;
        if (!caller.IsAnAuthor)
        {
            error = Refuse(route, StatusCodes.Status403Forbidden, "not_an_author",
                "a Mail Desk change is made by a person, or by a session: every change records who made it, and this " +
                "request carried neither a session's key nor a person's device key");
            return false;
        }
        if (channelChange && caller.Kind == FactoryMemoryAuthorKinds.Session && caller.Factory is not null)
        {
            FileLog.Write($"[{Tag}] {route}: REFUSED (403, {FactorySessionRefusedOutcome}) - session {caller.Id} is a session of factory {caller.Factory}");
            error = Results.Json(new
            {
                error = "factory_session_refused",
                outcome = FactorySessionRefusedOutcome,
                detail = factorySessionDetail is not null
                    ? $"{factorySessionDetail} ('{caller.Factory}' is this session's factory)"
                    : $"a session of the factory '{caller.Factory}' does not add or remove a Mail Desk channel or switch its " +
                      "mode; the owner does, or a session in no factory the owner tells to. A factory's session may change " +
                      "the rules: allow, block, rule and route",
            }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        who = caller.Kind == FactoryMemoryAuthorKinds.Person ? MailDeskWho.Person(caller.Id!) : MailDeskWho.Session(caller.Id!, caller.Name);
        return true;
    }

    /// <summary>The 400 a change without a usable why gets, or null when the why will do.</summary>
    private static IResult? WhyRefused(string? why, string route)
    {
        if (MailDeskWho.WhyRefusal(why) is not { } refusal) return null;
        return Refuse(route, StatusCodes.Status400BadRequest, string.IsNullOrWhiteSpace(why) ? "why_required" : "why_too_long", refusal);
    }

    // ---------- rows ----------

    private static object ChangeRow(MailDeskListChangeEntity c) => new
    {
        id = c.Id,
        atUtc = Utc(c.AtUtc),
        kind = c.Kind,
        action = c.Action,
        subject = c.Subject,
        detail = JsonDocument.Parse(c.Detail).RootElement,
        versionBefore = c.VersionBefore,
        versionAfter = c.VersionAfter,
        who = new { kind = c.WhoKind, id = c.WhoId, name = c.WhoName },
        whoInWords = FactoryPublisher.Words(c.WhoKind, c.WhoId, c.WhoName),
        why = c.Why,
    };

    private static object DecisionRow(MailDeskDecisionEntity d, IReadOnlyDictionary<Guid, string> addresses) => new
    {
        id = d.Id,
        channel = addresses.TryGetValue(d.ChannelId, out var address)
            ? address
            : throw new InvalidOperationException($"Mail Desk decision {d.Id} names channel {d.ChannelId}, which this account does not have."),
        messageKey = d.MessageKey,
        receivedUtc = Utc(d.ReceivedUtc),
        sender = d.SenderAddress,
        subject = d.Subject,
        layer = d.Layer,
        decision = d.Decision,
        route = d.RouteFactory is null ? null : new { factory = d.RouteFactory, seat = d.RouteSeat },
        reason = d.Reason,
        confidence = d.Confidence,
        costUsd = d.CostMicros is { } micros ? micros / 1_000_000m : (decimal?)null,
        mode = d.Mode,
        outcome = d.Outcome,
        outcomeDetail = d.OutcomeDetail,
        decidedUtc = Utc(d.DecidedUtc),
        rulesVersion = d.RulesVersion,
        attemptCount = d.AttemptCount,
    };

    internal static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    // ---------- requests ----------

    internal sealed record AddChannelRequest(string? Address, string? Provider, List<string>? Machines, string? Why);

    internal sealed record RemoveChannelRequest(string? Address, string? Why);

    internal sealed record SetModeRequest(string? Address, string? Mode, string? Why);

    internal sealed record RemoveEntryRequest(string? Key, string? Why);

    internal sealed record SetSettingRequest(string? Name, JsonElement? Value, string? Why);

    private static async Task<(T? Body, IResult? Error)> ReadBodyAsync<T>(HttpContext ctx, string route, CancellationToken ct) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, BodyJson, ct);
            if (body is not null) return (body, null);
        }
        catch (JsonException ex)
        {
            return (null, Refuse(route, StatusCodes.Status400BadRequest, "bad_request",
                $"the request body cannot be read as JSON ({ex.Message}); send one JSON object"));
        }
        return (null, Refuse(route, StatusCodes.Status400BadRequest, "bad_request", "the request has no body; send one JSON object"));
    }

    // ---------- the query ----------

    private static string? Query(HttpContext ctx, string name)
    {
        var value = ctx.Request.Query[name].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryFlag(HttpContext ctx, string name, string route, out bool value, out IResult? error)
    {
        error = null;
        value = false;
        if (Query(ctx, name) is not { } text) return true;
        if (bool.TryParse(text, out value)) return true;
        error = Refuse(route, StatusCodes.Status400BadRequest, "bad_filter", $"{name} is true or false, not '{text}'");
        return false;
    }

    private static bool TryWhole(HttpContext ctx, string name, string route, int min, int max, out int? value, out IResult? error)
    {
        error = null;
        value = null;
        if (Query(ctx, name) is not { } text) return true;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max)
        {
            value = n;
            return true;
        }
        error = Refuse(route, StatusCodes.Status400BadRequest, "bad_filter",
            max == int.MaxValue ? $"{name} is a whole number from {min}, not '{text}'" : $"{name} is a whole number from {min} to {max}, not '{text}'");
        return false;
    }

    private static bool TryWord(HttpContext ctx, string name, IReadOnlyList<string> words, string what, string route, out string? value,
        out IResult? error)
    {
        error = null;
        value = Query(ctx, name);
        if (value is null || MailDeskWords.IsOneOf(value, words)) return true;
        error = Refuse(route, StatusCodes.Status400BadRequest, "bad_filter",
            $"{name} '{value}' is not {what}; it is one of {MailDeskWords.InWords(words)}");
        value = null;
        return false;
    }

    // ---------- answers ----------

    /// <summary>The account the gate let through. The gate refuses a request bound to no account before any handler
    /// runs, so none here is a wiring defect.</summary>
    private static TenantId Tenant(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant)
        => resolveTenant(ctx) ?? throw new InvalidOperationException("A Mail Desk route was reached by a request bound to no account; the gate should have refused it.");

    private static IResult Refuse(string route, int status, string code, string detail)
    {
        // The detail is not logged: it can quote an address, and an address is the account's, not the log's.
        FileLog.Write($"[{Tag}] {route}: REFUSED ({status}, {code}) - nothing recorded");
        return Results.Json(new { error = code, detail }, statusCode: status);
    }
}
