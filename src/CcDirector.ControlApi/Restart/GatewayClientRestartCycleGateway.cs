using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Restart;

/// <summary>The real Gateway seam for the cycle, over the Director's existing outbound client.</summary>
public sealed class GatewayClientRestartCycleGateway : IRestartCycleGateway
{
    private readonly GatewayClient _client;

    /// <summary>Create the seam over a connected Gateway client.</summary>
    /// <param name="client">The Director's Gateway client.</param>
    public GatewayClientRestartCycleGateway(GatewayClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task<MachineRestartCapabilityDto> CheckCapabilityAsync(string machine, CancellationToken ct)
        => _client.GetRestartCapabilityAsync(machine, ct);

    /// <inheritdoc />
    public async Task<LauncherRestartAnswer> AskOwnLauncherRestartOnlyIfEmptyAsync(string machine, string? exePath, CancellationToken ct)
    {
        var (status, body) = await _client.RequestOwnRestartOnlyIfEmptyAsync(machine, exePath, ct);
        return new LauncherRestartAnswer(status, body);
    }

    /// <inheritdoc />
    public Task ReportAsync(string machine, string requestId, DirectorRestartProgressReport report, CancellationToken ct)
        => _client.ReportRestartProgressAsync(machine, requestId, report, ct);
}
