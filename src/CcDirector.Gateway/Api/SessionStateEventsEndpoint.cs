using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>Streams resolved session-state transitions as server-sent events.</summary>
internal static class SessionStateEventsEndpoint
{
    internal static async Task StreamAsync(
        HttpContext context,
        string sessionId,
        TenantId tenant,
        PushedSessionStore? sessions,
        Action? afterSubscription = null)
    {
        FileLog.Write($"[SessionStateEventsEndpoint] StreamAsync: tenant={tenant.ToLogString()}, session={sessionId}");
        if (sessions is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "session state streaming is not enabled on this Gateway" },
                cancellationToken: context.RequestAborted);
            return;
        }

        var queue = System.Threading.Channels.Channel.CreateUnbounded<SessionDto>();
        void OnSessionChanged(TenantId changedTenant, string directorId, SessionDto changed)
        {
            if (changedTenant.Equals(tenant)
                && string.Equals(changed.SessionId, sessionId, StringComparison.Ordinal)
                && sessions.IsHoldersRow(tenant, directorId, sessionId))
                queue.Writer.TryWrite(changed);
        }

        // Subscribe first: a delta between the initial read and subscription would otherwise leave
        // the stream showing the stale row until another state change happened.
        sessions.SessionChanged += OnSessionChanged;
        afterSubscription?.Invoke();
        var initial = sessions.TryGetLastKnownSession(tenant, sessionId)?.Session;
        if (initial is null)
        {
            sessions.SessionChanged -= OnSessionChanged;
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "session not found" },
                cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.ContentType = GatewayResponseCompression.EventStreamMimeType;
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var cancellationToken = context.RequestAborted;
        var eventNumber = 0L;
        (string State, string Source, bool Fallback)? last = null;

        async Task WriteIfChangedAsync(SessionDto session)
        {
            var key = (session.ActivityState, session.ActivityStateSource, session.ActivityStateIsFallback);
            if (last == key) return;
            last = key;
            eventNumber++;
            var data = JsonSerializer.Serialize(new
            {
                sessionId = session.SessionId,
                state = session.ActivityState,
                source = session.ActivityStateSource,
                fallback = session.ActivityStateIsFallback,
            });
            await context.Response.WriteAsync($"id: {eventNumber}\nevent: state\ndata: {data}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        try
        {
            await WriteIfChangedAsync(initial);
            await foreach (var session in queue.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteIfChangedAsync(session);
                if (string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                    break;
            }
            FileLog.Write($"[SessionStateEventsEndpoint] StreamAsync: session={sessionId}, result=exited");
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[SessionStateEventsEndpoint] StreamAsync: session={sessionId}, result=disconnected");
        }
        finally
        {
            sessions.SessionChanged -= OnSessionChanged;
        }
    }
}
