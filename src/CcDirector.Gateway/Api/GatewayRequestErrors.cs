using System.Text.Json;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// One request's error scope: the correlation id the Gateway gives it, the route it reached, and the sink its errors go
/// to. Held on the request's items and on its flow of execution, so a failure line logged anywhere while the request is
/// served is stored with the same id the client is answered with.
/// </summary>
internal sealed class GatewayRequestScope(string correlationId, string method, GatewayErrorSink sink)
{
    public string CorrelationId { get; } = correlationId;
    public string Method { get; } = method;
    public GatewayErrorSink Sink { get; } = sink;

    /// <summary>"VERB /route/{pattern}" once routing has matched an endpoint, else null.</summary>
    public string? Route { get; internal set; }
}

/// <summary>
/// Every Gateway error answer carries a CORRELATION ID, and the error behind it is in the error store under the same id
/// (the Error Logging mission, step 2, issue #3675). The client that shows the error can report that id, so the
/// phone's red box, the Gateway's row and the Director's rows read as one incident.
///
///   <see cref="Use"/>           - FIRST in the pipeline. Mints the id, and stamps it on every answer with a status of
///                                 400 or more as the <see cref="HeaderName"/> header - whichever code wrote the answer.
///   <see cref="UseRouteNote"/>  - after routing. Records "VERB /route/{pattern}" for the rows the request causes. The
///                                 PATTERN, not the path, so the fingerprint keeps routes apart and folds nothing a
///                                 caller typed into a path.
///   <see cref="AnswerUnhandledAsync"/> - the one answer to an exception no endpoint caught: a stored row (status 500,
///                                 the route as surface, the exception type, the id) and a 500 whose body carries the id.
///   <see cref="RecordRefusal"/> - a request the Gateway refused because the session's Director is offline or the
///                                 delivery failed, stored with the session and the id.
///
/// THE ID IS ALWAYS THE GATEWAY'S. A client cannot choose it, so no client can make its own report look like it shares
/// an incident with somebody else's request.
/// </summary>
internal static class GatewayRequestErrors
{
    public const string HeaderName = "X-Correlation-Id";
    internal const string ItemKey = "cc.errors.Scope";
    internal const string UnhandledErrorCode = "internal_error";

    private static readonly AsyncLocal<GatewayRequestScope?> CurrentScope = new();

    /// <summary>The scope of the request being served on this flow of execution, or null outside one.</summary>
    public static GatewayRequestScope? Current => CurrentScope.Value;

    /// <summary>The scope <see cref="Use"/> put on this request, or null when the pipeline has none.</summary>
    public static GatewayRequestScope? From(HttpContext ctx)
        => ctx.Items.TryGetValue(ItemKey, out var value) ? value as GatewayRequestScope : null;

    /// <summary>A fresh id: 32 lower-case hexadecimal characters.</summary>
    internal static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>The scope middleware. Install it FIRST, so even the readiness gate's refusal carries an id.</summary>
    public static void Use(IApplicationBuilder app, GatewayErrorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        app.Use((ctx, next) => RunAsync(ctx, next, sink));
        FileLog.Write($"[GatewayRequestErrors] Use: every answer of 400 or more carries {HeaderName}");
    }

    internal static async Task RunAsync(HttpContext ctx, Func<Task> next, GatewayErrorSink sink)
    {
        var scope = new GatewayRequestScope(NewCorrelationId(), ctx.Request.Method.ToUpperInvariant(), sink);
        ctx.Items[ItemKey] = scope;
        ctx.Response.OnStarting(() =>
        {
            if (ctx.Response.StatusCode >= 400)
                ctx.Response.Headers[HeaderName] = scope.CorrelationId;
            return Task.CompletedTask;
        });
        // Restored when this method returns: an async method's changes to its flow of execution do not leak out.
        CurrentScope.Value = scope;
        await next();
    }

    /// <summary>The route note. Install it right after <c>UseRouting</c>.</summary>
    public static void UseRouteNote(IApplicationBuilder app)
    {
        app.Use((ctx, next) =>
        {
            if (From(ctx) is { } scope) scope.Route = RouteOf(ctx, scope.Method);
            return next();
        });
    }

    /// <summary>"VERB /pattern" when an endpoint matched; else "VERB /path" with any secret in the path redacted.</summary>
    internal static string RouteOf(HttpContext ctx, string method)
    {
        if (ctx.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } pattern })
            return $"{method} {(pattern.StartsWith('/') ? pattern : "/" + pattern)}";
        return $"{method} {TeamInvitationEndpoints.RedactForLog(ctx.Request.Path.Value is { Length: > 0 } path ? path : "/")}";
    }

    /// <summary>
    /// Answer an exception no endpoint caught: one stored row and a 500 that carries the id. Requires the scope - a
    /// pipeline without <see cref="Use"/> is a wiring defect, and is said so rather than answered without an id.
    /// </summary>
    public static async Task AnswerUnhandledAsync(HttpContext ctx, Exception ex, Func<string>? describeRequestForLog = null)
    {
        var scope = From(ctx)
            ?? throw new InvalidOperationException(
                $"an unhandled exception reached the error boundary on a request with no error scope; install {nameof(GatewayRequestErrors)}.{nameof(Use)} first in the pipeline",
                ex);
        var route = scope.Route ?? RouteOf(ctx, scope.Method);

        // THE CLIENT LEFT (step 2 review, observation 3). A cancellation while the request itself is aborted is the
        // phone or the browser going away mid-request, not a fault of ours: it is logged, in the reporter's own tag so it
        // is never observed as an error, and no row is stored. There is nobody left to answer.
        if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
        {
            FileLog.Write($"{CcDirector.Core.ErrorReports.ErrorLine.ReporterTag} gateway: {route} abandoned by the client (correlation {scope.CorrelationId}); not stored");
            return;
        }

        var type = ex.GetType().FullName ?? ex.GetType().Name;

        scope.Sink.Report(new GatewayError(
            Source: "GatewayHost",
            Kind: "unhandled",
            // "VERB /route ..." first: the shape the fingerprint keeps apart by route.
            Message: $"{route} answered 500: {type}: {ex.Message}",
            ExceptionType: type,
            Stack: ex.ToString(),
            Surface: route,
            CorrelationId: scope.CorrelationId,
            HttpStatus: StatusCodes.Status500InternalServerError,
            ErrorCode: UnhandledErrorCode,
            SessionId: ctx.GetRouteValue("sid") as string));

        // Full detail in the process log, for whoever reads the live log. It is the row above in other words, so it is
        // kept out of the sink - otherwise a message that happens to carry a FAILED would store the one failure twice.
        using (GatewayErrorSink.SuppressObservation())
        {
            Console.Error.WriteLine($"[GatewayHost] pipeline exception (correlation {scope.CorrelationId}): {ex}");
            FileLog.Write($"[GatewayHost] unhandled exception (correlation {scope.CorrelationId}): {describeRequestForLog?.Invoke() ?? route}: {ex}");
        }

        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        // A generic body: no exception type or message reaches a remote client. The id is how they are joined.
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "internal error", correlation_id = scope.CorrelationId }));
    }

    /// <summary>
    /// Store a refusal of a request about one session - a prompt the Gateway could not deliver because the session's
    /// Director is offline or stale (503, 404), did not answer in time (504), or did not take it (502). Any answer with a
    /// status of 500 or more, or a 404, is recorded; anything else is not a refusal of this kind and is left alone.
    ///
    /// The row's words are the route, the status and the answer's machine code - NEVER the answer's sentence and never
    /// the request body. A prompt's words must not reach a report by any path (the owner's ruling of 8 October), and an
    /// answer's sentence can carry a Director's free text.
    /// </summary>
    public static void RecordRefusal(HttpContext ctx, IResult result, string account, string sessionId, string action)
    {
        if (result is not IStatusCodeHttpResult { StatusCode: { } status }) return;
        if (status < 500 && status != StatusCodes.Status404NotFound) return;

        var scope = From(ctx);
        if (scope is null)
        {
            // Only a pipeline built without the scope middleware - a test that maps a route on its own - reaches this.
            // GatewayHost installs the middleware first (GatewayRequestErrorsHostTests proves it on a real host).
            FileLog.Write($"[GatewayRequestErrors] RecordRefusal: no error scope on this request, so the {status} answer for session {sessionId} is in this log only");
            return;
        }

        var route = scope.Route ?? RouteOf(ctx, scope.Method);
        var code = CodeOf(result);
        scope.Sink.Report(new GatewayError(
            Source: "GatewayEndpoints",
            Kind: "refused",
            Message: code is null ? $"{route} answered {status}" : $"{route} answered {status} ({code})",
            Account: account,
            Surface: route,
            Action: action,
            CorrelationId: scope.CorrelationId,
            HttpStatus: status,
            ErrorCode: code,
            SessionId: sessionId));
    }

    /// <summary>The answer's machine code - a <c>code</c> property on its body - or null.</summary>
    private static string? CodeOf(IResult result)
    {
        if (result is not IValueHttpResult { Value: { } value }) return null;
        var body = JsonSerializer.SerializeToElement(value, value.GetType());
        return body.ValueKind == JsonValueKind.Object
               && body.TryGetProperty("code", out var code)
               && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
    }
}
