using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CcDirector.ControlApi;

/// <summary>
/// The hub methods <see cref="GatewayStreamClient"/> calls that carry a session id up to the Gateway, named
/// one per method so the ORDER of those calls can be recorded and asserted (devthrottle_internal#2311,
/// review finding S2-F13). In a team the Gateway decides whose a session is from its key row, so the order
/// is a security property: no session id may reach the Gateway from this Director before its key has been
/// sent. Production uses <see cref="HubConnectionCalls"/>; tests use a recorder.
/// </summary>
internal interface IDirectorHubCalls
{
    /// <summary>True while the connection can carry a call.</summary>
    bool IsConnected { get; }

    Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello);
    Task RegisterSessionKeyAsync(SessionKeyRegistration registration);
    Task PushSnapshotAsync(long sequence, SessionDto[] sessions);
    Task PushDeltaAsync(long sequence, SessionDto session);
    Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct);
    Task RevokeSessionKeyAsync(string sessionId);
    Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories);
}

/// <summary>The production <see cref="IDirectorHubCalls"/>: each call is the hub method of the same name.</summary>
internal sealed class HubConnectionCalls : IDirectorHubCalls
{
    private readonly HubConnection _connection;

    public HubConnectionCalls(HubConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    // Invoked GENERICALLY so the Gateway's answer is read. A Gateway too old to return capabilities returns
    // nothing, which SignalR gives us as null - and null is the answer that matters.
    public Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello)
        => _connection.InvokeAsync<GatewayCapabilities?>("Hello", hello);

    public Task RegisterSessionKeyAsync(SessionKeyRegistration registration)
        => _connection.InvokeAsync("RegisterSessionKey", registration);

    public Task PushSnapshotAsync(long sequence, SessionDto[] sessions)
        => _connection.InvokeAsync("PushSnapshot", sequence, sessions);

    public Task PushDeltaAsync(long sequence, SessionDto session)
        => _connection.InvokeAsync("PushDelta", sequence, session);

    public Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct)
        => _connection.InvokeAsync<TurnWatermark?>("PushTurns", sequence, batch, ct);

    public Task RevokeSessionKeyAsync(string sessionId)
        => _connection.InvokeAsync("RevokeSessionKey", sessionId);

    public Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories)
        => _connection.InvokeAsync("PushRepoSnapshot", sequence, repositories);
}
