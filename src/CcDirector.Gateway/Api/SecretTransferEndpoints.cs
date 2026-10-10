using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Secrets;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE SECRET HANDOFF ROUTES (issue #2943). A secret moves between two of the owner's machines sealed to the receiving
/// machine's key, so the Gateway relays it and can never read it. Every transfer needs ONE approval from the owner,
/// given wherever it was asked for, and the first answer wins.
///
/// <list type="bullet">
/// <item><c>GET /gateway/secrets/machines</c> - the machines that can receive: names and PUBLIC keys only.</item>
/// <item><c>POST /gateway/secrets/transfers</c> - ask for a transfer. A session may pass the owner's words from its chat
/// (owner decision 3), and the transfer is born approved; a machine's own credential (the cc-secrets window, the owner's
/// terminal) may say the owner approved it there; anything else waits for an answer.</item>
/// <item><c>GET /gateway/secrets/transfers</c> and <c>GET /gateway/secrets/transfers/{id}</c> - read them.</item>
/// <item><c>POST /gateway/secrets/transfers/{id}/answer</c> - approve or deny, from the phone, the Cockpit, the window, a
/// terminal or an agent's chat (owner decision 4). A session approves only with the owner's words.</item>
/// </list>
///
/// No route here takes or returns a secret or an envelope: the body of every one is names, machines, a reason, an
/// answer. The envelope travels only down the Directors' own tunnels, while a transfer is being delivered.
/// </summary>
public static class SecretTransferEndpoints
{
    public const string MachinesRoute = "/gateway/secrets/machines";
    public const string TransfersRoute = "/gateway/secrets/transfers";
    public const string TransferRoute = "/gateway/secrets/transfers/{id}";
    public const string AnswerRoute = "/gateway/secrets/transfers/{id}/answer";

    /// <summary>How far back finished transfers are listed.</summary>
    public const int FinishedWithinHours = 24;

    private static readonly Regex EntryName = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex FingerprintShape = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    private const string KindSession = "session";
    private const string KindOwnerDevice = "owner-device";
    private const string KindMachine = "machine";

    /// <summary>Who is calling: a session (cc-secrets inside a session), the owner's own phone or browser, or a
    /// machine's own credential (cc-secrets in the owner's terminal or window, which reads the machine's credential).</summary>
    internal sealed record SecretCaller(string Kind, string? SessionId, string? Surface, string Actor);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        SecretMachineRegistry machines, Func<TenantId, string, bool> isConnected, SecretTransferStore transfers,
        Func<TenantId, string, SessionDto?> findSession, Action<TenantId, string> startDelivery, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(isConnected);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(findSession);
        ArgumentNullException.ThrowIfNull(startDelivery);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapGet(MachinesRoute, (HttpContext ctx) => ListMachines(ctx, resolveTenant, machines, isConnected, nowUtc));
        // The return types are stated on purpose: an async lambda whose only parameter is HttpContext also fits
        // RequestDelegate, which would throw the IResult away and answer an empty 200 (found in #3549).
        app.MapPost(TransfersRoute, async Task<IResult> (HttpContext ctx) =>
            await CreateAsync(ctx, resolveTenant, machines, isConnected, transfers, findSession, startDelivery, nowUtc));
        app.MapGet(TransfersRoute, (HttpContext ctx) => List(ctx, resolveTenant, transfers, nowUtc));
        app.MapGet(TransferRoute, (HttpContext ctx, string id) => Get(ctx, id, resolveTenant, transfers, nowUtc));
        app.MapPost(AnswerRoute, async Task<IResult> (HttpContext ctx, string id) =>
            await AnswerAsync(ctx, id, resolveTenant, transfers, startDelivery, nowUtc));
        FileLog.Write($"[SecretTransferEndpoints] mapped GET {MachinesRoute}, POST/GET {TransfersRoute}, GET {TransferRoute}, POST {AnswerRoute}");
    }

    internal static IResult ListMachines(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        SecretMachineRegistry machines, Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        FileLog.Write("[SecretTransferEndpoints] GET machines");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var rows = machines.Machines(tenant, directorId => isConnected(tenant, directorId), nowUtc());
            FileLog.Write($"[SecretTransferEndpoints] GET machines: {rows.Count} machine(s)");
            return Results.Json(new SecretMachineListResponse { Machines = rows });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] GET {MachinesRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> CreateAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        SecretMachineRegistry machines, Func<TenantId, string, bool> isConnected, SecretTransferStore transfers,
        Func<TenantId, string, SessionDto?> findSession, Action<TenantId, string> startDelivery, Func<DateTime> nowUtc)
    {
        FileLog.Write("[SecretTransferEndpoints] POST transfer");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = Classify(ctx);

            SecretTransferCreateRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SecretTransferCreateRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "The body is not valid JSON.");
            }
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body",
                    "A body is required: { entry, fromMachine, toMachine, replace, reason }.");

            var entry = (body.Entry ?? "").Trim();
            var target = string.IsNullOrWhiteSpace(body.TargetName) ? entry : body.TargetName.Trim();
            if (!EntryName.IsMatch(entry) || !EntryName.IsMatch(target))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_entry",
                    "An entry name is lower-case letters, digits, dot, dash or underscore, starting with a letter or digit, "
                    + "at most 64 characters.");
            var reason = (body.Reason ?? "").Trim();
            if (caller.Kind == KindSession && reason.Length == 0)
                return Refuse(StatusCodes.Status400BadRequest, "reason_required",
                    "Say why this secret is needed, in one sentence the owner can decide on (--reason).");

            // Both machines must be able to take part NOW: a receiver that is offline fails at once, with the reason,
            // rather than leaving an approval the owner could give for nothing.
            var listed = machines.Machines(tenant, directorId => isConnected(tenant, directorId), nowUtc());
            var from = Listed(listed, body.FromMachine, out var fromRefusal);
            if (from is null) return fromRefusal!;
            var to = Listed(listed, body.ToMachine, out var toRefusal);
            if (to is null) return toRefusal!;
            if (string.Equals(from.Machine, to.Machine, StringComparison.OrdinalIgnoreCase))
                return Refuse(StatusCodes.Status400BadRequest, "same_machine",
                    $"{from.Machine} is both the sending and the receiving machine. A transfer moves a secret between two machines.");

            var answer = BornAnswer(caller, body, from.Machine, to.Machine, out var answerRefusal);
            if (answerRefusal is not null) return answerRefusal;

            var askedBy = AskedBy(tenant, caller, body.AskedOn, from.Machine, to.Machine, findSession);
            var row = transfers.Create(tenant,
                new SecretTransferAsk(entry, target, from.Machine, to.Machine, body.Replace, caller.SessionId, askedBy, reason),
                answer, nowUtc());
            if (row is null)
                return Refuse(StatusCodes.Status429TooManyRequests, "too_many_waiting",
                    $"You already have {SecretTransferStore.MaxWaitingPerSession} transfers waiting for the owner. Nothing "
                    + "was asked. Wait for an answer.");

            if (row.State == SecretTransferStates.Approved)
                startDelivery(tenant, row.TransferId);
            var now = nowUtc();
            FileLog.Write($"[SecretTransferEndpoints] POST transfer: transfer={row.TransferId}, state={row.State}, by={caller.Actor}");
            return Results.Json(new SecretTransferResponse
            {
                Transfer = ToDto(row, now),
                Note = row.State == SecretTransferStates.Approved
                    ? "Approved. Delivering now; read this transfer to see how it ends."
                    : "Waiting for the owner's answer - on the phone, in the Cockpit, in the cc-secrets window on "
                      + $"{from.Machine}, or with cc-secrets approve {row.TransferId}. It expires in 15 minutes.",
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] POST {TransfersRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult List(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, SecretTransferStore transfers,
        Func<DateTime> nowUtc)
    {
        FileLog.Write("[SecretTransferEndpoints] GET transfers");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var now = nowUtc();
            var rows = transfers.List(tenant, now - TimeSpan.FromHours(FinishedWithinHours), now);
            return Results.Json(new SecretTransferListResponse
            {
                Transfers = rows.Select(r => ToDto(r, now)).ToList(),
                FinishedWithinHours = FinishedWithinHours,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] GET {TransfersRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Get(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        SecretTransferStore transfers, Func<DateTime> nowUtc)
    {
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var now = nowUtc();
            var row = transfers.Find(tenant, id, now);
            if (row is null)
                return Refuse(StatusCodes.Status404NotFound, "transfer_not_found", $"No transfer {id} is known in this account.");
            return Results.Json(new SecretTransferResponse { Transfer = ToDto(row, now), Note = "" });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] GET {TransferRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> AnswerAsync(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        SecretTransferStore transfers, Action<TenantId, string> startDelivery, Func<DateTime> nowUtc)
    {
        FileLog.Write($"[SecretTransferEndpoints] POST answer: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = Classify(ctx);

            SecretTransferAnswerRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SecretTransferAnswerRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "The body is not valid JSON.");
            }
            if (body is null || body.Approve == body.Deny)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_answer",
                    "Answer with exactly one of { approve: true } or { deny: true }.");

            var now = nowUtc();
            var row = transfers.Find(tenant, id, now);
            if (row is null)
                return Refuse(StatusCodes.Status404NotFound, "transfer_not_found", $"No transfer {id} is known in this account.");

            var answer = GivenAnswer(caller, body, out var refusal);
            if (answer is null) return refusal!;
            if (!transfers.TryAnswer(tenant, row.TransferId, answer, now))
                return AlreadyAnswered(transfers.Find(tenant, row.TransferId, now)!);

            if (answer.Approve)
                startDelivery(tenant, row.TransferId);
            var after = transfers.Find(tenant, row.TransferId, now)!;
            FileLog.Write($"[SecretTransferEndpoints] POST answer: transfer={row.TransferId}, approve={answer.Approve}, where={answer.Where}, by={caller.Actor}");
            return Results.Json(new SecretTransferResponse
            {
                Transfer = ToDto(after, now),
                Note = answer.Approve ? "Approved. Delivering now." : "Denied. Nothing was moved.",
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] POST {AnswerRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- the rules -------------------------------------------------------------------------------------------

    internal static SecretCaller Classify(HttpContext ctx)
    {
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            var sid = session.SessionId.ToString("D");
            return new SecretCaller(KindSession, sid, null, $"session {sid}");
        }
        var device = AuthMiddleware.AuthenticatedDevice(ctx);
        var surface = SessionOriginSurfaces.FromDeviceType(device?.DeviceType);
        if (device is not null && surface != SessionOriginSurfaces.Unknown)
            return new SecretCaller(KindOwnerDevice, null, surface, $"the owner's {surface} ({device.DeviceId})");
        var credential = AuthMiddleware.RegisteringCredential(ctx) ?? "unknown credential";
        return new SecretCaller(KindMachine, null, null, $"a machine's own credential ({credential})");
    }

    /// <summary>The answer a new transfer is born with, or null when it must wait; <paramref name="refusal"/> when the
    /// caller asked for something it may not.</summary>
    private static SecretTransferAnswer? BornAnswer(SecretCaller caller, SecretTransferCreateRequest body,
        string fromMachine, string toMachine, out IResult? refusal)
    {
        refusal = null;
        var hasWords = body.OwnerApproved is not null;
        var words = (body.OwnerApproved ?? "").Trim();
        var here = (body.ApprovedHere ?? "").Trim().ToLowerInvariant();
        var accept = (body.AcceptReceiverFingerprint ?? "").Trim().ToLowerInvariant();

        if (caller.Kind == KindSession)
        {
            if (here.Length > 0 || accept.Length > 0)
            {
                refusal = Refuse(StatusCodes.Status403Forbidden, "session_cannot_approve_here",
                    "A session cannot say the owner approved in the window or a terminal, or accept a changed machine "
                    + "key. Pass the owner's own words from this session's chat with --owner-approved, or let the transfer "
                    + "wait for his answer.");
                return null;
            }
            if (hasWords && words.Length == 0)
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "owner_approved_empty",
                    "--owner-approved was given with no words. Give the owner's approval of this transfer, verbatim.");
                return null;
            }
            return words.Length > 0
                ? new SecretTransferAnswer(true, SecretTransferPlaces.Chat, caller.Actor, words, null)
                : null;
        }

        if (hasWords)
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "words_from_a_session_only",
                "Only a session reports the owner's words from its chat. In the window or a terminal the owner approves "
                + "there directly.");
            return null;
        }
        if (caller.Kind == KindOwnerDevice)
        {
            if (here.Length > 0 || accept.Length > 0)
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "approve_with_answer",
                    "From the phone or the Cockpit, approve a waiting transfer with its Approve button.");
                return null;
            }
            return null;
        }

        // A machine's own credential: cc-secrets in the owner's terminal or window on one of the two machines.
        if (here.Length == 0)
        {
            if (accept.Length > 0)
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "accept_needs_approval",
                    "A changed machine key is accepted only together with the owner's approval in the window or terminal.");
                return null;
            }
            return null;
        }
        if (here is not (SecretTransferPlaces.Window or SecretTransferPlaces.Terminal))
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "invalid_place",
                "approvedHere is 'window' or 'terminal' - where the owner approved it on this machine.");
            return null;
        }
        var askedOn = (body.AskedOn ?? "").Trim();
        if (!string.Equals(askedOn, fromMachine, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(askedOn, toMachine, StringComparison.OrdinalIgnoreCase))
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "asked_on_another_machine",
                "The owner approves a transfer in the window or terminal of one of its two machines; askedOn must name "
                + $"{fromMachine} or {toMachine}.");
            return null;
        }
        if (accept.Length > 0 && !FingerprintShape.IsMatch(accept))
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "invalid_fingerprint",
                "acceptReceiverFingerprint is the 64 hex characters of the receiving machine's key fingerprint.");
            return null;
        }
        return new SecretTransferAnswer(true, here, caller.Actor, null, accept.Length > 0 ? accept : null);
    }

    /// <summary>The answer a caller gives to a waiting transfer, or null with <paramref name="refusal"/>.</summary>
    private static SecretTransferAnswer? GivenAnswer(SecretCaller caller, SecretTransferAnswerRequest body, out IResult? refusal)
    {
        refusal = null;
        var words = (body.OwnerApproved ?? "").Trim();
        var where = (body.Where ?? "").Trim().ToLowerInvariant();
        var accept = (body.AcceptReceiverFingerprint ?? "").Trim().ToLowerInvariant();

        if (caller.Kind == KindSession)
        {
            if (accept.Length > 0)
            {
                refusal = Refuse(StatusCodes.Status403Forbidden, "session_cannot_accept_key",
                    "A session cannot accept a changed machine key. The owner does that in the cc-secrets window.");
                return null;
            }
            if (body.OwnerApproved is not null && words.Length == 0)
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "owner_approved_empty",
                    "--owner-approved was given with no words. Give the owner's approval of this transfer, verbatim.");
                return null;
            }
            if (body.Approve && words.Length == 0)
            {
                refusal = Refuse(StatusCodes.Status403Forbidden, "owner_words_required",
                    "A session approves a transfer only with the owner's own words from its chat: ask the owner, then rerun with "
                    + "--owner-approved \"<the owner's words, verbatim>\". The owner can also approve it on the phone, in the Cockpit or in "
                    + "the cc-secrets window.");
                return null;
            }
            return new SecretTransferAnswer(body.Approve, SecretTransferPlaces.Chat, caller.Actor, words.Length > 0 ? words : null, null);
        }

        if (body.OwnerApproved is not null)
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "words_from_a_session_only",
                "Only a session reports the owner's words from its chat.");
            return null;
        }

        if (caller.Kind == KindOwnerDevice)
        {
            if (accept.Length > 0)
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "accept_in_the_window",
                    "A changed machine key is accepted in the cc-secrets window on the sending machine.");
                return null;
            }
            var allowed = caller.Surface == SessionOriginSurfaces.Phone
                ? new[] { SecretTransferPlaces.Phone }
                : new[] { SecretTransferPlaces.Cockpit, SecretTransferPlaces.Badge };
            if (where.Length == 0) where = allowed[0];
            if (!allowed.Contains(where))
            {
                refusal = Refuse(StatusCodes.Status400BadRequest, "invalid_place",
                    $"From this device the answer is given {string.Join(" or ", allowed.Select(SecretTransferPlaces.Words))}.");
                return null;
            }
            return new SecretTransferAnswer(body.Approve, where, caller.Actor, null, null);
        }

        if (where is not (SecretTransferPlaces.Window or SecretTransferPlaces.Terminal))
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "invalid_place",
                "From a machine's own credential the answer is given in the 'window' or a 'terminal'.");
            return null;
        }
        if (accept.Length > 0 && !FingerprintShape.IsMatch(accept))
        {
            refusal = Refuse(StatusCodes.Status400BadRequest, "invalid_fingerprint",
                "acceptReceiverFingerprint is the 64 hex characters of the receiving machine's key fingerprint.");
            return null;
        }
        return new SecretTransferAnswer(body.Approve, where, caller.Actor, null, accept.Length > 0 ? accept : null);
    }

    private static SecretMachineDto? Listed(List<SecretMachineDto> listed, string? name, out IResult? refusal)
    {
        refusal = null;
        var wanted = (name ?? "").Trim();
        var match = listed.FirstOrDefault(m => string.Equals(m.Machine, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var there = listed.Count == 0 ? "none" : string.Join(", ", listed.Select(m => m.Machine));
            refusal = Refuse(StatusCodes.Status409Conflict, "machine_offline",
                $"'{wanted}' cannot take part in a transfer right now: no Director that can is connected there (machines "
                + $"that can: {there}). Nothing was asked.");
            return null;
        }
        if (match.Conflict is not null)
        {
            refusal = Refuse(StatusCodes.Status409Conflict, "machine_key_conflict", match.Conflict + " Nothing was asked.");
            return null;
        }
        return match;
    }

    private static string AskedBy(TenantId tenant, SecretCaller caller, string? askedOn, string fromMachine, string toMachine,
        Func<TenantId, string, SessionDto?> findSession)
    {
        if (caller.Kind == KindSession)
        {
            var row = findSession(tenant, caller.SessionId!);
            var number = row?.Number is { } n ? $"{n:000} " : "";
            var name = string.IsNullOrWhiteSpace(row?.Name) ? caller.SessionId : row!.Name;
            return $"Session {number}\"{name}\"";
        }
        if (caller.Kind == KindOwnerDevice)
            return $"You, on the {caller.Surface}";
        var machine = string.IsNullOrWhiteSpace(askedOn) ? $"{fromMachine} or {toMachine}" : askedOn.Trim();
        return $"You, in the cc-secrets window or a terminal on {machine}";
    }

    private static IResult AlreadyAnswered(SecretTransferEntity row) => Refuse(StatusCodes.Status409Conflict, "already_answered",
        row.State switch
        {
            SecretTransferStates.Expired => "This transfer expired before it was answered. Nothing changed.",
            SecretTransferStates.Denied => $"This transfer was already denied {SecretTransferPlaces.Words(row.AnsweredWhere)}. Nothing changed.",
            _ => $"This transfer was already approved {SecretTransferPlaces.Words(row.AnsweredWhere)}. Nothing changed.",
        });

    /// <summary>The transfer as every surface reads it, with its display strings folded here, once.</summary>
    internal static SecretTransferDto ToDto(SecretTransferEntity r, DateTime nowUtc)
    {
        var expires = AsUtc(r.ExpiresAtUtc);
        var waiting = r.State == SecretTransferStates.Waiting && expires > nowUtc;
        return new SecretTransferDto
        {
            TransferId = r.TransferId,
            Entry = r.EntryName,
            TargetName = r.TargetName,
            FromMachine = r.FromMachine,
            ToMachine = r.ToMachine,
            Replace = r.Replace,
            AskedBySessionId = r.AskedBySessionId,
            AskedBy = r.AskedBy,
            Reason = r.Reason,
            State = r.State,
            CreatedAtUtc = AsUtc(r.CreatedAtUtc),
            ExpiresAtUtc = expires,
            AnsweredWhere = r.AnsweredWhere,
            AnsweredBy = r.AnsweredBy,
            ApprovalWords = r.ApprovalWords,
            AnsweredAtUtc = r.AnsweredAtUtc is { } a ? AsUtc(a) : null,
            Outcome = r.Outcome,
            FinishedAtUtc = r.FinishedAtUtc is { } f ? AsUtc(f) : null,
            Summary = $"{r.EntryName} from {r.FromMachine} to {r.ToMachine}"
                      + (r.TargetName != r.EntryName ? $" (stored as {r.TargetName})" : ""),
            ReplaceNote = r.Replace ? $"Replaces the entry already on {r.ToMachine}." : null,
            StatusText = r.State switch
            {
                SecretTransferStates.Waiting => $"Waiting for your answer. Asked by {r.AskedBy}.",
                SecretTransferStates.Approved => $"Approved {SecretTransferPlaces.Words(r.AnsweredWhere)}. Delivering.",
                _ => r.Outcome ?? r.State,
            },
            CanAnswer = waiting,
        };
    }

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
