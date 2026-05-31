using CcDirector.Gateway.Contracts;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// The shape of <c>GET /sessions?envelope=true</c> on the Gateway: every session it
/// could aggregate across all registered Directors, plus a list of the Directors it
/// could NOT reach. We render both -- sessions in the rail, unreachable Directors as
/// inline placeholders -- so a dead Director is visible, never silently dropped.
/// </summary>
public sealed class SessionsEnvelope
{
    public List<SessionDto> Sessions { get; set; } = new();
    public List<MachineErrorDto> MachineErrors { get; set; } = new();
}
