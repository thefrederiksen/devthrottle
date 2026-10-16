using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>A factory as the email names it: its title, and its seats' names by seat id.</summary>
/// <param name="Title">The factory's title from the registry, or its id when it is not registered.</param>
/// <param name="SeatNames">Each seat's name (a role word) by seat id.</param>
public sealed record FactoryEmailFactory(string Title, IReadOnlyDictionary<string, string> SeatNames);

/// <summary>Everything one factory email is composed from. The composer reads nothing else.</summary>
/// <param name="Slot">The slot being sent.</param>
/// <param name="Zone">The account's time zone.</param>
/// <param name="Problems">The account's OPEN problems, from the one problems fold.</param>
/// <param name="Days">Each factory's last day, from the same fold.</param>
/// <param name="Factories">The account's factories by id, for titles and seat names.</param>
/// <param name="BaseUrl">This Gateway's public base address, or null when it has no remotely reachable one.</param>
public sealed record FactoryEmailInput(
    FactoryEmailSlot Slot,
    TimeZoneInfo Zone,
    IReadOnlyList<CronRunProblemDto> Problems,
    IReadOnlyList<CronFactoryDayDto> Days,
    IReadOnlyDictionary<string, FactoryEmailFactory> Factories,
    string? BaseUrl);

/// <summary>A composed factory email, or the reason none is sent at this slot.</summary>
/// <param name="Send">True when this slot sends an email.</param>
/// <param name="NotSentReason">Why nothing is sent, in words, when <see cref="Send"/> is false.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Html">The body as email-safe HTML: inline styles, tables, no scripts, ASCII only.</param>
/// <param name="Text">The same body as plain text.</param>
/// <param name="ProblemCount">How many problems the email lists.</param>
/// <param name="Factories">The ids of the factories the email covers - every factory it names - so the factory page can
/// list the emails that speak about it.</param>
public sealed record FactoryEmail(bool Send, string? NotSentReason, string Subject, string Html, string Text, int ProblemCount,
    IReadOnlyList<string> Factories);

/// <summary>
/// THE FACTORY EMAIL (Factory Control, step 4), composed exactly to the mockups the owner approved on 10 October 2026
/// (<c>docs/qa/factory-control/morning-email/</c>). Pure: no store, no clock, no I/O - every fact arrives in
/// <see cref="FactoryEmailInput"/>, folded by the Gateway, and is rendered here verbatim.
///
/// THE OWNER'S RULES:
///   - 08:00, every day: the problems still not fixed, whenever they started (a problem fixed yesterday is not open, so
///     it is not here), then one line per factory - runs done, runs that did not run, seats left open.
///   - 16:00 and midnight: ONLY when the shift that just ended raised a NEW problem - one recorded since the last slot.
///     Older open problems are one line pointing to the earlier email, never repeated.
///   - Read-only: no buttons. Each factory with a problem links to its factory page, where the problem is handled.
///   - Role words only - a seat is named by its role, never a person.
/// </summary>
public static class FactoryEmailComposer
{
    private const string Ink = "#16181D";
    private const string Muted = "#5A616B";
    private const string Faint = "#8A909A";
    private const string Line = "#E6E8EC";
    private const string Page = "#F5F6F8";
    private const string Navy = "#0B1628";
    private const string Accent = "#0066B8";
    private const string Bad = "#B42318";
    private const string BadBack = "#FDF3F2";
    private const string Font = "font-family:'Segoe UI',Arial,sans-serif;";

    /// <summary>The heading over a problem whose schedule is in no factory.</summary>
    public const string NoFactoryTitle = "Not in a factory";

    /// <summary>Compose the email for <paramref name="input"/>'s slot.</summary>
    public static FactoryEmail Compose(FactoryEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Slot.Kind == FactoryEmailKinds.Morning ? ComposeMorning(input) : ComposeCheck(input);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 08:00

    private static FactoryEmail ComposeMorning(FactoryEmailInput input)
    {
        var problems = input.Problems.Where(p => p.State == CronRunProblemStates.Open).ToList();
        var days = input.Days;
        if (problems.Count == 0 && days.Count == 0)
            return NotSent("this account has no factory schedules and no open problems, so there is nothing to report");

        var local = Local(input.Slot.BoundaryUtc, input.Zone);
        var dayText = local.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        var subject = problems.Count == 0
            ? $"Factories: all clear - {dayText}"
            : $"Factories: {Count(problems.Count, "problem")} still open - {dayText}";

        var done = days.Sum(d => d.Done);
        var didNotRun = days.Sum(d => d.DidNotRun);
        // A timed start whose check found nothing reads "nothing to do" (Factory Definition, phase 5b) - never "did not
        // run". Said only when there was one, so an email with no checked starts reads exactly as it always has.
        var nothingToDo = days.Sum(d => d.NothingToDo);
        // A failed check, timed or interval, is counted the same way: said only when there was one.
        var checkFailed = days.Sum(d => d.CheckFailed);
        var summary = (nothingToDo == 0
            ? $"{Count(days.Count, "factory", "factories")}, {Count(done, "run")} done, {Count(didNotRun, "run")} did not run"
            : $"{Count(days.Count, "factory", "factories")}, {Count(done, "run")} done, {Count(nothingToDo, "run")} with nothing to do, {Count(didNotRun, "run")} did not run")
            + (checkFailed == 0 ? "." : $", {Count(checkFailed, "check")} failed.");
        var headline = problems.Count == 0 ? "All clear" : $"{Count(problems.Count, "problem")} still open";

        var html = new StringBuilder();
        Open(html, subject, $"{local:HH:mm} report - {dayText} - the last 24 hours");
        html.Append($"<tr><td style=\"padding:16px 18px 6px;{Font}\">\n");
        html.Append($"<div style=\"font-size:21px;font-weight:700;color:{(problems.Count == 0 ? Ink : Bad)};\">{E(headline)}</div>\n");
        html.Append($"<div style=\"font-size:13.5px;color:{Muted};padding-top:3px;\">{E(summary)}</div>\n");
        html.Append("</td></tr>\n");

        if (problems.Count > 0)
        {
            SectionHeading(html, "Still not fixed", "12px 18px 4px");
            ProblemCards(html, input, problems, showOpenFor: true);
        }

        if (days.Count > 0)
        {
            SectionHeading(html, "Last 24 hours", "20px 18px 4px");
            FactoryTable(html, input, days);
        }

        var footer = new StringBuilder();
        footer.Append("This email only reports. Each problem is handled on its factory page - ");
        footer.Append(input.BaseUrl is null ? "open DevThrottle, Factories. " : "open the factory name above. ");
        footer.Append("A problem leaves this email when the next run of the same job succeeds, or when it is resolved with a reason.");
        if (CronRunResultFold.AnyCheckProblem(problems))
            footer.Append(' ').Append(CronRunResultFold.CheckClearsSentence);
        Close(html, footer.ToString(), $"Sent at {local:HH:mm} {ZoneName(input.Zone)} time to the account that set up these factories.");

        var text = new StringBuilder();
        text.AppendLine(subject).AppendLine();
        text.AppendLine(headline).AppendLine(summary).AppendLine();
        if (problems.Count > 0)
        {
            text.AppendLine("STILL NOT FIXED");
            foreach (var p in problems) TextProblem(text, input, p, showOpenFor: true);
            text.AppendLine();
        }
        if (days.Count > 0)
        {
            // The two phase 5b columns are there only when a day has one, as in the HTML table.
            var columns = new List<(string Name, Func<CronFactoryDayDto, int> Of)> { ("done", d => d.Done) };
            if (nothingToDo > 0) columns.Add(("nothing to do", d => d.NothingToDo));
            columns.Add(("did not run", d => d.DidNotRun));
            if (checkFailed > 0) columns.Add(("check failed", d => d.CheckFailed));
            columns.Add(("left open", d => d.LeftOpen));
            text.AppendLine($"LAST 24 HOURS ({string.Join(" / ", columns.Select(c => c.Name))})");
            foreach (var d in OrderedDays(input, days))
                text.AppendLine($"{TitleOf(input, d.Factory)}: {string.Join(" / ", columns.Select(c => c.Of(d)))}");
            text.AppendLine();
        }
        text.AppendLine(footer.ToString());

        return new FactoryEmail(true, null, subject, html.ToString(), text.ToString(), problems.Count, Covered(problems, days));
    }

    // ------------------------------------------------------------------------------------------------------------
    // 16:00 and midnight

    private static FactoryEmail ComposeCheck(FactoryEmailInput input)
    {
        var slot = input.Slot;
        var open = input.Problems.Where(p => p.State == CronRunProblemStates.Open).ToList();
        var fresh = open.Where(p => IsNew(p, slot)).ToList();
        if (fresh.Count == 0)
            return NotSent($"the {slot.EndedShift.Name} shift raised no new problem");
        var older = open.Count(p => p.RaisedAtUtc <= slot.PreviousCutoffUtc);

        var boundary = Local(slot.BoundaryUtc, input.Zone);
        var shiftLocal = Local(slot.EndedShift.StartUtc, input.Zone);
        var dayText = shiftLocal.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        var midnight = boundary.Hour == 0;
        var checkName = midnight ? "Midnight check" : $"{boundary:HH:mm} check";
        var shiftHours = $"{shiftLocal:HH:mm} to {(midnight ? "24:00" : boundary.ToString("HH:mm", CultureInfo.InvariantCulture))}";
        var headline = $"{Count(fresh.Count, "new problem")} in the {slot.EndedShift.Name} shift";
        var subject = $"Factories: {headline} - {dayText}";

        var html = new StringBuilder();
        Open(html, subject, $"{checkName} - {dayText} - {slot.EndedShift.Name} shift, {shiftHours}");
        html.Append($"<tr><td style=\"padding:16px 18px 6px;{Font}\">\n");
        html.Append($"<div style=\"font-size:21px;font-weight:700;color:{Bad};\">{E(headline)}</div>\n");
        html.Append("</td></tr>\n");
        ProblemCards(html, input, fresh, showOpenFor: false, topPadding: "10px");

        string? olderLine = null;
        if (older > 0)
        {
            olderLine = midnight
                ? $"Also still open from before: {Count(older, "problem")}, listed in today's 08:00 report and 16:00 check."
                : $"Also still open from before: {Count(older, "problem")}, listed in this morning's 08:00 report.";
            html.Append($"<tr><td style=\"padding:14px 18px 16px;{Font}font-size:13.5px;color:{Muted};\">\n{E(olderLine)}\n</td></tr>\n");
        }
        else
        {
            html.Append("<tr><td style=\"height:14px;line-height:14px;font-size:0;\">&nbsp;</td></tr>\n");
        }

        var footer = fresh.Count == 1
            ? "This email only reports. Handle the problem on its factory page - "
            : "This email only reports. Handle each problem on its factory page - ";
        footer += input.BaseUrl is null ? "open DevThrottle, Factories." : "open the factory name above.";
        Close(html, footer, "The 16:00 and midnight checks are sent only when something went wrong.");

        var text = new StringBuilder();
        text.AppendLine(subject).AppendLine();
        text.AppendLine(headline).AppendLine();
        foreach (var p in fresh) TextProblem(text, input, p, showOpenFor: false);
        if (olderLine is not null) text.AppendLine().AppendLine(olderLine);
        text.AppendLine().AppendLine(footer);

        return new FactoryEmail(true, null, subject, html.ToString(), text.ToString(), fresh.Count,
            Covered(fresh, Array.Empty<CronFactoryDayDto>()));
    }

    /// <summary>A problem is NEW at a check when it was raised after the previous slot's cut-off and by this one's.</summary>
    public static bool IsNew(CronRunProblemDto problem, FactoryEmailSlot slot)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(slot);
        return problem.RaisedAtUtc > slot.PreviousCutoffUtc && problem.RaisedAtUtc <= slot.CutoffUtc;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Pieces

    private static FactoryEmail NotSent(string reason) => new(false, reason, "", "", "", 0, Array.Empty<string>());

    // Every factory an email names, by id, once each, sorted.
    private static IReadOnlyList<string> Covered(IEnumerable<CronRunProblemDto> problems, IEnumerable<CronFactoryDayDto> days) =>
        problems.Select(p => p.Factory).Concat(days.Select(d => d.Factory))
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static void Open(StringBuilder html, string subject, string headerLine)
    {
        html.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append($"<title>{E(subject)}</title>\n</head>\n");
        html.Append($"<body style=\"margin:0;padding:0;background:{Page};\">\n");
        html.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:{Page};\">\n");
        html.Append("<tr><td align=\"center\" style=\"padding:12px 8px;\">\n");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:560px;background:#FFFFFF;border-radius:10px;\">\n");
        html.Append($"<tr><td style=\"background:{Navy};border-radius:10px 10px 0 0;padding:16px 18px;{Font}\">\n");
        html.Append("<div style=\"color:#FFFFFF;font-weight:700;font-size:16px;\">DevThrottle factories</div>\n");
        html.Append($"<div style=\"color:#9AA7B8;font-size:12.5px;padding-top:2px;\">{E(headerLine)}</div>\n");
        html.Append("</td></tr>\n");
    }

    private static void Close(StringBuilder html, string footer, string lastLine)
    {
        html.Append($"<tr><td style=\"padding:14px 18px 18px;border-top:1px solid {Line};{Font}font-size:12.5px;color:{Muted};line-height:1.5;\">\n");
        html.Append(E(footer)).Append("\n<br>").Append(E(lastLine)).Append('\n');
        html.Append("</td></tr>\n</table>\n</td></tr>\n</table>\n</body>\n</html>\n");
    }

    private static void SectionHeading(StringBuilder html, string text, string padding) =>
        html.Append($"<tr><td style=\"padding:{padding};{Font}font-size:11.5px;font-weight:700;letter-spacing:0.6px;color:{Faint};text-transform:uppercase;\">\n{E(text)}\n</td></tr>\n");

    private static void ProblemCards(StringBuilder html, FactoryEmailInput input, IReadOnlyList<CronRunProblemDto> problems,
        bool showOpenFor, string topPadding = "0")
    {
        html.Append($"<tr><td style=\"padding:{topPadding} 18px 0;\">\n");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">\n");
        for (var i = 0; i < problems.Count; i++)
        {
            var p = problems[i];
            if (i > 0)
                html.Append("<tr><td style=\"height:8px;line-height:8px;font-size:0;\">&nbsp;</td></tr>\n");
            html.Append($"<tr><td style=\"padding:11px 12px;border-left:4px solid {Bad};background:{BadBack};border-radius:4px;{Font}\">\n");
            html.Append($"<div style=\"font-size:15px;font-weight:700;\">{FactoryName(input, p.Factory, Ink, linkWhenProblem: true)}</div>\n");
            html.Append($"<div style=\"font-size:13.5px;color:{Ink};padding-top:3px;\">{E(SeatLine(input, p))}</div>\n");
            html.Append($"<div style=\"font-size:13.5px;color:{Bad};padding-top:3px;font-weight:600;\">{E(p.Sentence)}</div>\n");
            html.Append($"<div style=\"font-size:12.5px;color:{Muted};padding-top:3px;\">{E(WhenLine(p, showOpenFor))}</div>\n");
            html.Append("</td></tr>\n");
        }
        html.Append("</table>\n</td></tr>\n");
    }

    private static void FactoryTable(StringBuilder html, FactoryEmailInput input, IReadOnlyList<CronFactoryDayDto> days)
    {
        html.Append("<tr><td style=\"padding:0 18px 6px;\">\n");
        html.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"{Font}\">\n");
        html.Append("<tr>\n");
        html.Append($"<td style=\"padding:6px 0;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\">Factory</td>\n");
        html.Append($"<td align=\"right\" style=\"padding:6px 0 6px 8px;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\" width=\"44\">Done</td>\n");
        // The column is there only when a check found nothing to do (phase 5b): every other email is unchanged.
        var showNothingToDo = days.Any(d => d.NothingToDo > 0);
        if (showNothingToDo)
            html.Append($"<td align=\"right\" style=\"padding:6px 0 6px 8px;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\" width=\"64\">Nothing to do</td>\n");
        html.Append($"<td align=\"right\" style=\"padding:6px 0 6px 8px;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\" width=\"58\">Did not run</td>\n");
        // So is the check failed column (phase 5b): a timed or interval start's check that failed in the window.
        var showCheckFailed = days.Any(d => d.CheckFailed > 0);
        if (showCheckFailed)
            html.Append($"<td align=\"right\" style=\"padding:6px 0 6px 8px;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\" width=\"58\">Check failed</td>\n");
        html.Append($"<td align=\"right\" style=\"padding:6px 0 6px 8px;font-size:11.5px;color:{Faint};border-bottom:1px solid {Line};\" width=\"50\">Left open</td>\n");
        html.Append("</tr>\n");
        var ordered = OrderedDays(input, days);
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            var rule = i < ordered.Count - 1 ? $"border-bottom:1px solid {Line};" : "";
            var name = d.OpenProblems > 0
                ? FactoryName(input, d.Factory, Accent, linkWhenProblem: true, weight: "font-weight:600;")
                : E(TitleOf(input, d.Factory));
            html.Append("<tr>\n");
            html.Append($"<td style=\"padding:9px 0;font-size:14px;color:{Ink};{rule}\">{name}</td>\n");
            html.Append(NumberCell(d.Done, alarming: false, rule));
            if (showNothingToDo) html.Append(NumberCell(d.NothingToDo, alarming: false, rule));
            html.Append(NumberCell(d.DidNotRun, alarming: true, rule));
            if (showCheckFailed) html.Append(NumberCell(d.CheckFailed, alarming: true, rule));
            html.Append(NumberCell(d.LeftOpen, alarming: true, rule));
            html.Append("</tr>\n");
        }
        html.Append("</table>\n</td></tr>\n");
    }

    private static string NumberCell(int value, bool alarming, string rule)
    {
        var style = value == 0
            ? $"color:{Faint};"
            : alarming ? $"color:{Bad};font-weight:700;" : $"color:{Ink};";
        return $"<td align=\"right\" style=\"padding:9px 0;font-size:14px;{style}{rule}\">{value.ToString(CultureInfo.InvariantCulture)}</td>\n";
    }

    // The factory's name, linked to its factory page when this Gateway has a public address.
    private static string FactoryName(FactoryEmailInput input, string? factory, string colour, bool linkWhenProblem, string weight = "")
    {
        var title = E(factory is null ? NoFactoryTitle : TitleOf(input, factory));
        if (factory is null || input.BaseUrl is null || !linkWhenProblem)
            return title;
        var href = $"{input.BaseUrl.TrimEnd('/')}/factories/{Uri.EscapeDataString(factory)}#problems";
        return $"<a href=\"{E(href)}\" style=\"color:{colour};text-decoration:none;{weight}\">{title}</a>";
    }

    private static List<CronFactoryDayDto> OrderedDays(FactoryEmailInput input, IReadOnlyList<CronFactoryDayDto> days) =>
        days.OrderBy(d => TitleOf(input, d.Factory), StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Factory, StringComparer.Ordinal).ToList();

    private static string TitleOf(FactoryEmailInput input, string factory) =>
        input.Factories.TryGetValue(factory, out var f) && !string.IsNullOrWhiteSpace(f.Title) ? f.Title : factory;

    /// <summary>"Sender - Morning send": the seat's role word, then the schedule's name.</summary>
    private static string SeatLine(FactoryEmailInput input, CronRunProblemDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Seat)) return p.JobName;
        var seat = p.Factory is not null && input.Factories.TryGetValue(p.Factory, out var f) && f.SeatNames.TryGetValue(p.Seat, out var name)
            ? name
            : p.Seat;
        return $"{seat} - {p.JobName}";
    }

    /// <summary>"Started Fri 10 Oct 09:00 - morning shift - open 22 hours", or "Due ..." for a run that never started.
    /// When it started and how long it has been open are the problems fold's own words (<c>WhenText</c>,
    /// <c>OpenForText</c>), counted at the instant the email describes; nothing is worked out again here.</summary>
    private static string WhenLine(CronRunProblemDto p, bool showOpenFor)
    {
        var line = $"{p.WhenText} - {p.Shift} shift";
        if (showOpenFor && !string.IsNullOrWhiteSpace(p.OpenForText))
            line += " - " + p.OpenForText;
        return line;
    }

    private static void TextProblem(StringBuilder text, FactoryEmailInput input, CronRunProblemDto p, bool showOpenFor)
    {
        text.AppendLine($"- {(p.Factory is null ? NoFactoryTitle : TitleOf(input, p.Factory))}: {SeatLine(input, p)}");
        text.AppendLine($"  {p.Sentence}");
        text.AppendLine($"  {WhenLine(p, showOpenFor)}");
        if (p.Factory is not null && input.BaseUrl is not null)
            text.AppendLine($"  {input.BaseUrl.TrimEnd('/')}/factories/{Uri.EscapeDataString(p.Factory)}#problems");
    }

    /// <summary>The zone as a reader names it: the city of an IANA id ("America/Toronto" is "Toronto").</summary>
    public static string ZoneName(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var iana = zone.HasIanaId
            ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var mapped) ? mapped : null;
        if (string.IsNullOrWhiteSpace(iana) || !iana.Contains('/'))
            return zone.StandardName;
        return iana[(iana.LastIndexOf('/') + 1)..].Replace('_', ' ');
    }

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    private static string Count(int n, string singular, string? plural = null) =>
        $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>HTML-encode, and turn anything outside printable ASCII into a numeric reference, so the body is ASCII
    /// whatever a run wrote in its reason.</summary>
    public static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default:
                    if (c >= ' ' && c <= '~')
                        sb.Append(c);
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        sb.Append("&#").Append(char.ConvertToUtf32(c, s[++i]).ToString(CultureInfo.InvariantCulture)).Append(';');
                    else if (char.IsControl(c))
                        sb.Append(' ');
                    else
                        sb.Append("&#").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
                    break;
            }
        }
        return sb.ToString();
    }
}
