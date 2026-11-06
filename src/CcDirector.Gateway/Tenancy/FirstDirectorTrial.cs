using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// THE 7-DAY PRO TRIAL THAT STARTS AT AN ACCOUNT'S FIRST DIRECTOR (Website Black mission, devthrottle_internal
/// docs/missions/website-black-2026-11-05, owner decisions D3 and D4 of 5 November 2026: "Pro free for 7 days", and
/// the 7 days start "when their Director first connects", not at sign-up; D20: no card).
///
/// WHAT IT GRANTS, stated rather than implied. When this setting is ON, a hosted account that is NOT PAYING (no paid
/// entitlement, or the free plan) and that this Gateway has NEVER SEEN BEFORE (it holds no tenant) is granted a Pro
/// trial of <see cref="Length"/> at the moment its first Director enrols through the Director door
/// (<c>POST /devices/enroll-hosted</c>). The grant is the ledger's one writer,
/// <see cref="TrialRegistry.GrantIfFirstArrival"/>, so one trial per account, ever, still holds: a second Director, a
/// re-enrolment, or an enrolment after the 7 days have ended reads the same row and is granted nothing new.
///
/// WHAT IT NEVER GRANTS. Nothing through the Cockpit and phone door, nothing on a move between a person's own account
/// and a team, nothing to a team enrolment, nothing to a paying account, nothing to an account that already holds a
/// tenant (it reached the hosted Gateway before, so its first Director has already connected), and nothing at all
/// while the setting is OFF. A self-hosted Gateway never reads the setting.
///
/// OFF BY DEFAULT, AND OFF UNTIL THE FLIP. The owner allowed ONE Gateway release for this, with the setting off
/// (5 November 2026: "Yes, you can go ahead and make a gateway release."). Turning it on is part of the Website Black
/// flip: one line in the repository's Dockerfile, <c>ENV CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR=1</c>, reviewed and
/// deployed like any other change. The deploy writes no application setting of its own, so the image is the one place
/// a hosted setting is stated (the same reason as <see cref="PreLaunchGrace"/>).
///
/// A VALUE IT CANNOT READ STOPS THE GATEWAY. "1" is on; unset or blank is off; "0" is off. Anything else ("true",
/// "yes", "on", a typo) refuses to start, because reading a mistyped "on" as off would silently withhold a trial the
/// website promises, and reading a mistyped "off" as on would hand out paid product nobody decided to give.
/// </summary>
/// <param name="Enabled">True when the grant is on.</param>
public sealed record FirstDirectorTrial(bool Enabled)
{
    /// <summary>The environment variable that turns the grant on.</summary>
    public const string EnvVar = "CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR";

    /// <summary>
    /// How long the trial runs: 7 days, the number the website states ("7 days of Pro, free"). A constant, not a
    /// setting, for the reason <see cref="TrialRegistry.TrialLength"/> is: the page and the grant must not be able to
    /// disagree. Rows already in the ledger keep the instants they were written with; this length is used only for a
    /// NEW grant.
    /// </summary>
    public static readonly TimeSpan Length = TimeSpan.FromDays(7);

    /// <summary>The grant off: what every Gateway is unless the setting says otherwise.</summary>
    public static FirstDirectorTrial Off { get; } = new(false);

    /// <summary>The setting as this process's environment states it. Read once, when the host is built.</summary>
    public static FirstDirectorTrial FromEnvironment()
    {
        var setting = Parse(Environment.GetEnvironmentVariable(EnvVar));
        FileLog.Write($"[FirstDirectorTrial] FromEnvironment: {EnvVar}={(setting.Enabled ? "on" : "off")}");
        return setting;
    }

    /// <summary>The setting one value states. Pure, so it is tested directly.</summary>
    public static FirstDirectorTrial Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Off;

        var trimmed = value.Trim();
        if (trimmed == "1")
            return new FirstDirectorTrial(true);
        if (trimmed == "0")
            return Off;

        throw new InvalidOperationException(
            $"The setting {EnvVar} is set to '{value}', which is neither 1 (on) nor 0 (off). Correct the value, or " +
            "remove the setting to leave the grant off. The Gateway will not start on a value it cannot read, because " +
            "guessing would either withhold the 7-day Pro trial the website promises or hand out Pro nobody decided to give.");
    }
}
