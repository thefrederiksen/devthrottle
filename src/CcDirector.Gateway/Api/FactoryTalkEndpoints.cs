using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Factories screen's Talk button (Factories screen mission, phase C):
///
///   POST /gateway/factory-agents/factories/{factory}/seats/{seat}/talk  -> 201 FactoryTalkStartedDto | 400 | 403 | 404 | 409 | 502
///
/// It starts a NEW TOP-LEVEL SESSION OWNED BY THE PERSON WHO PRESSED IT, seated as that seat: on the seat's computer,
/// in the factory's folder, in the factory (so its rules and memory load as they do for its scheduled runs), named
/// <c>&lt;Factory&gt; - &lt;Seat&gt; - talk with the owner</c>, with <see cref="FactoryTalkSeed"/> as its first prompt.
///
/// The create goes through <see cref="GatewayEndpoints.StartSessionOnDirectorAsync"/> - the very path a person's New
/// Session takes - so who owns it is settled from the caller's credential there and nowhere else: a person's device
/// key makes it the person's, with no parent and no controller. This route is under <c>/gateway/factory-agents</c>,
/// the owner's views, which a session's key never reaches (SessionKeyGuard), so a session cannot start a talk.
///
/// ONLY THE OWNER'S OWN BROWSER OR PHONE (phase C review, finding 1). The seed tells the agent the owner is present and
/// may approve anything, the goal included, so the route first proves that claim: a session key, a Director's device
/// key and the shared machine token are all refused before a factory is read or a create is sent.
///
/// THE SWITCH, WITH A SENTENCE (phase C review, finding 2). Unlike the read routes, this one is not mapped into
/// <see cref="FactoryAgentsGate"/>'s group, whose 404 has no body: a button the Cockpit drew before the switch went off
/// must be able to say why nothing happened. It asks the gate's own question, and only after the owner check, so the
/// sentence reaches nobody but the owner of the account.
///
/// NO OTHER COMPUTER. A seat runs on one computer; when no Director is running there the answer is 409 saying so,
/// never a session somewhere else - a talk on the wrong computer would read the wrong folder.
///
/// HANDLE THE ISSUE (Factory Control, step 1b):
///
///   POST /gateway/factory-agents/factories/{factory}/problems/{runId}/handle -> 201 FactoryTalkStartedDto | 403 | 404 | 409 | 502
///
/// The same talk, under every rule above, opened with whoever <see cref="FactoryProblemHandoff"/> names - the boss -
/// and seeded with <see cref="FactoryTalkSeed.ComposeForProblem"/>: the problem, which session to read, and to resolve
/// it with <c>cc-devthrottle run resolve</c> once fixed. The problem is read from the one problems fold and must be
/// open and this factory's. A factory with no boss hands it to the Factory Manager (step 5): a new Factory Manager
/// talk, briefed in the same words, which becomes the account's bound Factory Manager.
///
/// A DEFINED FACTORY'S TALK (Factory Definition mission, phase 5). For a factory with a published definition both talks
/// are built from that definition by <see cref="FactorySeatStart"/> - the seat's agent, model, machine and repository
/// name, a seat kit, and no folder - and marked as the Gateway's own seat start, so the seat fields pass the person's
/// door (<see cref="SpawnFactory.MarkGatewaySeatStart"/>). The body may carry the person's override of the agent, the
/// model and the machine (<see cref="FactoryTalkOverrideDto"/>); it is recorded in words on the session's history row,
/// and an agent that cannot take the seat's rules or load its skills is refused before anything is sent. A factory
/// with no published definition takes exactly the path it took before, and an override for one is refused: it has no
/// definition for the override to depart from.
/// </summary>
internal static class FactoryTalkEndpoints
{
    public const string Route = FactoryAgentsViewEndpoints.Prefix + "/factories/{factory}/seats/{seat}/talk";
    public const string HandleRoute = FactoryAgentsViewEndpoints.Prefix + "/factories/{factory}/problems/{runId}/handle";

    /// <param name="listDirectors">The account's registered Directors.</param>
    /// <param name="schedule">One schedule of the account by id, or null when there is none.</param>
    /// <param name="startOnDirector">Start a session on one Director, as a person's New Session does.</param>
    /// <param name="problems">One factory's open problems, from the one problems fold (<c>GET /cron/problems</c>).</param>
    /// <param name="timeZone">The account's time zone, the one the problems' shifts are counted in.</param>
    /// <param name="factoryManager">The Factory Manager's facts (Factory Control, step 5): a factory with no boss hands
    /// Handle the issue to it. REQUIRED, so a wiring line that forgot it fails to compile rather than refusing every
    /// boss-less factory.</param>
    /// <param name="publishedBrief">Factory Seats mission, phase 2c: the PUBLISHED brief the Gateway holds for one seat
    /// of one factory, or null when it holds none - then the talk is told the path of the seat's brief file, as
    /// before. Never a draft. REQUIRED, so a wiring line that forgot it fails to compile rather than starting every
    /// talk without its brief.</param>
    /// <param name="definitionOf">Factory Definition mission, phase 5: one factory's PUBLISHED definition in one account
    /// (<see cref="FactorySeatStart.ReadPublished"/>), or null when it is not defined. REQUIRED, so a wiring line that
    /// forgot it fails to compile rather than starting every defined seat's talk in a folder.</param>
    public static void Map(IEndpointRouteBuilder app, Factory.FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoryRegistryStore registry,
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors, Func<TenantId, string, CronJobDto?> schedule,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector,
        Func<TenantId, string, CronRunProblemsDto> problems, Func<TenantId, TimeZoneInfo> timeZone,
        FactoryManagerSources factoryManager, Func<TenantId, string, string, FactoryGivenBrief?> publishedBrief,
        Func<TenantId, string, PublishedFactoryDefinition?> definitionOf)
    {
        ArgumentNullException.ThrowIfNull(definitionOf);
        ArgumentNullException.ThrowIfNull(publishedBrief);
        ArgumentNullException.ThrowIfNull(factoryManager);
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(listDirectors);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(startOnDirector);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(timeZone);

        app.MapPost(Route, async (HttpContext ctx, string factory, string seat) =>
        {
            FileLog.Write($"[FactoryTalkEndpoints] POST talk: factory={factory}, seat={seat}");
            if (Admit(ctx, factorySwitch, resolveTenant, registry, factory, out var tenant, out var registered) is { } refused)
                return refused;

            var seated = registered!.Seats.FirstOrDefault(s => string.Equals(s.Id, (seat ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (seated is null)
                return Refused(StatusCodes.Status404NotFound,
                    $"{registered.Title} has no seat '{seat}'. Its seats are: {string.Join(", ", registered.Seats.Select(s => s.Id))}.");

            var (scheduleId, scheduleSeed) = RulesOf(tenant, seated, schedule);
            var (byPerson, badBody) = await ReadOverrideAsync(ctx);
            if (badBody is not null) return badBody;
            if (!TryDefinition(definitionOf, tenant, registered, out var published, out var unread)) return unread!;
            if (published is not null)
            {
                var given = published.Seat(seated.Id) is { } defined ? published.BriefOf(defined) : null;
                return await StartDefinedAsync(ctx, tenant, registered, seated, published, byPerson, listDirectors, startOnDirector,
                    FactoryTalkSeed.SessionName(registered, seated),
                    given is null ? null : FactoryTalkSeed.Compose(registered, seated, scheduleId, scheduleSeed, given), given);
            }
            if (byPerson is not null) return OverrideOfAnUndefinedFactory(registered);

            // Read once: the version the talk is given is the version it is told to record and the answer carries.
            if (!TryBrief(publishedBrief, tenant, registered, seated, out var brief, out var noBrief)) return noBrief!;
            return await StartAsync(ctx, tenant, registered, seated, listDirectors, startOnDirector,
                FactoryTalkSeed.SessionName(registered, seated),
                FactoryTalkSeed.Compose(registered, seated, scheduleId, scheduleSeed, brief), brief);
        });

        app.MapPost(HandleRoute, async (HttpContext ctx, string factory, string runId) =>
        {
            FileLog.Write($"[FactoryTalkEndpoints] POST handle the issue: factory={factory}, run={runId}");
            if (Admit(ctx, factorySwitch, resolveTenant, registry, factory, out var tenant, out var registered) is { } refused)
                return refused;

            var problem = problems(tenant, registered!.Factory).Problems.FirstOrDefault(p =>
                p.State == CronRunProblemStates.Open
                && string.Equals(p.RunId, (runId ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Factory, registered.Factory, StringComparison.OrdinalIgnoreCase));
            if (problem is null)
                return Refused(StatusCodes.Status404NotFound,
                    $"{registered.Title} has no open problem on run '{runId}'. It may have been resolved, or cleared by a later run that reported ok - reload the page.");

            var handoff = FactoryProblemHandoff.Route(registered);
            if (handoff.ToFactoryManager)
            {
                // A factory with no boss: the Factory Manager, briefed in the boss's words (Factory Control, step 5).
                FileLog.Write($"[FactoryTalkEndpoints] handle the issue: {registered.Factory} has no boss - handing it to the Factory Manager");
                return await FactoryManagerEndpoints.StartAsync(ctx, tenant, factoryManager, body: null,
                    FactoryManagerSeed.ProblemSessionName(registered, problem),
                    FactoryManagerSeed.ComposeForProblem(FactoryManagerEndpoints.Live(factoryManager.Factories(tenant)),
                        registered, problem, timeZone(tenant)));
            }

            var seated = handoff.Seat!;
            var (scheduleId, scheduleSeed) = RulesOf(tenant, seated, schedule);
            var (byPerson, badBody) = await ReadOverrideAsync(ctx);
            if (badBody is not null) return badBody;
            if (!TryDefinition(definitionOf, tenant, registered, out var published, out var unread)) return unread!;
            if (published is not null)
            {
                var given = published.Seat(seated.Id) is { } defined ? published.BriefOf(defined) : null;
                return await StartDefinedAsync(ctx, tenant, registered, seated, published, byPerson, listDirectors, startOnDirector,
                    FactoryTalkSeed.ProblemSessionName(registered, seated, problem),
                    given is null ? null : FactoryTalkSeed.ComposeForProblem(registered, seated, scheduleId, scheduleSeed, problem, timeZone(tenant), given),
                    given);
            }
            if (byPerson is not null) return OverrideOfAnUndefinedFactory(registered);

            if (!TryBrief(publishedBrief, tenant, registered, seated, out var brief, out var noBrief)) return noBrief!;
            return await StartAsync(ctx, tenant, registered, seated, listDirectors, startOnDirector,
                FactoryTalkSeed.ProblemSessionName(registered, seated, problem),
                FactoryTalkSeed.ComposeForProblem(registered, seated, scheduleId, scheduleSeed, problem, timeZone(tenant), brief), brief);
        });

        FileLog.Write($"[FactoryTalkEndpoints] mapped {Route} and {HandleRoute}");
    }

    /// <summary>
    /// The checks every talk passes first, in this order: the owner's own browser or phone, the factory switch, an
    /// account, a registered factory. Null when it may go on; otherwise the refusal.
    /// </summary>
    private static IResult? Admit(HttpContext ctx, Factory.FactoryAgentsSwitch factorySwitch, Func<HttpContext, TenantId?> resolveTenant,
        FactoryRegistryStore registry, string factory, out TenantId tenant, out RegisteredFactoryDto? registered)
    {
        tenant = default;
        registered = null;
        if (FleetManagerOwnerDevice.Require(ctx, "start a talk with a factory agent",
                "a session may not start a talk with a factory agent; a talk is the owner's own session, opened from the owner's phone or browser",
                nameof(FactoryTalkEndpoints), out var notOwner) is null)
            return notOwner!;
        if (!FactoryAgentsGate.IsOnFor(ctx, factorySwitch, resolveTenant, out var why))
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: {why}");
            return Refused(StatusCodes.Status404NotFound,
                "Factory agents are switched off for this account, so no talk was started. The administrator switches them on per account.");
        }
        if (resolveTenant(ctx) is not { } resolved)
            return Refused(StatusCodes.Status403Forbidden, "No account is bound to this request.");
        tenant = resolved;

        registered = registry.Find(tenant, factory);
        if (registered is null)
            return Refused(StatusCodes.Status404NotFound,
                $"No factory '{factory}' is registered in this account, so there is nobody to talk to. Register it with cc-devthrottle factory register.");
        return null;
    }

    /// <summary>The seat's first schedule and its seed: the rules of its scheduled runs, which a talk runs under.</summary>
    private static (string? ScheduleId, string? ScheduleSeed) RulesOf(TenantId tenant, RegisteredFactorySeatDto seated,
        Func<TenantId, string, CronJobDto?> schedule)
    {
        var scheduleId = seated.Schedules.FirstOrDefault();
        return (scheduleId, scheduleId is null ? null : schedule(tenant, scheduleId)?.Action?.Seed);
    }

    /// <summary>Start the talk on the seat's own computer, as the person's own top-level session.</summary>
    private static Task<IResult> StartAsync(HttpContext ctx, TenantId tenant, RegisteredFactoryDto registered,
        RegisteredFactorySeatDto seated, Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector, string name, string prePrompt,
        FactoryGivenBrief? brief)
    {
        var request = new NewSessionRequest
        {
            RepoPath = registered.Folder,
            Agent = "ClaudeCode",
            Factory = registered.Factory,
            Name = name,
            PrePrompt = prePrompt,
            // The session is the person's: it answers to nobody but them.
            ControllerSessionId = null,
            ParentSessionId = null,
        };
        return SendAsync(ctx, tenant, registered, seated, seated.Computer, $"the computer {seated.Name} runs on",
            request, listDirectors, startOnDirector, brief);
    }

    /// <summary>
    /// A DEFINED FACTORY'S TALK (Factory Definition mission, phase 5): the seat as the published definition holds it,
    /// built by <see cref="FactorySeatStart"/>, with the person's override if the body carried one - refused first when
    /// its agent cannot take the seat's rules or load its skills - and marked as the Gateway's own seat start.
    /// <paramref name="prePrompt"/> and <paramref name="brief"/> are null when the version holds no such seat.
    /// </summary>
    private static Task<IResult> StartDefinedAsync(HttpContext ctx, TenantId tenant, RegisteredFactoryDto registered,
        RegisteredFactorySeatDto seated, PublishedFactoryDefinition published, FactorySeatStartOverride? byPerson,
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector, string name, string? prePrompt,
        FactoryGivenBrief? brief)
    {
        if (published.Seat(seated.Id) is not { } seat || prePrompt is null || brief is null)
        {
            var notHeld = FactorySeatStart.SeatNotInVersion(published, seated.Id);
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: {notHeld}");
            return Task.FromResult(Refused(StatusCodes.Status409Conflict, $"The talk was not started: {notHeld}"));
        }
        if (FactorySeatStart.RefuseOverride(seat, byPerson) is { } cannot)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED the override {byPerson!.Words()} for {registered.Factory}/{seat.Seat}: {cannot}");
            return Task.FromResult(Refused(StatusCodes.Status409Conflict, $"The talk was not started: {cannot}"));
        }

        var built = FactorySeatStart.Build(published, seat, name, prePrompt, byPerson);
        // The session is the person's: it answers to nobody but them.
        built.Request.ControllerSessionId = null;
        built.Request.ParentSessionId = null;
        // The seat fields were set HERE, by the Gateway, from the published definition: mark the request so they pass
        // the person's door, which refuses a seat a person names by hand.
        SpawnFactory.MarkGatewaySeatStart(ctx);
        var whose = string.IsNullOrWhiteSpace(byPerson?.Machine) ? $"the computer {seated.Name} runs on" : "the computer chosen for this talk";
        return SendAsync(ctx, tenant, registered, seated, built.Machine, whose, built.Request, listDirectors, startOnDirector, brief);
    }

    /// <summary>Send the talk's create to the first Director running on <paramref name="machine"/>, and answer.</summary>
    private static async Task<IResult> SendAsync(HttpContext ctx, TenantId tenant, RegisteredFactoryDto registered,
        RegisteredFactorySeatDto seated, string machine, string whose, NewSessionRequest request,
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector, FactoryGivenBrief? brief)
    {
        // The first Director that is running on the seat's computer, exactly as a schedule's run picks one -
        // and never one on another computer.
        var director = listDirectors(tenant).FirstOrDefault(d =>
            d.StoppedAtUtc is null && string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase));
        if (director is null)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: no Director running on {machine} for {registered.Factory}/{seated.Id}");
            return Refused(StatusCodes.Status409Conflict,
                $"No Director is running on {machine}, {whose}, so the talk was not started. Start the Director on {machine} and press Talk again.");
        }

        var outcome = await startOnDirector(ctx, director.DirectorId, request);
        if (outcome.Session is not { } started)
        {
            FileLog.Write($"[FactoryTalkEndpoints] the create on {director.DirectorId} was refused for {registered.Factory}/{seated.Id}");
            return outcome.Refusal!;
        }

        FileLog.Write($"[FactoryTalkEndpoints] started talk sid={started.SessionId} for {registered.Factory}/{seated.Id} on {director.DirectorId} ({machine}), "
                      + (brief is null ? "brief=none given (told the path of its file)" : $"given brief {brief.VersionWords}"));
        return Results.Json(new FactoryTalkStartedDto
        {
            SessionId = started.SessionId,
            SessionName = request.Name!,
            Href = "/session/" + Uri.EscapeDataString(started.SessionId),
            Factory = registered.Factory,
            Seat = seated.Id,
            Computer = machine,
            DirectorId = director.DirectorId,
            BriefVersion = brief?.Version,
            BriefVersionKind = brief?.Kind,
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// The brief the talk is given, read once. A DEFINED factory whose published definition holds no file for the seat
    /// cannot give it one, and its talk is never told the path of a file instead (Factory Definition, phase 4): the
    /// read refuses in words, and the talk is not started.
    /// </summary>
    private static bool TryBrief(Func<TenantId, string, string, FactoryGivenBrief?> publishedBrief, TenantId tenant,
        RegisteredFactoryDto registered, RegisteredFactorySeatDto seated, out FactoryGivenBrief? brief, out IResult? refused)
    {
        refused = null;
        try
        {
            brief = publishedBrief(tenant, registered.Factory, seated.Id);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: the brief of {registered.Factory}/{seated.Id} could not be given: {ex.Message}");
            brief = null;
            refused = Refused(StatusCodes.Status409Conflict, $"The talk was not started: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The published definition the talk is built from, read once, or null for a factory that is not defined. A
    /// published version that cannot be read refuses the talk in words: it is never started from a folder instead.
    /// </summary>
    private static bool TryDefinition(Func<TenantId, string, PublishedFactoryDefinition?> definitionOf, TenantId tenant,
        RegisteredFactoryDto registered, out PublishedFactoryDefinition? published, out IResult? refused)
    {
        refused = null;
        try
        {
            published = definitionOf(tenant, registered.Factory);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: the definition of {registered.Factory} could not be read: {ex.Message}");
            published = null;
            refused = Refused(StatusCodes.Status409Conflict, $"The talk was not started: {ex.Message}.");
            return false;
        }
    }

    /// <summary>
    /// The person's override of the talk's agent, model and machine, from the body, or null when there is no body or it
    /// chooses nothing (Factory Definition mission, phase 5). A body that is not the override's JSON is refused.
    /// </summary>
    private static async Task<(FactorySeatStartOverride? Override, IResult? Refusal)> ReadOverrideAsync(HttpContext ctx)
    {
        string text;
        using (var reader = new StreamReader(ctx.Request.Body))
            text = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        FactoryTalkOverrideDto? body;
        try
        {
            body = JsonSerializer.Deserialize<FactoryTalkOverrideDto>(text, BodyJson);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: the talk's body is not the override's JSON: {ex.Message}");
            return (null, Refused(StatusCodes.Status400BadRequest,
                "The talk's body is not JSON the talk reads. Send nothing, or an object with agent, model and machine to choose them for this talk."));
        }
        var chosen = FactorySeatStart.Normalize(body is null ? null : new FactorySeatStartOverride(body.Agent, body.Model, body.Machine));
        if (chosen is not null) FileLog.Write($"[FactoryTalkEndpoints] the person chose: {chosen.Words()}");
        return (chosen, null);
    }

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    /// <summary>The refusal for an override of a factory with no published definition: there is no seat definition for
    /// it to depart from, and an undefined factory's talk is started exactly as it always was.</summary>
    private static IResult OverrideOfAnUndefinedFactory(RegisteredFactoryDto registered)
    {
        FileLog.Write($"[FactoryTalkEndpoints] REFUSED: an override on {registered.Factory}, which has no published definition");
        return Refused(StatusCodes.Status400BadRequest,
            $"{registered.Title} has no published definition, so a talk with it runs as it is registered: an agent, a model or a machine can be chosen only for a seat of a defined factory. Press Talk without choosing them.");
    }

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);
}
