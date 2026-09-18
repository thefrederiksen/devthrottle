using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The SINGLE resolve-then-create path for starting a session on a target MACHINE ("start a session
/// on another computer"). Resolves the machine to a runnable Director via
/// <see cref="IDirectorTargetResolver"/> (launching one through the launcher when none is running)
/// and creates the session on it over the Director tunnel.
/// Both the cron firing engine (<see cref="DirectorCronSessionStarter"/>) and the interactive
/// POST /machines/{machine}/sessions relay call this ONE method, so scheduled and on-demand spawns
/// route identically with no duplicated resolve/create logic.
///
/// Fail-fast and loud: when the machine is off / unreachable the resolver returns an Error and this
/// reports it as a failure - it NEVER falls back to a local spawn.
/// </summary>
public sealed class MachineSessionSpawner
{
    /// <summary>
    /// The create-session call. Production routes through <see cref="SessionVerbClient.CreateSessionAsync"/>,
    /// which is TUNNEL-ONLY: a Director that is not connected yields an error, never an HTTP dial. Tests
    /// inject a fake so the resolve-then-create decision is verified without a live Director. Delivery is by
    /// DirectorId over the tunnel; there is no endpoint (the old vestigial endpoint parameter, always blank
    /// in tunnel-only mode, was retired with issue #1727).
    /// </summary>
    public delegate Task<(bool ok, SessionDto? body, string? error)> CreateSessionDelegate(
        string directorId, NewSessionRequest req, CancellationToken ct);

    private readonly IDirectorTargetResolver _resolver;
    private readonly CreateSessionDelegate _create;

    /// <param name="resolver">Resolves the target machine to a Director, launching one on demand.</param>
    /// <param name="sendCommand">The send-a-command-down-the-stream hook.</param>
    internal MachineSessionSpawner(IDirectorTargetResolver resolver,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
        : this(resolver, (directorId, req, ct) =>
            SessionVerbClient.ForDirector(directorId, sendCommand).CreateSessionAsync(req, ct))
    {
    }

    /// <summary>Test seam: inject the resolver and a fake create call directly.</summary>
    internal MachineSessionSpawner(IDirectorTargetResolver resolver, CreateSessionDelegate create)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>
    /// Resolve <paramref name="machine"/> to a Director (launching one if none is running) and create the
    /// session described by <paramref name="req"/> on it. Returns <c>ok=false</c> with the resolver's or
    /// the Director's error - and NEVER a local fallback - when the machine cannot be resolved or the create
    /// fails. <c>directorId</c> is the resolved Director (for the cron run record); it is populated even on a
    /// failure the resolver could attribute to a Director.
    ///
    /// <paramref name="refuseDirector"/>, when given, is asked about the RESOLVED Director before anything is sent
    /// to it, and a non-null answer refuses the create with that sentence. It exists because a Director the
    /// launcher has only just started is not known until the resolver returns, and a caller that needs something
    /// of the Director (the Fleet Manager's own folder, for one) must be able to refuse before the create rather
    /// than read an older Director's unrelated error afterwards.
    /// </summary>
    public async Task<(bool ok, SessionDto? dto, string? error, string? directorId)> SpawnOnMachineAsync(
        string machine, NewSessionRequest req, CancellationToken ct, Func<string, string?>? refuseDirector = null)
    {
        if (req is null)
            throw new ArgumentNullException(nameof(req));

        // req.Director, when set, names ONE Director and pins the resolve to it (machine alone resolves
        // to the first Director on the machine, which on a machine running several named instances is a
        // coin toss). Passed through the same single resolve-then-create path rather than a second one,
        // so a targeted spawn keeps the tenant scoping, the mission stamping and the workflow seat it
        // already had - the only thing that changes is WHICH Director gets the create.
        var target = await _resolver.ResolveAsync(machine, req.Director, ct);
        // Tunnel-only: a resolved DirectorId IS the target - delivery is by id over the tunnel, so a
        // Director with a blank control endpoint is perfectly reachable. Fail only when the resolver
        // reported an error (machine off / unreachable / launch failed) or resolved no Director at all;
        // do NOT reject on an endpoint (that stale REST-era guard is what issue #1727 removed).
        if (!string.IsNullOrEmpty(target.Error) || string.IsNullOrEmpty(target.DirectorId))
        {
            var reason = target.Error ?? "the target machine has no registered director";
            FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync FAILED: machine={machine}, {reason}");
            return (false, null, reason, target.DirectorId);
        }

        if (refuseDirector?.Invoke(target.DirectorId) is { } refusal)
        {
            FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync REFUSED before the create: machine={machine}, director={target.DirectorId}: {refusal}");
            return (false, null, refusal, target.DirectorId);
        }

        FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync: machine={machine}, director={target.DirectorId}, repo={req.RepoPath}");

        var (ok, body, error) = await _create(target.DirectorId, req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync FAILED: machine={machine}, error={error}");
            return (false, null, error ?? "director did not return a session id", target.DirectorId);
        }

        FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync: started sid={body.SessionId}, director={target.DirectorId}");
        return (true, body, null, target.DirectorId);
    }
}
