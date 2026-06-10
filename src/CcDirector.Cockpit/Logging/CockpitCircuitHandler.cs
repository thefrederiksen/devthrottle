using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CcDirector.Cockpit.Logging;

/// <summary>
/// Logs Blazor Server circuit lifecycle events to the persisted Cockpit log (issue #199, scope C:
/// "Blazor circuit reconnect events"). A circuit is the server-side state behind one browser tab's
/// SignalR connection; when the WebSocket drops and the browser reconnects, the same circuit's
/// connection goes Down then Up. Persisting these makes a flaky remote connection - the root of the
/// blank-terminal incidents - visible in the log instead of guesswork.
/// </summary>
public sealed class CockpitCircuitHandler(ILogger<CockpitCircuitHandler> logger) : CircuitHandler
{
    private readonly ILogger<CockpitCircuitHandler> _logger = logger;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit opened id={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Circuit connection DOWN id={CircuitId} (browser disconnected; reconnect pending)", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit connection UP id={CircuitId} (browser (re)connected)", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Circuit closed id={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }
}
