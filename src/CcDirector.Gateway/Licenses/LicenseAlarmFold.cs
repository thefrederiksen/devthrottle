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

    /// <summary>The banner: one line for each open alarm, oldest first, and a summary line when there are several.</summary>
    public static LicenseAlarmsResponse Banner(IReadOnlyList<LicenseAlarmEntity> open, DateTime nowUtc)
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
                Text = Line(a.Problem, a.Action),
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
        body.Append(". Another is sent only if a problem is fixed and then comes back.");
        return new LicenseOwnerEmail(accounts, subject, body.ToString());
    }
}
