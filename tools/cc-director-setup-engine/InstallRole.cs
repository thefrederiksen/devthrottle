namespace CcDirector.Setup.Engine;

/// <summary>
/// The up-front install type. Gateway is a SUPERSET of Workstation: it installs
/// everything a workstation does, plus the always-on Gateway + Cockpit service.
/// There is exactly one Gateway on a tailnet; most machines are Workstations.
/// </summary>
public enum InstallRole
{
    Workstation,
    Gateway,
}
