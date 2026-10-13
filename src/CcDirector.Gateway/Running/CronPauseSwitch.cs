using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Running;

/// <summary>
/// PAUSE EVERYTHING (issue #3910): one switch per account that stops the scheduler firing, kept in the account's
/// settings so a Gateway restart keeps it.
///
/// While it is on the cron engine starts nothing - not a due schedule on the sweep, not a run-now. It is a switch
/// BESIDE the schedules, never a write to them: each schedule keeps its own enabled flag untouched, so resuming is
/// one switch flipped back and every schedule is exactly as active or as paused as it was before. A session a
/// schedule already started keeps running; pausing stops firing, not sessions. Factory triggers are not schedules
/// and are not affected.
///
/// The stored value is the moment the pause began (round-trip UTC); no row means not paused. A stored value that
/// cannot be read is an error, never a quiet "not paused" - the sweep then fires nothing for that account, which
/// is the safe direction for a switch whose whole purpose is to stop the machine.
/// </summary>
public sealed class CronPauseSwitch
{
    /// <summary>The banner shown wherever schedules are read while everything is paused.</summary>
    public const string PausedBanner =
        "Everything is paused. No schedule will start until you resume. Each schedule keeps its own setting, " +
        "and sessions already running are not stopped.";

    public const string PauseLabel = "Pause everything";
    public const string ResumeLabel = "Resume everything";

    public const string PauseTitle =
        "Stop every schedule from starting. Each schedule keeps its own setting, and running sessions are not stopped.";
    public const string ResumeTitle =
        "Let schedules start again, each exactly as it was. Runs missed while paused are not started late.";

    private readonly TenantSettingsStore _settings;

    public CronPauseSwitch(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Whether the account's scheduler is paused.</summary>
    public bool IsPaused(TenantId tenant) => PausedSinceUtc(tenant) is not null;

    /// <summary>When the account's pause began, or null when it is not paused.</summary>
    public DateTime? PausedSinceUtc(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.SchedulesPaused);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
            throw new InvalidOperationException(
                $"The pause-everything switch recorded for account {tenant.ToLogString()} is not a time: '{raw}'.");
        return since.ToUniversalTime();
    }

    /// <summary>Pause the account's scheduler. Pausing an account that is already paused keeps the moment it began.</summary>
    public void Pause(TenantId tenant, DateTime nowUtc)
    {
        if (PausedSinceUtc(tenant) is { } since)
        {
            FileLog.Write($"[CronPauseSwitch] Pause: account={tenant.ToLogString()} already paused since {since:o}");
            return;
        }
        var stamp = nowUtc.ToUniversalTime();
        _settings.Set(tenant, TenantSettingKeys.SchedulesPaused, stamp.ToString("o", CultureInfo.InvariantCulture), stamp);
        FileLog.Write($"[CronPauseSwitch] Pause: account={tenant.ToLogString()} paused at {stamp:o}");
    }

    /// <summary>Resume the account's scheduler. Returns false when it was not paused.</summary>
    public bool Resume(TenantId tenant)
    {
        var removed = _settings.Remove(tenant, TenantSettingKeys.SchedulesPaused);
        FileLog.Write($"[CronPauseSwitch] Resume: account={tenant.ToLogString()} wasPaused={removed}");
        return removed;
    }

    /// <summary>The switch as every client shows it: the state, and the finished words for the banner and the control.</summary>
    public CronPauseDto Describe(TenantId tenant)
    {
        var since = PausedSinceUtc(tenant);
        var paused = since is not null;
        return new CronPauseDto
        {
            Paused = paused,
            SinceUtc = since,
            Banner = paused ? PausedBanner : null,
            ActionLabel = paused ? ResumeLabel : PauseLabel,
            ActionTitle = paused ? ResumeTitle : PauseTitle,
        };
    }
}
