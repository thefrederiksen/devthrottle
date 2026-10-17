using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE LICENSES ROUTES (the License Switching mission): what the Licenses page of the Cockpit reads.
///
/// <list type="bullet">
/// <item><c>GET /gateway/licenses</c> - every Claude account and every machine.</item>
/// <item><c>GET /gateway/licenses/forecast</c> - when each account is expected to reach the switching threshold, the
/// next expected switch and its target, and which account each machine moves to next.</item>
/// <item><c>GET /gateway/licenses/log</c> - the switch log, newest first, one page at a time.</item>
/// <item><c>PUT /gateway/licenses/accounts/renewal</c> - set or clear the date an account's subscription is billed
/// again. A write, and the owner's own: it is not in the session key guard's list.</item>
/// </list>
///
/// Every route answers for the CALLER'S OWN account and no other: the tenant comes from the request's credential,
/// never from the path, the query or a body. A request with no account bound is refused; an account whose machines
/// have never reported is answered with an empty list and a sentence saying so, not with an error.
///
/// THE CLIENT IS DUMB (CLAUDE.md rule 7): every verdict in these answers is folded by
/// <see cref="LicenseDisplayFold"/>. No route here takes or returns a sign-in key - the Gateway holds none.
/// </summary>
public static class LicenseEndpoints
{
    /// <summary>How many log rows a page holds when the caller does not say.</summary>
    public const int DefaultLogPageSize = 50;

    /// <summary>The most log rows one page may hold.</summary>
    public const int MaxLogPageSize = 200;

    private static readonly IReadOnlyList<LicenseExhaustion> NoExhaustions = Array.Empty<LicenseExhaustion>();

    /// <param name="approvers">Who approves each account's sign-ins; the overview says it per account.</param>
    /// <param name="signIns">The sign-ins started from the Cockpit; the overview lists the ones a machine has not finished.</param>
    /// <param name="isConnected">Whether that account's Director, by its identifier, has a stream connection now.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, LicenseUsageHistory history, LicenseStore store, Func<DateTime> nowUtc,
        LicenseSwitchService switches, Func<TenantId?> tenantInScope,
        LicenseApproverStore approvers, LicenseSignInLog signIns, Func<TenantId, string, bool> isConnected)
    {
        // THE TWO WRITES ARE FOR A SIGNED-IN PERSON ONLY (the Tech Lead's ruling on the review of pull request 3936,
        // finding 1): the owner's own phone or browser, and nothing else. A session key is refused, raised or not, and
        // so are a Director's own device key and the shared machine token - the same one rule the Fleet Manager's
        // owner-only routes use. The three reads stay open to all of them.
        static IResult? OwnerOnly(HttpContext ctx, string what)
        {
            FleetManagerOwnerDevice.Require(ctx, what,
                $"a session may not {what}; that is the owner's own action, from the Licenses page of the Cockpit",
                "LicenseEndpoints", out var refusal);
            return refusal;
        }

        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(tenantInScope);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nowUtc);
        ArgumentNullException.ThrowIfNull(approvers);
        ArgumentNullException.ThrowIfNull(signIns);
        ArgumentNullException.ThrowIfNull(isConnected);

        app.MapGet(LicenseRoutes.Overview, (HttpContext ctx) => Overview(ctx, resolveTenant, snapshots, nowUtc, approvers, signIns, isConnected));
        app.MapGet(LicenseRoutes.Forecast, (HttpContext ctx) => Forecast(ctx, resolveTenant, snapshots, history, nowUtc));
        app.MapGet(LicenseRoutes.Log, (HttpContext ctx) => Log(ctx, resolveTenant, store, nowUtc));
        // The answer is executed here, by hand: a lambda that takes only the HttpContext is mapped as a plain request
        // delegate, and a result it merely RETURNED would be dropped - every call would answer 200 with no body.
        // WHO IS ASKING, AND HOW, IS JUDGED BEFORE A BYTE OF THE BODY IS READ: a caller that is not the owner's own
        // device is refused without its body being read at all, and so is a body not sent as application/json.
        app.MapPut(LicenseRoutes.AccountRenewal, async (HttpContext ctx) =>
        {
            const string what = "set the renewal date of a subscription";
            if ((OwnerOnly(ctx, what) ?? NotJson(ctx)) is { } refusedBeforeTheBody)
            {
                await refusedBeforeTheBody.ExecuteAsync(ctx);
                return;
            }
            var (body, renewsOnGiven) = ParseRenewalBody(await ReadBodyTextAsync(ctx));
            await SetRenewalDate(ctx, c => OwnerOnly(c, what), resolveTenant, store, nowUtc, body, renewsOnGiven).ExecuteAsync(ctx);
        });
        app.MapPost(LicenseRoutes.SwitchNow, async (HttpContext ctx) =>
        {
            const string what = "switch the machines of a subscription to another one";
            if ((OwnerOnly(ctx, what) ?? NotJson(ctx)) is { } refusedBeforeTheBody)
            {
                await refusedBeforeTheBody.ExecuteAsync(ctx);
                return;
            }
            await (await SwitchNowAsync(ctx, c => OwnerOnly(c, what), resolveTenant, tenantInScope, switches, store,
                ParseBody<LicenseSwitchNowRequest>(await ReadBodyTextAsync(ctx)))).ExecuteAsync(ctx);
        });
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseRoutes.Overview}, GET {LicenseRoutes.Forecast}, GET {LicenseRoutes.Log}, PUT {LicenseRoutes.AccountRenewal}, POST {LicenseRoutes.SwitchNow}");
    }

    internal static IResult Overview(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, Func<DateTime> nowUtc,
        LicenseApproverStore approvers, LicenseSignInLog signIns, Func<TenantId, string, bool> isConnected)
    {
        FileLog.Write("[LicenseEndpoints] GET overview");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            // The page does not judge the open "no account has room" events, so none are passed.
            var picture = snapshots.ReadPicture(tenant, nowUtc(), NoExhaustions);
            var response = LicenseDisplayFold.Overview(picture);
            // Who approves each account's sign-ins, and where a sign-in can be started now (contract section 8).
            LicenseSignInFold.Apply(response, picture.Overview.Machines, approvers.List(tenant), signIns.ListOpen(tenant),
                directorId => isConnected(tenant, directorId));
            FileLog.Write($"[LicenseEndpoints] GET overview: tenant={tenant.ToLogString()}, accounts={response.Accounts.Count}, machines={response.Machines.Count}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Forecast(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseSnapshotSource snapshots, LicenseUsageHistory history, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET forecast");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var now = nowUtc();
            var picture = snapshots.ReadPicture(tenant, now, NoExhaustions);
            var response = LicenseDisplayFold.Forecast(picture, history.Read(tenant, now));
            FileLog.Write($"[LicenseEndpoints] GET forecast: tenant={tenant.ToLogString()}, accounts={response.Accounts.Count}, nextSwitch={(response.NextSwitch is null ? "none" : "one")}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses/forecast FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Map <c>GET /gateway/licenses/alarms</c>: the license alarms open now, with the banner's finished words, for the
    /// caller's own account. Read by the shell of every Cockpit page and of the phone app. A read only: no route raises,
    /// clears or changes an alarm - only the Gateway's own checks do (<see cref="LicenseAlarmService.RaiseAsync"/>).
    /// </summary>
    public static void MapAlarms(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, LicenseAlarmService alarms,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(alarms);
        ArgumentNullException.ThrowIfNull(nowUtc);
        app.MapGet(LicenseRoutes.Alarms, (HttpContext ctx) => Alarms(ctx, resolveTenant, alarms, nowUtc));
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseRoutes.Alarms}");
    }

    internal static IResult Alarms(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicenseAlarmService alarms,
        Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET alarms");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var response = LicenseAlarmFold.Banner(alarms.ListOpen(tenant), nowUtc());
            FileLog.Write($"[LicenseEndpoints] GET alarms: tenant={tenant.ToLogString()}, open={response.Count}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses/alarms FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Log(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, LicenseStore store, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET log");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();

            var limit = DefaultLogPageSize;
            if (ctx.Request.Query.TryGetValue("limit", out var limitText)
                && (!int.TryParse(limitText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLogPageSize))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_limit", $"limit is a whole number from 1 to {MaxLogPageSize}.");

            var now = nowUtc();
            var cursorText = ctx.Request.Query.TryGetValue("cursor", out var given) ? given.ToString() : null;
            var cursor = new LogCursor(now, 0);
            if (!string.IsNullOrEmpty(cursorText) && !LogCursor.TryParse(cursorText, out cursor))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_cursor",
                    "cursor is the nextCursor of the page before this one, exactly as it was given.");

            // One more row than the page holds says whether an older page exists, without a second query.
            var rows = store.ListLogPage(tenant, cursor.UntilUtc, cursor.Skip, limit + 1);
            var hasMore = rows.Count > limit;
            var entries = LicenseDisplayFold.LogEntries(rows.Take(limit).ToList(), store.ListMachineNames(tenant));
            var empty = string.IsNullOrEmpty(cursorText) && entries.Count == 0;
            FileLog.Write($"[LicenseEndpoints] GET log: tenant={tenant.ToLogString()}, entries={entries.Count}, hasMore={hasMore}");
            return Results.Json(new LicenseLogResponse
            {
                AsOfUtc = now,
                IsEmpty = empty,
                EmptyText = empty ? LicenseDisplayFold.EmptyLogText : null,
                Entries = entries,
                HasMore = hasMore,
                NextCursor = hasMore ? new LogCursor(cursor.UntilUtc, cursor.Skip + limit).ToString() : null,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses/log FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// <c>PUT /gateway/licenses/accounts/renewal</c>: set, or with <c>"renewsOn": null</c> clear, the date one
    /// account's SUBSCRIPTION is billed again. The account is one of the caller's own DevThrottle account - the tenant
    /// comes from the credential, and a Claude account that tenant's machines have never reported is answered 404, so
    /// one DevThrottle account can neither set nor learn of another's. The date is "yyyy-MM-dd" and nothing else: no
    /// time, no zone, and no other spelling is guessed at. <paramref name="body"/> is null when the request carried
    /// no JSON object that could be read. <paramref name="renewsOnGiven"/> says the body NAMED <c>renewsOn</c>: only an
    /// explicit null clears the date, so a body that leaves the field out - a misspelled name, most likely - is
    /// refused and the stored date is left as it was. <paramref name="ownerOnly"/> answers the refusal for a caller
    /// that is not the owner's own signed-in phone or browser, and null for one that is.
    /// </summary>
    internal static IResult SetRenewalDate(HttpContext ctx, Func<HttpContext, IResult?> ownerOnly,
        Func<HttpContext, TenantId?> resolveTenant, LicenseStore store, Func<DateTime> nowUtc,
        LicenseRenewalDateRequest? body, bool renewsOnGiven)
    {
        FileLog.Write("[LicenseEndpoints] PUT renewal date");
        try
        {
            if (ownerOnly(ctx) is { } notTheOwner)
                return notTheOwner;
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (body is null || !renewsOnGiven)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body",
                    "The body is a JSON object with accountEmail and renewsOn. renewsOn must be given: a date, or null to clear it.");
            if (string.IsNullOrWhiteSpace(body.AccountEmail))
                return Refuse(StatusCodes.Status400BadRequest, "account_required", "accountEmail is required.");
            if (NotAnAccountEmail(body.AccountEmail))
                return InvalidAccount("accountEmail");

            DateOnly? renewsOn = null;
            if (body.RenewsOn is not null)
            {
                if (!DateOnly.TryParseExact(body.RenewsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return Refuse(StatusCodes.Status400BadRequest, "invalid_date", "renewsOn is a date written yyyy-MM-dd, or null to clear it.");
                renewsOn = date;
            }

            if (!store.SetRenewalDate(tenant, body.AccountEmail, renewsOn))
                return Refuse(StatusCodes.Status404NotFound, "unknown_account", "No machine of this DevThrottle account holds that Claude account.");

            FileLog.Write($"[LicenseEndpoints] PUT renewal date: tenant={tenant.ToLogString()}, set={(renewsOn is null ? "cleared" : "a date")}");
            return Results.Json(new LicenseRenewalDateResponse
            {
                AccountEmail = LicenseStore.NormalizeEmail(body.AccountEmail),
                RenewsOn = renewsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                RenewsText = LicenseDisplayFold.RenewsText(renewsOn, nowUtc()),
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] PUT /gateway/licenses/accounts/renewal FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// POST /gateway/licenses/switch-now: move every machine on one account of the caller's own DevThrottle account to
    /// another, now. The account is the tenant of the credential, never anything in the body. It answers when every
    /// machine has answered or its 60-second wait has ended. Accepted is 200 with each machine's switch as its log row
    /// stands; refused is 409 with the refusal word and a finished sentence, and nothing was written or sent.
    /// </summary>
    internal static async Task<IResult> SwitchNowAsync(HttpContext ctx, Func<HttpContext, IResult?> ownerOnly,
        Func<HttpContext, TenantId?> resolveTenant, Func<TenantId?> tenantInScope, LicenseSwitchService switches,
        LicenseStore store, LicenseSwitchNowRequest? body)
    {
        FileLog.Write("[LicenseEndpoints] POST switch-now");
        try
        {
            if (ownerOnly(ctx) is { } notTheOwner)
                return notTheOwner;
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "The body is a JSON object with accountEmail and, to name a target, targetAccountEmail.");
            if (string.IsNullOrWhiteSpace(body.AccountEmail))
                return Refuse(StatusCodes.Status400BadRequest, "account_required", "accountEmail is required: the account to switch away from.");
            if (NotAnAccountEmail(body.AccountEmail))
                return InvalidAccount("accountEmail");
            if (body.TargetAccountEmail is { } named && NotAnAccountEmail(named))
                return InvalidAccount("targetAccountEmail");
            // The switch is written for the caller's account and SENT in the scope in effect. If the two were ever
            // not the same, rows would be written and nothing sent - so that is refused here, before anything is.
            if (tenantInScope() is not { } scope || scope != tenant)
                throw new InvalidOperationException("Switch now was called outside the scope of the caller's own account; nothing was written or sent.");

            var result = await switches.SwitchNowAsync(tenant, body.AccountEmail, body.TargetAccountEmail, ctx.RequestAborted);
            var machines = store.ListMachineNames(tenant);
            if (!result.Accepted)
            {
                FileLog.Write($"[LicenseEndpoints] POST switch-now: tenant={tenant.ToLogString()}, refused={result.RefusalCode}");
                return Refuse(StatusCodes.Status409Conflict, result.RefusalCode!, LicenseDisplayFold.SwitchNowRefused(result, machines));
            }

            var rows = result.Switches
                .Select(s => store.FindSwitch(tenant, s.SwitchId)
                    ?? throw new InvalidOperationException($"Switch {s.SwitchId} was sent, and its row is gone."))
                .ToList();
            FileLog.Write($"[LicenseEndpoints] POST switch-now: tenant={tenant.ToLogString()}, switches={rows.Count}, skipped={result.Skipped.Count}");
            return Results.Json(LicenseDisplayFold.SwitchNowAccepted(result, rows, machines));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] POST /gateway/licenses/switch-now FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The longest an account email may be: the width of the column it is stored in.</summary>
    public const int MaxAccountEmailLength = 320;

    // True for text that cannot be an account email of the store: longer than its column, or holding a character
    // that is not text to be read - a control character, a line or paragraph separator, or a format character such
    // as the one that reverses the direction of what follows. Such text is refused before it is looked up, logged or
    // said back in a sentence: a line break in it would otherwise write what looks like a second line of the log.
    internal static bool NotAnAccountEmail(string text)
        => text.Length > MaxAccountEmailLength || text.Any(NotReadable);

    private static bool NotReadable(char c) => LicenseApproverStore.NotReadable(c);

    private static IResult InvalidAccount(string field)
        => Refuse(StatusCodes.Status400BadRequest, "invalid_account",
            $"{field} is at most {MaxAccountEmailLength} characters and holds no control, format or line-breaking character.");

    /// <summary>The refusal for a body that was not sent as <c>application/json</c>, or null when it was. A page of
    /// another site can make a browser send text/plain without asking first; it cannot make it send this.</summary>
    internal static IResult? NotJson(HttpContext ctx)
        => ctx.Request.HasJsonContentType()
            ? null
            : Refuse(StatusCodes.Status400BadRequest, "invalid_body", "The body must be sent as application/json.");

    private static readonly System.Text.Json.JsonSerializerOptions BodyOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    // The text of the request's body. Reading it is the caller's own request; what it holds is judged by the parsers.
    private static async Task<string> ReadBodyTextAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }

    /// <summary>The body as T, or null when it is not a JSON object of that shape. A body that cannot be read is the
    /// caller's mistake and is answered 400 by the handler; it is not an error of the Gateway.</summary>
    internal static T? ParseBody<T>(string json) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, BodyOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The body of the renewal route, and whether it NAMED <c>renewsOn</c> (whatever the capitals, as every
    /// other field name is read). The typed body alone cannot tell a field left out from one given as null.</summary>
    internal static (LicenseRenewalDateRequest? Body, bool RenewsOnGiven) ParseRenewalBody(string json)
    {
        var body = ParseBody<LicenseRenewalDateRequest>(json);
        if (body is null)
            return (null, false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var given = document.RootElement.EnumerateObject()
            .Any(p => string.Equals(p.Name, "renewsOn", StringComparison.OrdinalIgnoreCase));
        return (body, given);
    }

    /// <summary>
    /// Where a walk through the log stands: the moment the walk began and how many rows it has taken. The log only
    /// grows and a row's time never changes, so every page of one walk reads the rows written up to that one moment and
    /// a row written during the walk cannot push an old row onto two pages or off both. Written as
    /// "<c>ticks</c>.<c>rows taken</c>"; the page passes it back as it was given and never reads it.
    /// </summary>
    internal readonly record struct LogCursor(DateTime UntilUtc, int Skip)
    {
        public override string ToString()
            => UntilUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "." + Skip.ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string text, out LogCursor cursor)
        {
            cursor = default;
            var parts = text.Split('.');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var skip)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            cursor = new LogCursor(new DateTime(ticks, DateTimeKind.Utc), skip);
            return true;
        }
    }

    private static IResult NoAccount()
        => Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
