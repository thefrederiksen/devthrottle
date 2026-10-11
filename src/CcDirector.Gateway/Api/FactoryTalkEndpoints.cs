using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
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
/// open and this factory's. A factory with no boss is refused with the hand-over's own sentence, until the Factory
/// Manager exists (step 5).
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
    public static void Map(IEndpointRouteBuilder app, Factory.FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoryRegistryStore registry,
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors, Func<TenantId, string, CronJobDto?> schedule,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector,
        Func<TenantId, string, CronRunProblemsDto> problems, Func<TenantId, TimeZoneInfo> timeZone)
    {
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
            return await StartAsync(ctx, tenant, registered, seated, listDirectors, startOnDirector,
                FactoryTalkSeed.SessionName(registered, seated),
                FactoryTalkSeed.Compose(registered, seated, scheduleId, scheduleSeed));
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
            if (!handoff.Available)
            {
                FileLog.Write($"[FactoryTalkEndpoints] REFUSED handle the issue: {registered.Factory} has nobody to hand it to");
                return Refused(StatusCodes.Status409Conflict, handoff.UnavailableText!);
            }

            var seated = handoff.Seat!;
            var (scheduleId, scheduleSeed) = RulesOf(tenant, seated, schedule);
            return await StartAsync(ctx, tenant, registered, seated, listDirectors, startOnDirector,
                FactoryTalkSeed.ProblemSessionName(registered, seated, problem),
                FactoryTalkSeed.ComposeForProblem(registered, seated, scheduleId, scheduleSeed, problem, timeZone(tenant)));
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
    private static async Task<IResult> StartAsync(HttpContext ctx, TenantId tenant, RegisteredFactoryDto registered,
        RegisteredFactorySeatDto seated, Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector, string name, string prePrompt)
    {
        // The first Director that is running on the seat's computer, exactly as a schedule's run picks one -
        // and never one on another computer.
        var director = listDirectors(tenant).FirstOrDefault(d =>
            d.StoppedAtUtc is null && string.Equals(d.MachineName, seated.Computer, StringComparison.OrdinalIgnoreCase));
        if (director is null)
        {
            FileLog.Write($"[FactoryTalkEndpoints] REFUSED: no Director running on {seated.Computer} for {registered.Factory}/{seated.Id}");
            return Refused(StatusCodes.Status409Conflict,
                $"No Director is running on {seated.Computer}, the computer {seated.Name} runs on, so the talk was not started. Start the Director on {seated.Computer} and press Talk again.");
        }

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

        var outcome = await startOnDirector(ctx, director.DirectorId, request);
        if (outcome.Session is not { } started)
        {
            FileLog.Write($"[FactoryTalkEndpoints] the create on {director.DirectorId} was refused for {registered.Factory}/{seated.Id}");
            return outcome.Refusal!;
        }

        FileLog.Write($"[FactoryTalkEndpoints] started talk sid={started.SessionId} for {registered.Factory}/{seated.Id} on {director.DirectorId} ({seated.Computer})");
        return Results.Json(new FactoryTalkStartedDto
        {
            SessionId = started.SessionId,
            SessionName = request.Name,
            Href = "/session/" + Uri.EscapeDataString(started.SessionId),
            Factory = registered.Factory,
            Seat = seated.Id,
            Computer = seated.Computer,
            DirectorId = director.DirectorId,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);
}
