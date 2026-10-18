using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// EVERY WORD A LICENSE ALARM IS SHOWN WITH (License Switching phase 6), folded once here: the red banner's lines on
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

    /// <summary>The line above the alarms when more than one is open; null for none or one.</summary>
    public static string? Summary(int count) => count > 1 ? $"{count} license problems need you." : null;

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
            }).ToList(),
        };
    }

    /// <summary>
    /// The sentence on the push of one alarm that opened: the machine's name and the kind of problem, and nothing a
    /// check wrote about the account. A push shows on a locked screen and is held by the browser vendor's push service,
    /// so it never carries an account email address; the Licenses page, read over the signed-in connection, does.
    /// </summary>
    public static string PushBody(string machineName, LicenseAlarmKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return machineName.Length == 0
            ? $"{kind.Title}. Open Licenses to see what to do."
            : $"{machineName}: {kind.Title}. Open Licenses to see what to do.";
    }

    /// <summary>The Licenses log's row for an alarm that opened.</summary>
    public static string OpenedLogMessage(string problem, string action) => $"Alarm opened. {Line(problem, action)}";

    /// <summary>The Licenses log's row for an alarm that opened again within the hold-down, and told nobody.</summary>
    public static string HeldLogMessage(string problem, string action)
        => $"Alarm opened again within {HoldDownWords} of closing, so no email or push was sent. {Line(problem, action)}";

    /// <summary>The Licenses log's row for an alarm that closed by itself.</summary>
    public static string ClosedLogMessage(string problem) => $"Alarm closed, because this is no longer true: {problem}";

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
            subject = alarm.MachineName.Length == 0
                ? "DevThrottle license alarm: a problem with your licenses needs you"
                : $"DevThrottle license alarm: a problem on {alarm.MachineName} needs you";
            body.Append(CultureInfo.InvariantCulture, $"{alarm.Problem}\r\n\r\n{alarm.Action}\r\n\r\n");
        }
        else
        {
            subject = $"DevThrottle license alarm: {alarms.Count} problems with your licenses need you";
            body.Append(CultureInfo.InvariantCulture, $"{alarms.Count} problems with your licenses need you. One line for each:\r\n\r\n");
            foreach (var alarm in alarms)
                body.Append(CultureInfo.InvariantCulture, $"- {Line(alarm.Problem, alarm.Action)}\r\n");
            body.Append("\r\n");
        }
        body.Append("Each problem is shown in red at the top of every page of the Cockpit until it is fixed, and on its Licenses page.\r\n");
        body.Append("\r\nThis is the only email for ");
        body.Append(alarms.Count == 1 ? "this problem" : "these problems");
        body.Append(CultureInfo.InvariantCulture, $". Another is sent only if a problem is fixed and then comes back more than {HoldDownWords} later.");
        return new LicenseOwnerEmail(accounts, subject, body.ToString());
    }

    /// <summary>The hold-down in words, as the banner, the log and the email say it.</summary>
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
