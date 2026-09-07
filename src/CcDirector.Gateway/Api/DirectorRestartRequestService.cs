using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>An HTTP-shaped answer: the status to send and the body to serialise. Kept free of ASP.NET so
/// the whole decision is testable without a server.</summary>
public sealed record RestartRequestAnswer(int Status, object Body);

/// <summary>
/// THE DECISIONS behind the restart-request routes - issue #2725 (restart epic, Phase 6). A session ASKS,
/// this scrutinises, the owner ACCEPTS once, and this hands the cycle to the Director.
///
/// WHY IT IS A CLASS WITH DELEGATES AND NOT LOGIC INSIDE THE ROUTE LAMBDAS. The accept dispatches a
/// command down a Director's stream and closes the request when that fails, and the create consults
/// three registries and refuses in five different ways. Every one of those refusals is the feature - the
/// owner is never shown an approval for a restart that cannot work - so every one of them needs a test
/// that can fail, and a route lambda can only be tested through a booted host. The facts arrive as
/// delegates; the rulings are here; the routes render them.
///
/// NOTHING HERE WIDENS THE ADMISSION SURFACE. A request restarts nothing and grants nothing: it is a
/// pending record. The accept is a human action on the admission-scoped surface, and
/// <see cref="Util.SessionKeyGuard"/> refuses it to a session key exactly as it refuses the direct
/// restart. The cycle the accept starts runs on the DIRECTOR, which asks its OWN launcher on its own
/// credential.
/// </summary>
public sealed class DirectorRestartRequestService
{
    /// <summary>The verb the accept sends down the Director's stream.</summary>
    public const string CycleVerb = DirectorRestartVerbs.Cycle;

    /// <summary>The verb by which a Director is asked, before the owner is, whether it is the Director its
    /// launcher would restart. Read-only on the Director.</summary>
    public const string EligibilityVerb = DirectorRestartVerbs.Eligibility;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DirectorRestartRequestStore _store;
    private readonly Func<TenantId, IReadOnlyCollection<DirectorDto>> _listDirectors;
    private readonly Func<TenantId, string, LauncherDto?> _launcherRegistration;
    private readonly Func<TenantId, string, LauncherStreamConnection?>? _launcherConnection;
    private readonly Func<TenantId, string, PushedSessionStore.DirectorKnowledge> _directorSessions;
    private readonly Func<TenantId, string, SessionDto?> _findSession;
    private readonly Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> _sendCommand;

    /// <param name="store">The pending records.</param>
    /// <param name="listDirectors">The tenant's registered Directors.</param>
    /// <param name="launcherRegistration">The tenant's launcher registration row for a machine, or null.</param>
    /// <param name="launcherConnection">The launcher's live command stream and declaration, or null when it
    /// holds none. The DELEGATE ITSELF is null on a Gateway wired without the stream registry, and then
    /// every request is refused with 503 - a verdict computed from what cannot be observed would refuse
    /// every machine as unreachable, or worse, and this is the answer an owner is about to act on.</param>
    /// <param name="directorSessions">What the Gateway holds of one Director's sessions right now.</param>
    /// <param name="findSession">One session by id within the tenant, for the requester's name.</param>
    /// <param name="sendCommand">Send a command down a Director's stream. Null result means no stream.</param>
    public DirectorRestartRequestService(
        DirectorRestartRequestStore store,
        Func<TenantId, IReadOnlyCollection<DirectorDto>> listDirectors,
        Func<TenantId, string, LauncherDto?> launcherRegistration,
        Func<TenantId, string, LauncherStreamConnection?>? launcherConnection,
        Func<TenantId, string, PushedSessionStore.DirectorKnowledge> directorSessions,
        Func<TenantId, string, SessionDto?> findSession,
        Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> sendCommand)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _listDirectors = listDirectors ?? throw new ArgumentNullException(nameof(listDirectors));
        _launcherRegistration = launcherRegistration ?? throw new ArgumentNullException(nameof(launcherRegistration));
        _launcherConnection = launcherConnection;
        _directorSessions = directorSessions ?? throw new ArgumentNullException(nameof(directorSessions));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
    }

    /// <summary>
    /// A session asks. Scrutinise the machine and either create the pending record or refuse on the spot.
    /// </summary>
    /// <param name="tenant">The calling tenant, from the authenticated credential.</param>
    /// <param name="machine">The machine named in the path.</param>
    /// <param name="body">The reason and, optionally, which Director.</param>
    /// <param name="caller">The session that is asking, or null when the caller is a device rather than a
    /// session (the owner asking from the Cockpit on the fleet's behalf).</param>
    public async Task<RestartRequestAnswer> CreateAsync(TenantId tenant, string machine,
        CreateDirectorRestartRequest? body, SessionCredentialIdentity? caller, CancellationToken ct)
    {
        FileLog.Write($"[DirectorRestartRequestService] Create tenant={tenant.Value} machine={machine} "
                      + $"caller={(caller is null ? "device" : caller.SessionId.ToString())}");

        if (body is null || string.IsNullOrWhiteSpace(body.Reason))
            return new RestartRequestAnswer(400, new
            {
                code = "reason_required",
                error = "say why the Director should be restarted, in your own words. The owner decides "
                       + "on that sentence, and a request with none gives him nothing to decide on.",
                machine,
            });
        var reason = body.Reason.Trim();

        // ---- Which Director. Never a guess between two. ----
        var onMachine = _listDirectors(tenant)
            .Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (onMachine.Count == 0)
            return new RestartRequestAnswer(404, new
            {
                code = "no_director_on_machine",
                error = $"no Director is registered on '{machine}' in this account, so there is nothing to "
                       + "drain and nothing to restart. List what is registered with: cc-devthrottle director list",
                machine,
            });

        DirectorDto? target;
        if (!string.IsNullOrWhiteSpace(body.DirectorId))
        {
            target = onMachine.FirstOrDefault(d => string.Equals(d.DirectorId, body.DirectorId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new RestartRequestAnswer(404, new
                {
                    code = "director_not_on_machine",
                    error = $"Director '{body.DirectorId}' is not registered on '{machine}'. The Directors "
                           + $"there are: {Listed(onMachine)}.",
                    machine,
                });
        }
        else if (caller is not null && onMachine.FirstOrDefault(d =>
                     string.Equals(d.DirectorId, caller.DirectorId, StringComparison.OrdinalIgnoreCase)) is { } own)
        {
            // A session asking with no Director named is asking about its OWN Director, when that Director
            // is on the machine it named. This is the ordinary case: "restart the Director I am on".
            target = own;
        }
        else if (onMachine.Count == 1)
        {
            target = onMachine[0];
        }
        else
        {
            return new RestartRequestAnswer(409, new
            {
                code = "director_ambiguous",
                error = $"'{machine}' runs {onMachine.Count} Directors ({Listed(onMachine)}) and the request "
                       + "did not say which. Name one with directorId. Nothing is guessed here: a drain on "
                       + "the wrong Director closes the wrong sessions.",
                machine,
            });
        }

        // ---- The scrutiny: what the machine says, BEFORE the owner is asked. ----
        if (_launcherConnection is null)
            return new RestartRequestAnswer(503, new
            {
                code = "capability_unobservable",
                error = "this Gateway cannot scrutinise the request: its launcher stream registry is not "
                       + "wired, so whether a launcher holds a command stream and what it declared are both "
                       + "unobservable here. This is a Gateway wiring fault, not a fact about the machine. "
                       + "No request was created.",
                machine,
            });

        var capability = MachineRestartCapability.Judge(machine, _launcherRegistration(tenant, machine),
            _launcherConnection(tenant, machine), _store.Clock());

        if (RefuseOnCapability(machine, capability) is { } refusal)
        {
            FileLog.Write($"[DirectorRestartRequestService] Create REFUSED by scrutiny machine={machine}: "
                          + $"verdict={capability.Verdict} guardedRestart={capability.GuardedRestart}");
            return refusal;
        }

        // ---- The Director must be reachable to take the cycle, and its roster is the live count. ----
        var knowledge = _directorSessions(tenant, target.DirectorId);
        if (!knowledge.Connected)
            return new RestartRequestAnswer(409, new
            {
                code = "director_not_connected",
                error = $"Director {Describe(target)} on '{machine}' is registered but holds no stream to this "
                       + "Gateway right now, so it could not be told to drain. Nothing was created. Check that "
                       + "Director is running and connected, then ask again.",
                machine,
                directorId = target.DirectorId,
            });

        // NEVER PUSHED IS NOT ZERO SESSIONS. A Director that has connected and not yet sent its roster has
        // an empty session list here and no push time - and the live count on this record is a sentence
        // the owner decides on. "Holds no live sessions" written from an absence would be plausible and
        // false; the honest answer is that the roster is not known yet.
        if (knowledge.AsOfUtc is null)
            return new RestartRequestAnswer(409, new
            {
                code = "director_roster_not_reported",
                error = $"Director {Describe(target)} on '{machine}' is connected but has not yet reported its "
                       + "sessions to this Gateway, so how many are live is not known - and not knowing is not "
                       + "zero. Nothing was created. Ask again in a moment.",
                machine,
                directorId = target.DirectorId,
            });

        // ---- The Director must confirm it is the one its launcher would restart. ----
        // A launcher restarts the Director it supervises and only that one (issue #2743: the path on the
        // command is ignored). A request that drained a development slot and then restarted the main
        // Director would close one fleet and take another down. Only the Director can answer this - it
        // knows its own instance and executable - so it is asked, and no answer is a refusal.
        var eligibility = await AskEligibilityAsync(target.DirectorId, ct);
        if (eligibility.Refusal is not null)
            return new RestartRequestAnswer(409, new
            {
                code = "director_not_restartable_by_its_launcher",
                error = eligibility.Refusal,
                machine,
                directorId = target.DirectorId,
            });

        var liveCount = knowledge.Sessions.Count;
        var requesterName = RequesterName(tenant, caller);

        var record = new DirectorRestartRequestDto
        {
            Machine = machine,
            DirectorId = target.DirectorId,
            DirectorName = string.IsNullOrWhiteSpace(target.DisplayName) ? target.MachineName : target.DisplayName,
            RequestedBySessionId = caller?.SessionId.ToString() ?? "",
            RequestedBySessionName = requesterName,
            Reason = reason,
            LiveSessionCount = liveCount,
            LiveSessionsSentence = liveCount switch
            {
                0 => "That Director holds no live sessions right now.",
                1 => "That Director holds 1 live session right now; it will be asked to write a handover and close.",
                _ => $"That Director holds {liveCount} live sessions right now; each will be asked to write a handover and close, leaf-first.",
            },
            Capability = capability,
        };
        record.Title = $"Restart the Director on {machine}?";
        record.AskedBySentence = $"{requesterName} asks: {reason}";

        if (!_store.TryCreate(tenant, record, out var pending))
            return new RestartRequestAnswer(409, new
            {
                code = "request_already_pending",
                error = $"a restart of '{machine}' is already pending (request {pending!.Id}, asked by "
                       + $"{pending.RequestedBySessionName} at {pending.RequestedAtUtc:HH:mm} UTC). Two approvals "
                       + "racing one drain is the same hazard as two drains, so the second ask is refused. "
                       + "Wait for the owner's answer to the first.",
                machine,
                pending,
            });

        return new RestartRequestAnswer(201, record);
    }

    /// <summary>The owner accepts. Re-scrutinise, then hand the cycle to the Director.</summary>
    public async Task<RestartRequestAnswer> AcceptAsync(TenantId tenant, string machine, string id, CancellationToken ct)
    {
        FileLog.Write($"[DirectorRestartRequestService] Accept tenant={tenant.Value} machine={machine} id={id}");

        var outcome = _store.Accept(tenant, id, out var request);
        switch (outcome)
        {
            case RestartAcceptOutcome.NotFound:
                return NotFound(machine, id);
            case RestartAcceptOutcome.Expired:
                return new RestartRequestAnswer(409, new
                {
                    code = "request_expired",
                    error = $"request {id} expired at {request!.ExpiresAtUtc:HH:mm} UTC and can no longer be "
                           + "accepted: an approval that outlives its request could restart a Director nobody "
                           + "currently wants restarted. Ask again.",
                    machine,
                    request,
                });
            case RestartAcceptOutcome.NotPending:
                return new RestartRequestAnswer(409, new
                {
                    code = "request_not_pending",
                    error = $"request {id} is {request!.State.ToString().ToLowerInvariant()}, not pending, so "
                           + "there is nothing to accept."
                           + (string.IsNullOrWhiteSpace(request.StateReason) ? "" : " " + request.StateReason),
                    machine,
                    request,
                });
        }

        if (!string.Equals(request!.Machine, machine, StringComparison.OrdinalIgnoreCase))
        {
            // The id is real and it names a different machine than the path. Nothing dispatched.
            _store.Abandon(tenant, id, $"the accept named machine '{machine}' but the request is for '{request.Machine}'", out request);
            return new RestartRequestAnswer(409, new
            {
                code = "machine_mismatch",
                error = $"request {id} is for '{request!.Machine}', not '{machine}'. Nothing was done.",
                machine,
                request,
            });
        }

        // THE MACHINE MAY HAVE CHANGED SINCE THE REQUEST. The same scrutiny again, on the accept, so an
        // approval never starts a cycle the machine can no longer finish. The Director runs it a third
        // time immediately before asking its launcher, and that one is not redundant either.
        if (_launcherConnection is null)
        {
            _store.Abandon(tenant, id, "the Gateway's launcher stream registry is not wired, so the machine could not be re-checked", out request);
            return new RestartRequestAnswer(503, new { code = "capability_unobservable", error = request!.StateReason, machine, request });
        }
        var capability = MachineRestartCapability.Judge(machine, _launcherRegistration(tenant, machine),
            _launcherConnection(tenant, machine), _store.Clock());
        if (DirectorRestartGate.Refusal(capability) is { } detail)
        {
            _store.Abandon(tenant, id, "on accept the machine was checked again and can no longer be restarted: " + detail, out request);
            FileLog.Write($"[DirectorRestartRequestService] Accept ABANDONED id={id}: {detail}");
            return new RestartRequestAnswer(409, new
            {
                code = "cannot_restart_now",
                error = request!.StateReason,
                machine,
                capability,
                request,
            });
        }

        // ---- Hand the cycle to the Director. Nothing else is asked of anybody after this. ----
        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = CycleVerb,
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(new DirectorRestartCycleOrder
            {
                RequestId = request.Id,
                Machine = request.Machine,
                Reason = request.Reason,
                RequestedBySessionId = request.RequestedBySessionId,
                RequestedBySessionName = request.RequestedBySessionName,
                AcceptedAtUtc = request.AcceptedAtUtc ?? _store.Clock(),
            }, Json),
        };

        DirectorCommandResult? result;
        try
        {
            result = await _sendCommand(request.DirectorId, command, ct);
        }
        catch (Exception ex)
        {
            _store.Abandon(tenant, id, $"the Director could not be told to begin: {ex.Message}", out request);
            FileLog.Write($"[DirectorRestartRequestService] Accept dispatch FAILED id={id}: {ex.Message}");
            return new RestartRequestAnswer(502, new { code = "director_dispatch_failed", error = request!.StateReason, machine, request });
        }

        if (result is null)
        {
            _store.Abandon(tenant, id, $"Director {request.DirectorId} holds no stream to this Gateway, so it could not be told to begin. Nothing was drained.", out request);
            return new RestartRequestAnswer(502, new { code = "director_not_connected", error = request!.StateReason, machine, request });
        }
        if (result.Status != DirectorCommandStatus.Ok)
        {
            _store.Abandon(tenant, id, $"the Director refused to begin: {result.Error ?? result.Status.ToString()}", out request);
            return new RestartRequestAnswer(502, new { code = "director_refused", error = request!.StateReason, machine, request });
        }

        _store.Report(tenant, id, new DirectorRestartProgressReport
        {
            State = DirectorRestartRequestState.Accepted,
            Progress = "the Director has taken the cycle: it is draining itself and will report here",
        }, out request);
        FileLog.Write($"[DirectorRestartRequestService] Accept id={id}: cycle handed to director={request!.DirectorId}");
        return new RestartRequestAnswer(200, request);
    }

    /// <summary>The owner declines.</summary>
    public RestartRequestAnswer Decline(TenantId tenant, string machine, string id, string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "declined by the owner" : $"declined by the owner: {reason.Trim()}";
        if (_store.Decline(tenant, id, why, out var request))
            return new RestartRequestAnswer(200, request!);
        if (request is null) return NotFound(machine, id);
        return new RestartRequestAnswer(409, new
        {
            code = "request_not_pending",
            error = $"request {id} is {request.State.ToString().ToLowerInvariant()}, not pending, so there is nothing to decline.",
            machine,
            request,
        });
    }

    /// <summary>The Director reports.</summary>
    public RestartRequestAnswer Report(TenantId tenant, string machine, string id, DirectorRestartProgressReport? report)
    {
        if (report is null || string.IsNullOrWhiteSpace(report.Progress))
            return new RestartRequestAnswer(400, new { code = "progress_required", error = "a report says what happened, in a sentence.", machine });
        if (report.State is not (DirectorRestartRequestState.Accepted or DirectorRestartRequestState.Abandoned or DirectorRestartRequestState.Completed))
            return new RestartRequestAnswer(400, new
            {
                code = "state_not_reportable",
                error = $"a Director reports Accepted (still running), Abandoned or Completed; '{report.State}' is not its to set.",
                machine,
            });
        if (_store.Report(tenant, id, report, out var request))
            return new RestartRequestAnswer(200, request!);
        if (request is null) return NotFound(machine, id);
        return new RestartRequestAnswer(409, new
        {
            code = "request_not_running",
            error = $"request {id} is {request.State.ToString().ToLowerInvariant()}, so a Director's report has nowhere to land.",
            machine,
            request,
        });
    }

    /// <summary>
    /// The refusal a capability answer earns, or null when the machine can be restarted AND its launcher
    /// will refuse to restart a Director holding live sessions. The rule itself is
    /// <see cref="DirectorRestartGate"/>, shared with the Director, which applies it again immediately
    /// before asking its launcher.
    /// </summary>
    internal static RestartRequestAnswer? RefuseOnCapability(string machine, MachineRestartCapabilityDto capability)
    {
        var refusal = DirectorRestartGate.Refusal(capability);
        if (refusal is null) return null;
        return new RestartRequestAnswer(409, new
        {
            code = DirectorRestartGate.RefusalCode(capability),
            error = refusal,
            machine,
            capability,
        });
    }

    private sealed record Eligibility(string? Refusal);

    private async Task<Eligibility> AskEligibilityAsync(string directorId, CancellationToken ct)
    {
        DirectorCommandResult? result;
        try
        {
            result = await _sendCommand(directorId, new DirectorCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Verb = EligibilityVerb,
                SessionId = "",
                PayloadJson = "{}",
            }, ct);
        }
        catch (Exception ex)
        {
            return new Eligibility($"Director {directorId} could not be asked whether its launcher would restart it: {ex.Message}. No request was created.");
        }

        if (result is null)
            return new Eligibility($"Director {directorId} holds no stream to this Gateway, so it could not be asked whether its launcher would restart it. No request was created.");
        if (result.Status != DirectorCommandStatus.Ok)
            return new Eligibility($"Director {directorId} did not answer whether its launcher would restart it "
                                   + $"({result.Status}: {result.Error ?? "no detail"}). A Director built before this "
                                   + "question existed cannot answer it, and no answer is not a yes. Update that Director. No request was created.");

        DirectorRestartEligibilityDto? answer = null;
        try { answer = JsonSerializer.Deserialize<DirectorRestartEligibilityDto>(result.BodyJson ?? "", Json); }
        catch (JsonException) { /* handled as no answer below */ }
        if (answer is null)
            return new Eligibility($"Director {directorId} answered the eligibility question with a body this Gateway could not read. No request was created.");

        // == against the one answer that permits.
        if (answer.Eligible == true) return new Eligibility(null);
        return new Eligibility(string.IsNullOrWhiteSpace(answer.Reason)
            ? $"Director {directorId} says it is not the Director its launcher supervises, and gave no reason. No request was created."
            : answer.Reason + " No request was created.");
    }

    private string RequesterName(TenantId tenant, SessionCredentialIdentity? caller)
    {
        if (caller is null) return "a device on this account";
        var session = _findSession(tenant, caller.SessionId.ToString());
        var shortId = caller.SessionId.ToString("N")[..8];
        if (session is null) return $"session {shortId} (not on the roster)";
        return string.IsNullOrWhiteSpace(session.Name) ? $"session {shortId} (unnamed)" : $"{session.Name} ({shortId})";
    }

    private static RestartRequestAnswer NotFound(string machine, string id) => new(404, new
    {
        code = "request_not_found",
        error = $"no restart request {id} exists for '{machine}' in this account. It may have been swept after closing, or it may never have existed.",
        machine,
    });

    private static string Describe(DirectorDto d)
        => string.IsNullOrWhiteSpace(d.DisplayName) ? d.DirectorId : $"{d.DirectorId} ({d.DisplayName})";

    private static string Listed(IEnumerable<DirectorDto> directors)
        => string.Join(", ", directors.Select(Describe));
}
