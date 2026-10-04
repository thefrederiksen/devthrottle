using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The hosted Gateway's team routes for screen D3 (devthrottle_internal#2311), over one
/// <see cref="GatewayAccountEnrollRunner"/>: listing signs the person in in the browser and keeps the account
/// token in memory for the move that follows, which is why one instance serves one visit to the Team tab.
/// </summary>
public sealed class HostedDirectorTeamService : IDirectorTeamService
{
    private readonly GatewayAccountEnrollRunner _runner;

    /// <param name="runner">The runner whose sign-in and token the list and the move share; null uses a real one.</param>
    public HostedDirectorTeamService(GatewayAccountEnrollRunner? runner = null)
    {
        _runner = runner ?? new GatewayAccountEnrollRunner();
    }

    /// <inheritdoc />
    public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct)
        => _runner.SignInAndListHostedTeamsAsync(ct);

    /// <inheritdoc />
    public Task<OperationResult<string>> MoveAsync(string directorId, string? teamId, CancellationToken ct)
        => _runner.MoveHostedDirectorAsync(directorId, teamId, ct);
}
