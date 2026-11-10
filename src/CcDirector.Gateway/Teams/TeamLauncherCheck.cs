using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE LAUNCHER'S OWN-MACHINE CHECK, as the launcher routes and the launcher stream ask it (devthrottle_internal#2829).
/// The team gate lets a launcher's register, heartbeat, goodbye and stream through
/// (<see cref="TeamCallerOwnership.IsLauncherOwnMachineRoute"/>) because the machine is not in what the gate can read;
/// each of them asks this before it writes anything. Inside a team's tenant the answer is the one ownership rule,
/// <see cref="TeamCallerOwnership.LauncherMachineRefusal"/>. A personal account is never refused here: its launcher is
/// exactly as it was.
///
/// Before this, the gate answered Unknown for every launcher route in a team - nothing resolved a launcher's machine to
/// a person - so a team Director's launcher could not register or connect, and that Director could not be restarted.
/// </summary>
public sealed class TeamLauncherCheck
{
    /// <summary>The one sentence a refused launcher is told. It names nothing about the team's computers.</summary>
    public const string Refusal =
        "Inside a team a launcher may register only for the computer its own Director was set up on, with that " +
        "Director's own key, and DevThrottle cannot show that this computer is that one. Nothing was registered.";

    private readonly Func<TenantId, bool> _isTeam;
    private readonly TeamCallerOwnership? _ownership;

    /// <param name="isTeam">Whether a tenant is a team's (and Teams is released on this Gateway).</param>
    /// <param name="ownership">The one ownership rule.</param>
    public TeamLauncherCheck(Func<TenantId, bool> isTeam, TeamCallerOwnership ownership)
    {
        _isTeam = isTeam ?? throw new ArgumentNullException(nameof(isTeam));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    private TeamLauncherCheck()
    {
        _isTeam = _ => false;
        _ownership = null;
    }

    /// <summary>
    /// A check for a Gateway, or a test host, that serves personal accounts only: no tenant is a team's, so nothing is
    /// refused here. Named, so a host that has teams cannot get it by forgetting an argument.
    /// </summary>
    public static TeamLauncherCheck PersonalAccountsOnly { get; } = new();

    /// <summary>
    /// Why the launcher request in <paramref name="tenant"/> may not act for <paramref name="machine"/>, or null when it
    /// may. Null for every personal tenant.
    /// </summary>
    public string? Refuse(TenantId tenant, DeviceCredentialIdentity? device, SessionCredentialIdentity? session,
        string? machine, string what)
    {
        if (!_isTeam(tenant))
            return null;
        return _ownership is null
            ? Refusal
            : _ownership.LauncherMachineRefusal(tenant, device, session, machine, what);
    }
}
