using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// EVERY WORD A LICENSE ALARM IS SHOWN WITH (License Switching phase 6), folded once here: the banner's lines on
/// every Cockpit page and on the phone, the push on a locked screen, the owner email, and the Licenses log's rows. The
/// clients render these verbatim (CLAUDE.md rule 7). Pure: the same alarms give the same words.
/// </summary>
public static class LicenseAlarmFold
{
    /// <summary>The words of the banner's link to the Licenses page.</summary>
    public const string LinkText = "Open Licenses";

    /// <summary>The push's title, on every alarm.</summary>
    public const string PushTitle = "License problem";

    /// <summary>The banner's line for one alarm: what is wrong, then the one thing to do.</summary>
    public static string Line(string problem, string action) => $"{problem} {action}";

    /// <summary>The frame's single summary line. Full details belong on the Licenses page.</summary>
    public static string? Summary(int count) => count > 0 ? $"{count} license problem{(count == 1 ? "" : "s")}" : null;

    /// <summary>
    /// The sentence added to the banner's line of an alarm that has come and gone, held back from the owner's email and
    /// phones by <see cref="LicenseAlarmService.HoldDown"/>; null for an alarm that opened once.
    /// </summary>
    public static string? CameBack(int timesInARow) => timesInARow < 2
        ? null
        : $"It has come and gone: this is the {Ordinal(timesInARow)} time in a row, each within {HoldDownWords} of the last, and you were told only the first time.";

    /// <summary>The banner: one line for each open alarm, oldest first, and a summary line when there are several.</summary>
    /// <param name="timesInARow">How many times in a row each alarm has opened (<see cref="LicenseAlarmService.TimesInARow"/>);
    /// an alarm not in it opened once.</param>
    public static LicenseAlarmsResponse Banner(IReadOnlyList<LicenseAlarmEntity> open, DateTime nowUtc,
        IReadOnlyDictionary<Guid, int>? timesInARow = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        return new LicenseAlarmsResponse
        {
            AsOfUtc = nowUtc,
            Count = open.Count,
            Tone = BannerTone(open),
            Summary = Summary(open.Count),
            LinkText = open.Count == 0 ? null : LinkText,
            Alarms = open.Select(a => new LicenseAlarmDto
            {
                AlarmId = a.Id.ToString("N"),
                Kind = a.Kind,
                MachineName = a.MachineName.Length == 0 ? null : a.MachineName,
                AccountEmail = a.AccountEmail.Length == 0 ? null : a.AccountEmail,
                Text = CameBack(timesInARow is not null && timesInARow.TryGetValue(a.Id, out var times) ? times : 1) is { } cameBack
                    ? $"{Line(a.Problem, a.Action)} {cameBack}"
                    : Line(a.Problem, a.Action),
                OpenedAtUtc = DateTime.SpecifyKind(a.OpenedAtUtc, DateTimeKind.Utc),
                Tone = Tone(a),
            }).ToList(),
        };
    }

    /// <summary>
    /// The sentence on the push of one alarm that opened: the machine's name and the kind of problem, and nothing a
    /// check wrote about the account. A push shows on a locked screen and is held by the browser vendor's push service,
    /// so it never carries an account email address; the Licenses page, read over the signed-in connection, does.
    /// </summary>
    public static string PushBody(string machineName, LicenseAlarmKind kind, bool isRepeat = false)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var still = isRepeat ? "Still: " : "";
        return machineName.Length == 0
            ? $"{still}{kind.Title}. Open Licenses to see what to do."
            : $"{still}{machineName}: {kind.Title}. Open Licenses to see what to do.";
    }

    /// <summary>The banner tone of one open alarm: its kind's, as stored on its row (<see cref="LicenseAlarmTones"/>).
    /// A row stored before tones existed has the column's default, <see cref="LicenseAlarmTones.Warning"/>.</summary>
    public static string Tone(LicenseAlarmEntity alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return alarm.Tone switch
        {
            LicenseAlarmTones.Warning or LicenseAlarmTones.Critical => alarm.Tone,
            _ => throw new InvalidOperationException($"License alarm {alarm.Id:N} carries the tone '{alarm.Tone}', which is not an alarm tone."),
        };
    }

    /// <summary>
    /// The banner's tone, one word for every screen: <see cref="LicenseAlarmTones.Critical"/> (red) when any open alarm is
    /// critical - only a machine stuck above the switching threshold - else <see cref="LicenseAlarmTones.Warning"/>
    /// (yellow). Null when nothing is open. The owner, 6 November 2026: "a warning should be yellow, not red. It doesn't go
    /// red until it goes above 97%."
    /// </summary>
    public static string? BannerTone(IReadOnlyList<LicenseAlarmEntity> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (open.Count == 0)
            return null;
        return open.Any(a => Tone(a) == LicenseAlarmTones.Critical) ? LicenseAlarmTones.Critical : LicenseAlarmTones.Warning;
    }

    /// <summary>The Licenses log's row for an alarm that opened.</summary>
    public static string OpenedLogMessage(string problem, string action) => $"Alarm opened. {Line(problem, action)}";

    /// <summary>The Licenses log's row for an alarm that opened again within the hold-down, and told nobody.</summary>
    public static string HeldLogMessage(string problem, string action)
        => $"Alarm opened again within {HoldDownWords} of closing, so no email or push was sent. {Line(problem, action)}";

    /// <summary>The Licenses log's row for an alarm that closed by itself.</summary>
    public static string ClosedLogMessage(string problem) => $"Alarm closed, because this is no longer true: {problem}";

    /// <summary>The Licenses log row of an hourly repeat (<see cref="LicenseAlarmService.RepeatEvery"/>).</summary>
    public static string RepeatedLogMessage(string problem, string action, bool pushHeldForTheNight) => pushHeldForTheNight
        ? $"Still not fixed an hour on: the owner was emailed again; no push, because it is night and the account is not past {NightPushPercentWords}. {Line(problem, action)}"
        : $"Still not fixed an hour on: the owner was told again. {Line(problem, action)}";

    private static string NightPushPercentWords => $"{LicenseCannotSwitchAlarm.LoudAtNightAbovePercent.ToString("0", CultureInfo.InvariantCulture)}%";

    /// <summary>The ONE owner email for every alarm whose email is due in one check, oldest first.</summary>
    public static LicenseOwnerEmail Email(IReadOnlyList<LicenseAlarmEntity> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        if (alarms.Count == 0)
            throw new ArgumentException("An alarm email is about at least one alarm.", nameof(alarms));
        var accounts = string.Join(", ", alarms.Select(a => a.AccountEmail).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal));
        var body = new StringBuilder();
        string subject;
        if (alarms.Count == 1)
        {
            var alarm = alarms[0];
            subject = alarm.Subject is { Length: > 0 } own
                ? $"DevThrottle: {own}"
                : alarm.MachineName.Length == 0
                    ? "DevThrottle license alarm: a problem with your licenses needs you"
                    : $"DevThrottle license alarm: a problem on {alarm.MachineName} needs you";
            body.Append(CultureInfo.InvariantCulture, $"{alarm.Problem}\r\n\r\n{alarm.Action}\r\n\r\n");
        }
        else
        {
            // The most urgent subject named first: the subject of a repeating warning names the machine and the hours left.
            subject = alarms.FirstOrDefault(a => a.Subject is { Length: > 0 }) is { } first
                ? $"DevThrottle: {first.Subject} (and {alarms.Count - 1} more license problem{(alarms.Count == 2 ? "" : "s")})"
                : $"DevThrottle license alarm: {alarms.Count} problems with your licenses need you";
            body.Append(CultureInfo.InvariantCulture, $"{alarms.Count} problems with your licenses need you. One line for each:\r\n\r\n");
            foreach (var alarm in alarms)
                body.Append(CultureInfo.InvariantCulture, $"- {Line(alarm.Problem, alarm.Action)}\r\n");
            body.Append("\r\n");
        }
        body.Append("Open the Licenses page for the details of each problem. The Cockpit shows their count until they are fixed.\r\n");
        body.Append("\r\n");
        body.Append(alarms.Any(a => RepeatingKinds.Contains(a.Kind))
            ? "You are told again every hour until a machine that cannot switch can switch."
            : $"A problem that is fixed and comes back within {HoldDownWords} is not emailed again.");
        return new LicenseOwnerEmail(accounts, subject, body.ToString());
    }

    /// <summary>The hold-down in words, as the banner, the log and the email say it.</summary>
    // The kinds that tell the owner again every hour: the cannot-switch warning and the stranded alarm.
    private static readonly HashSet<string> RepeatingKinds = new(StringComparer.Ordinal)
    {
        LicenseCannotSwitchAlarm.Kind.Key, LicenseStrandedAlarm.Kind.Key,
    };

    private static string HoldDownWords => $"{LicenseAlarmService.HoldDown.TotalHours.ToString(CultureInfo.InvariantCulture)} hours";

    /// <summary>"second", "third" ... "tenth", then "11th", "21st", "22nd" and so on.</summary>
    internal static string Ordinal(int n)
    {
        string[] words = { "", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth" };
        if (n >= 1 && n < words.Length)
            return words[n];
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n.ToString(CultureInfo.InvariantCulture)}{suffix}";
    }
}
