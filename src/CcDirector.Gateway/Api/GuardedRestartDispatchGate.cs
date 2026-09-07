using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE LAST GATE BEFORE A GUARDED RESTART IS SENT, bound to the connection that will receive it. Issue
/// #2725 (restart epic, Phase 6), from an independent review of the first draft.
///
/// WHY A CHECK AT THE CAPABILITY ROUTE IS NOT ENOUGH. The capability answer is folded from the launcher
/// connection the Gateway held at the moment of asking. A launcher connection can be superseded - the
/// registry lets a new stream from the same machine replace the old one - so a restart asked for a
/// moment after a "yes" can be delivered to a DIFFERENT launcher, and if that one predates the
/// only-if-empty condition it restarts a busy Director and answers a bare success. The relay turns that
/// bare success into a failure, but by then the sessions are gone: detection, not prevention.
///
/// So the rule is applied HERE, on the same read that yields the connection identifier the command goes
/// down. <see cref="LauncherConnectionRegistry.GetActiveConnection"/> returns the connection and what it
/// declared as one value; this refuses when that declaration does not carry the guard, and the send
/// uses that same connection. There is no window between the check and the dispatch in which a
/// different launcher can be substituted, because there is no second read.
///
/// == AGAINST THE DECLARED TOKEN. A launcher that declared nothing, a launcher that declared a list
/// without the token, and a launcher this Gateway has never heard from are all refusals. A guarded
/// restart is sent only to a launcher that said, itself, that it honours the guard.
/// </summary>
internal static class GuardedRestartDispatchGate
{
    /// <summary>The reason this command must NOT be sent down this connection, or null when it may.</summary>
    /// <param name="command">The command about to be sent.</param>
    /// <param name="connection">The connection it would go down, with its declaration - or null when there
    /// is none, which the caller reports as unreachable before ever reaching here.</param>
    public static string? Refusal(LauncherCommand command, LauncherStreamConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.OnlyIfEmpty) return null;
        if (connection is null)
            return "a restart that must happen only if the Director is empty was not sent: no launcher "
                   + "stream is connected for that machine.";

        var declared = connection.Declaration?.Commands;
        var honours = declared is not null
                      && declared.Any(c => string.Equals(c?.Trim(), LauncherCapabilities.DirectorRestartOnlyIfEmpty,
                          StringComparison.OrdinalIgnoreCase));
        if (honours) return null;

        return "a restart that must happen only if the Director is empty was NOT sent: the launcher "
               + "currently connected for that machine "
               + (connection.Declaration is null
                   ? "declared no capabilities on joining, so it predates the only-if-empty condition and would "
                     + "ignore it, restart a busy Director and report success."
                   : $"declares [{string.Join(", ", declared ?? new List<string>())}] and not "
                     + $"{LauncherCapabilities.DirectorRestartOnlyIfEmpty}, so it would ignore the condition.")
               + " Nothing was done. Update the launcher on that machine, then ask again.";
    }
}
