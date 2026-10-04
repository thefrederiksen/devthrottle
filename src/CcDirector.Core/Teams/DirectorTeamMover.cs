using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// The hosted Gateway's side of changing a Director's team (screen D3). Implemented over HTTP by the setup
/// engine; faked in tests.
/// </summary>
public interface IDirectorTeamService
{
    /// <summary>The teams the signed-in person may move this Director to.</summary>
    Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct);

    /// <summary>
    /// Move the Director <paramref name="deviceId"/> to <paramref name="teamId"/> (null = the personal account).
    /// The Gateway refuses while the Director has a session registered; on success it revokes the old key and
    /// returns the new one.
    /// </summary>
    Task<OperationResult<string>> MoveAsync(string deviceId, string? teamId, CancellationToken ct);
}

/// <summary>
/// Changes the team this Director works for (screen D3) - only with every session closed, so no session ever
/// starts on one team and finishes on another.
///
/// The rule is enforced HERE, not only by a disabled button: <see cref="MoveAsync"/> counts the running
/// sessions itself at the moment of the move and refuses without calling the Gateway when there are any. The
/// Gateway refuses too; the Director does not lean on that.
///
/// On success the new key and the new team are stored together, the key first: a Director holding the new
/// team's name with the old, revoked key would show one team and fail to reach any.
/// </summary>
public sealed class DirectorTeamMover
{
    private readonly IDirectorTeamService _service;
    private readonly Func<int> _runningSessionCount;
    private readonly Action<string> _persistKey;
    private readonly Action<DirectorTeam> _persistTeam;

    /// <param name="service">The Gateway's move route.</param>
    /// <param name="runningSessionCount">How many sessions this Director holds right now.</param>
    /// <param name="persistKey">Stores the new device key for this Director.</param>
    /// <param name="persistTeam">Stores the new team for this Director.</param>
    public DirectorTeamMover(
        IDirectorTeamService service,
        Func<int> runningSessionCount,
        Action<string> persistKey,
        Action<DirectorTeam> persistTeam)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _runningSessionCount = runningSessionCount ?? throw new ArgumentNullException(nameof(runningSessionCount));
        _persistKey = persistKey ?? throw new ArgumentNullException(nameof(persistKey));
        _persistTeam = persistTeam ?? throw new ArgumentNullException(nameof(persistTeam));
    }

    /// <summary>
    /// Why a move cannot happen right now - "Close the 2 running sessions first." - or null when it can.
    /// </summary>
    public static string? RefusalFor(int runningSessions)
    {
        if (runningSessions < 0)
            throw new ArgumentOutOfRangeException(nameof(runningSessions), "A session count cannot be negative.");
        return runningSessions switch
        {
            0 => null,
            1 => "Close the 1 running session first.",
            _ => $"Close the {runningSessions} running sessions first.",
        };
    }

    /// <summary>
    /// Move this Director to <paramref name="target"/>. Refused, with nothing sent and nothing stored, while any
    /// session is running. Otherwise the Gateway's answer decides: its refusal is returned as it is; its new key
    /// and the new team are stored.
    /// </summary>
    public async Task<OperationResult<DirectorTeam>> MoveAsync(string deviceId, TeamChoice target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(target);

        var running = _runningSessionCount();
        FileLog.Write($"[DirectorTeamMover] MoveAsync: device={deviceId}, target={(target.IsPersonal ? "personal account" : "team " + target.TeamId)}, runningSessions={running}");

        var refusal = RefusalFor(running);
        if (refusal is not null)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync: REFUSED by the Director - {running} session(s) running");
            return OperationResult<DirectorTeam>.Fail(refusal);
        }

        var moved = await _service.MoveAsync(deviceId, target.TeamId, ct).ConfigureAwait(false);
        if (!moved.Success)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync: the Gateway refused: {moved.ErrorMessage}");
            return OperationResult<DirectorTeam>.Fail(moved.ErrorMessage ?? "The Gateway refused the move.");
        }
        if (string.IsNullOrWhiteSpace(moved.Value))
            throw new InvalidOperationException("The Gateway accepted the move but returned no device key.");

        var team = target.ToTeam();
        _persistKey(moved.Value);
        _persistTeam(team);
        FileLog.Write("[DirectorTeamMover] MoveAsync: moved; new device key and team stored");
        return OperationResult<DirectorTeam>.Ok(team);
    }
}
