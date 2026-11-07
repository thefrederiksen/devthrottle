using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE OWNER'S 82% TEST SWITCH (mission "Licenses - Always Ready, Loud When Not", the owner's ruling of 7 November 2026).
/// It makes ONE machine's current license read as <see cref="Percent"/> of its weekly limit, so the owner can see the
/// yellow cannot-switch warning on a real machine, sign in to the license it recommends, and watch the warning clear
/// within a minute. It is the owner's own test tool and nothing else turns it on.
///
/// WHAT IT CHANGES. Every read of the license picture (<see cref="LicenseSnapshotSource"/>) - the minute check, the
/// Licenses page, the forecast - sees the weekly use of the license that machine is on raised to <see cref="Percent"/>.
/// It only ever RAISES a number that is lower: a license really above it reads as it is, so a real switch is never hidden.
/// <see cref="Percent"/> is under the switching threshold, so it switches nothing. A license's use is one number, so every
/// machine on that same license reads it too.
///
/// HOW IT IS TURNED OFF. The DELETE route. It is a tenant setting (<see cref="TenantSettingKeys.LicenseTestUsageMachine"/>),
/// so a Gateway that restarts keeps it on, and the GET route says whether it is on and for which machine.
/// </summary>
public sealed class LicenseTestUsage
{
    /// <summary>The weekly use the machine's license reads as while the switch is on.</summary>
    public const double Percent = 82.0;

    private readonly TenantSettingsStore _settings;

    public LicenseTestUsage(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The machine the switch is on for, or null when it is off. A stored value that is not a machine identifier
    /// is a hand-edited setting: it is reported and read as off.</summary>
    public Guid? MachineFor(TenantId tenant)
    {
        var stored = _settings.Get(tenant, TenantSettingKeys.LicenseTestUsageMachine);
        if (stored is null)
            return null;
        if (Guid.TryParse(stored, out var id))
            return id;
        FileLog.Write($"[LicenseTestUsage] MachineFor: tenant={tenant.ToLogString()}, the stored machine is not a machine identifier");
        return null;
    }

    /// <summary>Turn the switch on for <paramref name="machineId"/>, which must be on the Licenses page. Null when it is
    /// on; else the sentence saying why not.</summary>
    public string? TurnOn(TenantId tenant, Guid machineId, IReadOnlyList<LicenseMachineView> machines, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machines);
        FileLog.Write($"[LicenseTestUsage] TurnOn: tenant={tenant.ToLogString()}, machine={machineId:N}");
        if (machines.All(m => m.MachineId != machineId))
            return "That machine is not on the Licenses page.";
        _settings.Set(tenant, TenantSettingKeys.LicenseTestUsageMachine, machineId.ToString("N"), nowUtc);
        return null;
    }

    /// <summary>Turn the switch off. True when it was on.</summary>
    public bool TurnOff(TenantId tenant)
    {
        var removed = _settings.Remove(tenant, TenantSettingKeys.LicenseTestUsageMachine);
        FileLog.Write($"[LicenseTestUsage] TurnOff: tenant={tenant.ToLogString()}, wasOn={removed}");
        return removed;
    }

    /// <summary>
    /// <paramref name="overview"/> with the weekly use of the license <paramref name="machineId"/> is on raised to
    /// <see cref="Percent"/>, when it is lower. Unchanged when the switch is off, the machine is not in the overview or is on
    /// no license, or the license has no reading. Pure.
    /// </summary>
    public static LicenseOverview Apply(LicenseOverview overview, Guid? machineId)
    {
        ArgumentNullException.ThrowIfNull(overview);
        if (machineId is not { } id || overview.Machines.FirstOrDefault(m => m.MachineId == id)?.ActiveAccountEmail is not { } active)
            return overview;
        var account = LicenseStore.NormalizeEmail(active);
        var accounts = overview.Accounts.Select(a =>
                LicenseStore.NormalizeEmail(a.AccountEmail) == account && a.Freshest is { } freshest
                                                                       && !(freshest.SevenDayUtilization >= Percent)
                    ? a with { Freshest = freshest with { SevenDayUtilization = Percent } }
                    : a)
            .ToList();
        return overview with { Accounts = accounts };
    }

    /// <summary>The words of the GET answer.</summary>
    public static string Text(Guid? machineId, IReadOnlyList<LicenseMachineView> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);
        if (machineId is not { } id)
            return "The test switch is off.";
        var machine = machines.FirstOrDefault(m => m.MachineId == id);
        var percent = Percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        return machine is null
            ? $"The test switch is on for a machine no longer on the Licenses page, so it changes nothing. Turn it off."
            : $"The test switch is on: the license {LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser)} is on reads as at least "
              + $"{percent} of its weekly limit. Turn it off when the test is done.";
    }
}
