using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CcDirector.Gateway.Api;

/// <summary>
/// launcher-persistent-join: the ONE place a lifecycle command is pushed DOWN a launcher's persistent
/// stream. The machine relay routes go through <see cref="TrySendAsync"/> so the delivery decision is
/// uniform across verbs and cannot diverge. The launcher twin of <see cref="DirectorCommandRouter"/>.
///
/// The stream is the ONLY path to a launcher (remove-the-network-port mission, phase 6 - the launcher
/// listens on nothing). A null from the injected <paramref name="sendCommand"/> delegate therefore means
/// the command CANNOT BE DELIVERED right now - no hook wired, or no active connection for this
/// tenant+machine - and the caller turns that into a loud refusal
/// (<see cref="LauncherLifecycleRelay.RelayOutcomeKind.NotConnected"/>). It is never a cue to reach the
/// launcher another way; there is no other way. A non-null result - success OR a typed failure - is the
/// launcher's own answer and is authoritative.
/// </summary>
internal static class LauncherCommandRouter
{
    /// <summary>The signature of the "send a command down a launcher's stream" hook. The tenant scopes the
    /// connection lookup to the caller's own launcher (tenant, machine).</summary>
    public delegate Task<LauncherCommandResult?> SendLauncherCommandAsync(TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the CALLING TENANT's launcher stream. Returns the stream result, or null
    /// when the command could not be delivered (no hook, or the launcher is not stream-connected for this
    /// tenant+machine) - which the caller reports as a refusal, never routes around.
    /// </summary>
    public static async Task<LauncherCommandResult?> TrySendAsync(
        SendLauncherCommandAsync? sendCommand, TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct)
    {
        if (sendCommand is null)
            return null;

        var result = await sendCommand(tenant, machineName, command, ct);
        FileLog.Write($"[LauncherCommandRouter] {command.Verb} tenant={tenant.Value} machine={machineName}: {(result is null ? "no stream connection - undeliverable" : $"stream status={result.Status}")}");
        return result;
    }

    /// <summary>
    /// Push one command down one launcher connection and wait for its answer. The Gateway host's send hook calls
    /// this once it has found the connection.
    ///
    /// THE STREAM CAN GO WHILE THE COMMAND IS IN FLIGHT (issue #4423). SignalR answers a client result whose
    /// connection closed - or had already closed between the caller's lookup and the send - with an
    /// <see cref="IOException"/>. That is not a server fault: the launcher had the command and may have done it,
    /// and an update does exactly this when it restarts the launcher into the new version. It is thrown as
    /// <see cref="LauncherStreamLostException"/> so the relay can say what happened.
    /// </summary>
    public static async Task<LauncherCommandResult> InvokeDownStreamAsync(
        IHubContext<Streaming.LauncherHub> hub, string connectionId, string machineName, LauncherCommand command, CancellationToken ct)
    {
        try
        {
            return await hub.Clients.Client(connectionId).InvokeAsync<LauncherCommandResult>("Command", command, ct);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[LauncherCommandRouter] InvokeDownStreamAsync: machine={machineName}, verb={command.Verb}: the stream closed before the launcher answered: {ex.Message}");
            throw new LauncherStreamLostException(machineName, command.Verb, ex);
        }
    }
}

/// <summary>
/// The command was delivered down a launcher's stream, and that stream closed BEFORE the launcher answered, so
/// the answer is lost (issue #4423). It is not the same as an undeliverable command: the launcher had the
/// command, and may well have done it. The commonest cause is the command itself - an update restarts the
/// launcher into the new version, and the old process goes before it can reply.
///
/// Thrown by the Gateway host's send hook, which is the only place that can tell this from any other fault,
/// and turned into its own outcome by <see cref="LauncherLifecycleRelay"/>, so the reader is told what
/// happened rather than handed a server fault or a guess about the launcher's age.
/// </summary>
internal sealed class LauncherStreamLostException : Exception
{
    public LauncherStreamLostException(string machineName, string verb, Exception inner)
        : base($"the launcher on '{machineName}' closed its command stream before answering '{verb}'", inner)
    {
    }
}
