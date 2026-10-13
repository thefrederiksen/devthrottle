using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// Sends one license command that addresses no session down a Director's stream and waits at most
/// <paramref name="timeout"/> for its answer. Null means nothing was sent (the Director is not connected);
/// <see cref="DirectorCommandStatus.Timeout"/> and <see cref="DirectorCommandStatus.TunnelDropped"/> are made by the
/// Gateway. It reads the tenant scope in effect, so it is always called inside the tenant's scope (contract section 6
/// rule 4).
/// </summary>
/// <param name="machineName">The machine the Director runs on, for the sentence a timeout is described with.</param>
internal delegate Task<DirectorCommandResult?> LicenseCommandSender(string directorId, string verb, object payload,
    TimeSpan timeout, string machineName, CancellationToken ct);

internal static class LicenseCommandSenders
{
    /// <summary>The sender the Gateway runs with: <see cref="DirectorCommandRouter.TrySendAsync"/> over the host's one
    /// way down a Director's stream, so the wait and its three outcomes are the same as for every other command.</summary>
    public static LicenseCommandSender Over(DirectorCommandRouter.SendDirectorCommandAsync send)
    {
        ArgumentNullException.ThrowIfNull(send);
        return (directorId, verb, payload, timeout, machineName, ct) =>
            DirectorCommandRouter.TrySendAsync(send, directorId, verb, "", payload, ct, timeout, machineName);
    }
}
