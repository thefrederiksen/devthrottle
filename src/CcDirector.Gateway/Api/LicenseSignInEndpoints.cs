using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE SIGN-IN ROUTES OF THE LICENSES PAGE (the License Switching mission, contract section 8).
///
/// <list type="bullet">
/// <item><c>GET /gateway/licenses/approvers</c> - which machine and browser profile approve each account's sign-ins.</item>
/// <item><c>PUT /gateway/licenses/approvers</c> - set the approver of one account.</item>
/// <item><c>DELETE /gateway/licenses/approvers?accountEmail=</c> - remove it.</item>
/// <item><c>POST /gateway/licenses/sign-ins/add-spare</c> - sign a machine in to an account it does not hold yet.</item>
/// <item><c>POST /gateway/licenses/sign-ins/renew</c> - renew a sign-in a machine holds.</item>
/// </list>
///
/// Every route answers for the CALLER'S OWN account and no other: the tenant comes from the request's credential,
/// never from the path, the query or a body, and a request with no account bound is refused.
///
/// THE FOUR WRITES ARE FOR A SIGNED-IN PERSON ONLY, the same one rule as the two writes of <see cref="LicenseEndpoints"/>:
/// the owner's own phone or browser, and nothing else. Who approves a sign-in, and starting one, are the owner's own
/// actions. A session key is refused, raised or not (the read is in <c>SessionKeyGuard</c>'s allow list; the writes are
/// not), and so are a Director's own device key and the shared machine token, before a byte of the body is read.
///
/// THE TWO SIGN-IN ROUTES ANSWER AT ONCE. A sign-in takes minutes; the request is answered <c>202</c> as soon as its
/// row of the log is written and its command is on its way, and the outcome arrives on that row through the log route.
///
/// THE CLIENT IS DUMB (CLAUDE.md rule 7): every verdict in these answers is folded by <see cref="LicenseSignInFold"/>,
/// and a refusal carries a finished sentence. No route here takes or returns a sign-in key, the address a sign-in is
/// approved at, or an approval - the Gateway keeps none of them.
/// </summary>
public static class LicenseSignInEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="isConnected">Whether that account's Director, by its identifier, has a stream connection now.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        LicenseApproverStore approvers, LicenseSignInService signIns, LicenseReadModel reads,
        Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(approvers);
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(isConnected);
        ArgumentNullException.ThrowIfNull(nowUtc);

        static IResult? OwnerOnly(HttpContext ctx, string what)
        {
            FleetManagerOwnerDevice.Require(ctx, what,
                $"a session may not {what}; that is the owner's own action, from the Licenses page of the Cockpit",
                "LicenseSignInEndpoints", out var refusal);
            return refusal;
        }

        const string setApprover = "set which machine approves an account's sign-ins";
        const string removeApprover = "remove the machine that approves an account's sign-ins";
        const string startSignIn = "start a sign-in on a machine";

        app.MapGet(LicenseSignInRoutes.Approvers, (HttpContext ctx) => ListApprovers(ctx, resolveTenant, approvers, reads, isConnected, nowUtc));
        // The three handlers that read a body are given as Func<HttpContext, Task<IResult>> ON PURPOSE. A lambda that
        // takes the request and returns a task also fits the plain request delegate, which is what would be chosen:
        // the handler would run and its answer be thrown away, a 200 with an empty body whatever it decided.
        app.MapPut(LicenseSignInRoutes.Approvers, (Func<HttpContext, Task<IResult>>)(async ctx =>
            OwnerOnly(ctx, setApprover) ?? await SetApproverAsync(ctx, resolveTenant, approvers, reads, isConnected, nowUtc)));
        app.MapDelete(LicenseSignInRoutes.Approvers, (HttpContext ctx) => OwnerOnly(ctx, removeApprover) ?? RemoveApprover(ctx, resolveTenant, approvers, nowUtc));
        app.MapPost(LicenseSignInRoutes.AddSpare, (Func<HttpContext, Task<IResult>>)(async ctx =>
            OwnerOnly(ctx, startSignIn) ?? await StartSignInAsync(ctx, resolveTenant, signIns, LicenseSignInReasons.Manual)));
        app.MapPost(LicenseSignInRoutes.Renew, (Func<HttpContext, Task<IResult>>)(async ctx =>
            OwnerOnly(ctx, startSignIn) ?? await StartSignInAsync(ctx, resolveTenant, signIns, LicenseSignInReasons.Renewal)));
        FileLog.Write($"[LicenseSignInEndpoints] mapped GET, PUT and DELETE {LicenseSignInRoutes.Approvers}, POST {LicenseSignInRoutes.AddSpare}, POST {LicenseSignInRoutes.Renew}");
    }

    internal static IResult ListApprovers(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicenseApproverStore approvers,
        LicenseReadModel reads, Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseSignInEndpoints] GET /gateway/licenses/approvers");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var now = nowUtc();
            var response = LicenseSignInFold.Approvers(approvers.List(tenant), reads.Read(tenant, now).Machines,
                directorId => isConnected(tenant, directorId), now);
            FileLog.Write($"[LicenseSignInEndpoints] GET /gateway/licenses/approvers: tenant={tenant.ToLogString()}, approvers={response.Approvers.Count}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInEndpoints] GET /gateway/licenses/approvers FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> SetApproverAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseApproverStore approvers, LicenseReadModel reads, Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseSignInEndpoints] PUT /gateway/licenses/approvers");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var (body, unreadable) = await ReadBodyAsync<LicenseSetApproverRequest>(ctx).ConfigureAwait(false);
            if (body is null)
                return unreadable!;

            var now = nowUtc();
            var set = approvers.Set(tenant, body.AccountEmail, body.MachineName, body.OsUser, body.BrowserProfile, now);
            if (set.Approver is not { } approver)
                return Refuse(set.RefusalWord == "too_many_approvers" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                    set.RefusalWord!, set.RefusalText!);

            var dto = LicenseSignInFold.Approver(approver, reads.Read(tenant, now).Machines, directorId => isConnected(tenant, directorId), now);
            FileLog.Write($"[LicenseSignInEndpoints] PUT /gateway/licenses/approvers: tenant={tenant.ToLogString()}, account={dto.AccountEmail}, status={dto.Status}");
            return Results.Json(dto);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInEndpoints] PUT /gateway/licenses/approvers FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult RemoveApprover(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicenseApproverStore approvers,
        Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseSignInEndpoints] DELETE /gateway/licenses/approvers");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var email = LicenseStore.NormalizeEmail(ctx.Request.Query.TryGetValue("accountEmail", out var given) ? given.ToString() : null);
            if (!LicenseApproverStore.IsAccountEmail(email))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_account", "accountEmail is the email of a Claude account.");

            var removed = approvers.Remove(tenant, email, nowUtc());
            FileLog.Write($"[LicenseSignInEndpoints] DELETE /gateway/licenses/approvers: tenant={tenant.ToLogString()}, account={email}, removed={removed}");
            return Results.Json(new LicenseApproverRemovedResponse
            {
                AccountEmail = email,
                Removed = removed,
                Text = removed
                    ? $"No machine is set to approve sign-ins for {email} any more."
                    : $"No machine was set to approve sign-ins for {email}.",
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInEndpoints] DELETE /gateway/licenses/approvers FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> StartSignInAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSignInService signIns, string reason)
    {
        FileLog.Write($"[LicenseSignInEndpoints] POST /gateway/licenses/sign-ins: reason={reason}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var (body, unreadable) = await ReadBodyAsync<LicenseStartSignInRequest>(ctx).ConfigureAwait(false);
            if (body is null)
                return unreadable!;

            var start = signIns.Start(tenant, body.AccountEmail, body.MachineId, reason);
            if (!start.Accepted)
                return Refuse(start.Status, start.RefusalWord!, start.RefusalText!);

            var row = start.Row!;
            var machine = start.Machine!;
            var label = start.Label!;
            var response = new LicenseSignInStartedResponse
            {
                SignInId = row.SwitchId!,
                AccountEmail = row.ToAccountEmail,
                Machine = new LicenseMachineRefDto
                {
                    MachineId = machine.MachineId.ToString("N"),
                    MachineName = machine.MachineName,
                    OsUser = machine.OsUser,
                    Label = label,
                },
                Text = $"{row.Message} It can take a few minutes; the outcome appears in the log.",
                Entry = LicenseSignInFold.LogEntry(row, label),
            };
            FileLog.Write($"[LicenseSignInEndpoints] POST /gateway/licenses/sign-ins: tenant={tenant.ToLogString()}, signIn={response.SignInId}, accepted");
            return Results.Json(response, statusCode: start.Status);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSignInEndpoints] POST /gateway/licenses/sign-ins FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The request's body, or the refusal to answer with when it is missing or cannot be read.</summary>
    private static async Task<(T? Body, IResult? Unreadable)> ReadBodyAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<T>(Json, ctx.RequestAborted).ConfigureAwait(false);
            return body is null
                ? (null, Refuse(StatusCodes.Status400BadRequest, "bad_request_body", "The request needs a JSON body."))
                : (body, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A body that is not JSON, or a request that does not say it is: the caller's mistake, answered as one.
            return (null, Refuse(StatusCodes.Status400BadRequest, "bad_request_body", "The body could not be read as JSON."));
        }
    }

    private static IResult NoAccount()
        => Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
