using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>A factory as the email names it: its title, and its seats' names by seat id.</summary>
/// <param name="Title">The factory's title from the registry, or its id when it is not registered.</param>
/// <param name="SeatNames">Each seat's name (a role word) by seat id.</param>
/// <param name="Archived">True when the owner archived the factory: it is named where a problem names it, but it gets no
/// line of its own in the 08:00 email.</param>
public sealed record FactoryEmailFactory(string Title, IReadOnlyDictionary<string, string> SeatNames, bool Archived = false);

/// <summary>What waits on the owner, for the 08:00 email's "Needs you" section: every asked and escalated row of the
/// factory activity record that no row corrects - the same items the factory page's "Waiting for you" and the factory
/// floor's desk show (<see cref="FactoryAgentsFold.OpenWaiting"/>).</summary>
/// <param name="Items">The open items, in any order; the email orders them.</param>
/// <param name="Truncated">True when the record held more asked, escalated or correcting rows than one read returns, so
/// the list may miss an item - and then the email says so.</param>
public sealed record FactoryEmailNeedsYou(IReadOnlyList<FactoryActivityDto> Items, bool Truncated)
{
    /// <summary>Nothing waits on the owner.</summary>
    public static readonly FactoryEmailNeedsYou None = new(Array.Empty<FactoryActivityDto>(), false);
}

/// <summary>Everything one factory email is composed from. The composer reads nothing else.</summary>
/// <param name="Slot">The slot being sent.</param>
/// <param name="Zone">The account's time zone.</param>
/// <param name="Problems">The account's OPEN problems, from the one problems fold.</param>
/// <param name="Days">Each factory's last day, from the same fold.</param>
/// <param name="Factories">The account's factories by id, for titles and seat names.</param>
/// <param name="BaseUrl">This Gateway's public base address, or null when it has no remotely reachable one.</param>
/// <param name="MailDesk">The 08:00 email's Mail Desk section, folded on the Gateway (<see cref="MailDeskEmailFold"/>),
/// or null when the account's Mail Desk is off or has no mailbox - and then the email is exactly as it was before.</param>
/// <param name="NeedsYou">What waits on the owner (the 08:00 email only), or null for nothing.</param>
public sealed record FactoryEmailInput(
    FactoryEmailSlot Slot,
    TimeZoneInfo Zone,
    IReadOnlyList<CronRunProblemDto> Problems,
    IReadOnlyList<CronFactoryDayDto> Days,
    IReadOnlyDictionary<string, FactoryEmailFactory> Factories,
    string? BaseUrl,
    MailDeskEmailSection? MailDesk = null,
    FactoryEmailNeedsYou? NeedsYou = null);

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
///     it is not here), then what needs the owner, then a line for EVERY factory of the account - its runs counted and
///     what they did, in the runs' own reported words (the one daily email the owner approved on 26 October 2026,
///     <c>devthrottle_internal/docs/missions/factories-run-alone-2026-10-26/daily-email-mockup.html</c>).
///   - "Needs you" is every item a factory run queued for the owner and nobody has handled: the open asked and
///     escalated rows of the factory activity record, each linking to that item on its factory page. A run never waits
///     for the answer; the next run reads it.
///   - What a factory did is never written by a model: it is the line each run's own session reported with its ok
///     result, verbatim, and a run that reported nothing is said to have reported nothing.
///   - 16:00 and midnight: ONLY when the shift that just ended raised a NEW problem - one recorded since the last slot.
///     Older open problems are one line pointing to the earlier email, never repeated.
///   - Read-only: no buttons. Each factory with a problem links to its factory page, where the problem is handled.
///   - Role words only - a seat is named by its role, never a person.
///   - The Mail Desk section (Mail Desk mission, phase 3, section 9) is in the 08:00 email only, after the factories,
///     and only when the Gateway handed one in: every count, every junked email, every change, the spend.
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
    private const string AccentBack = "#F0F6FC";
    private const string Font = "font-family:'Segoe UI',Arial,sans-serif;";

    /// <summary>The heading over a problem whose schedule is in no factory.</summary>
    public const string NoFactoryTitle = "Not in a factory";

    /// <summary>The heading of the 08:00 email's section of items waiting on the owner.</summary>
    public const string NeedsYouHeading = "Needs you - answer when you like";

    /// <summary>The heading of the 08:00 email's line per factory.</summary>
    public const string FactoriesHeading = "Last 24 hours - what each factory did";

    /// <summary>The words after when an item was queued.</summary>
    public const string NextRunActs = "the next run acts on your answer";

    /// <summary>What a run that reported no line with its ok result is said to have done.</summary>
    public const string ReportedNothing = "reported nothing";

    /// <summary>How many of one seat's reported lines a factory's line quotes before it points to the factory page.</summary>
    public const int MaxLinesPerSeat = 3;

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
        var needsYou = input.NeedsYou ?? FactoryEmailNeedsYou.None;
        var waiting = FactoryOwnerActions.WaitingOrder(needsYou.Items);
        if (problems.Count == 0 && input.Days.Count == 0 && input.MailDesk is null && waiting.Count == 0)
            return NotSent("this account has no factory schedules and no open problems, so there is nothing to report");

        // EVERY FACTORY GETS A LINE: each factory the problems fold counted, and each registered factory not archived,
        // even one with no schedule - it is listed with nothing done rather than left out.
        var days = input.Days.ToList();
        foreach (var (id, f) in input.Factories)
        {
            if (f.Archived || days.Any(d => string.Equals(d.Factory, id, StringComparison.OrdinalIgnoreCase))) continue;
            days.Add(new CronFactoryDayDto { Factory = id });
        }

        var local = Local(input.Slot.BoundaryUtc, input.Zone);
        var dayText = local.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        var needsHeadline = waiting.Count == 0 ? null : $"{Count(waiting.Count, "thing")} {(waiting.Count == 1 ? "needs" : "need")} you";
        var subject = needsHeadline is not null
            ? $"Factories: {needsHeadline} - {dayText}"
            : problems.Count == 0
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
            + (checkFailed == 0 ? "." : $", {Count(checkFailed, "check")} failed or did not run.");
        if (needsHeadline is not null)
        {
            // With items waiting, the headline is theirs, so the summary says how the problems stand.
            summary += problems.Count == 0 ? " No problems open." : $" {Count(problems.Count, "problem")} still open, listed first.";
            summary += days.Sum(d => d.LeftOpen) == 0
                ? " Every run closed on its own; nothing waits for your answer."
                : " Nothing waits for your answer.";
        }
        var headline = needsHeadline ?? (problems.Count == 0 ? "All clear" : $"{Count(problems.Count, "problem")} still open");
        var headlineColour = needsHeadline is not null ? Accent : problems.Count == 0 ? Ink : Bad;

        var html = new StringBuilder();
        Open(html, subject, $"Daily report - {dayText} - the last 24 hours");
        html.Append($"<tr><td style=\"padding:16px 18px 6px;{Font}\">\n");
        html.Append($"<div style=\"font-size:21px;font-weight:700;color:{headlineColour};\">{E(headline)}</div>\n");
        html.Append($"<div style=\"font-size:13.5px;color:{Muted};padding-top:3px;\">{E(summary)}</div>\n");
        html.Append("</td></tr>\n");

        if (problems.Count > 0)
        {
            SectionHeading(html, "Still not fixed", "12px 18px 4px");
            ProblemCards(html, input, problems, showOpenFor: true);
        }

        if (waiting.Count > 0)
        {
            SectionHeading(html, NeedsYouHeading, problems.Count > 0 ? "20px 18px 4px" : "12px 18px 4px");
            NeedsYouCards(html, input, waiting, needsYou.Truncated);
        }

        if (days.Count > 0)
        {
            SectionHeading(html, FactoriesHeading, "20px 18px 4px");
            FactoryLines(html, input, days);
        }

        if (input.MailDesk is { } mailDesk)
            MailDeskSection(html, mailDesk);

        var footer = new StringBuilder();
        footer.Append("This email only reports. ");
        footer.Append(input.BaseUrl is null ? "Open DevThrottle, Factories, to act on anything here. " : "Every line opens the Cockpit. ");
        if (waiting.Count > 0)
            footer.Append("A factory never waits for you: what needs you is queued above, and the next run reads your answer and acts on it. ");
        footer.Append(problems.Count > 0
            ? "Each problem is handled on its factory page. A problem leaves this email when the next run of the same job succeeds, or when it is resolved with a reason."
            : "Problems, when there are any, are listed first under \"Still not fixed\".");
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
        if (waiting.Count > 0)
        {
            text.AppendLine(NeedsYouHeading.ToUpperInvariant());
            foreach (var item in waiting)
            {
                text.AppendLine($"- {OneLine(NeedsYouTitle(input, item))}");
                text.AppendLine($"  {OneLine(NeedsYouBody(input, item))}");
                text.AppendLine($"  {NeedsYouWhen(item, input.Zone)}");
                if (NeedsYouHref(input, item) is { } href) text.AppendLine($"  {href}");
            }
            if (needsYou.Truncated) text.AppendLine(TruncatedSentence);
            text.AppendLine();
        }
        if (days.Count > 0)
        {
            text.AppendLine(FactoriesHeading.ToUpperInvariant());
            foreach (var d in OrderedDays(input, days))
            {
                text.AppendLine($"{TitleOf(input, d.Factory)} - {RunCounts(d)}");
                text.AppendLine($"  {OneLine(WhatItDid(input, d))}");
                if (FactoryHref(input, d) is { } href) text.AppendLine($"  {href}");
            }
            text.AppendLine();
        }
        if (input.MailDesk is { } mailDeskText)
            TextMailDesk(text, mailDeskText);
        text.AppendLine(footer.ToString());

        var covered = Covered(problems, days).Concat(waiting.Select(w => w.Factory))
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (input.MailDesk is { } named && !covered.Contains(named.Factory, StringComparer.OrdinalIgnoreCase))
            covered.Add(named.Factory);
        return new FactoryEmail(true, null, subject, html.ToString(), text.ToString(), problems.Count,
            covered.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    // ------------------------------------------------------------------------------------------------------------
    // 08:00: what needs the owner, and what each factory did

    /// <summary>Said under the items when the record could not be read whole.</summary>
    public const string TruncatedSentence =
        "The record held more items than one read returns, so this list may miss one. Open DevThrottle, Factories, for the whole list.";

    private static void NeedsYouCards(StringBuilder html, FactoryEmailInput input, IReadOnlyList<FactoryActivityDto> items, bool truncated)
    {
        html.Append("<tr><td style=\"padding:0 18px;\">\n");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">\n");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i > 0)
                html.Append("<tr><td style=\"height:8px;line-height:8px;font-size:0;\">&nbsp;</td></tr>\n");
            var title = E(NeedsYouTitle(input, item));
            var linked = NeedsYouHref(input, item) is { } href
                ? $"<a href=\"{E(href)}\" style=\"color:{Ink};text-decoration:none;\">{title}</a>"
                : title;
            html.Append($"<tr><td style=\"padding:11px 12px;border-left:4px solid {Accent};background:{AccentBack};border-radius:4px;{Font}\">\n");
            html.Append($"<div style=\"font-size:15px;font-weight:700;\">{linked}</div>\n");
            html.Append($"<div style=\"font-size:13.5px;color:{Ink};padding-top:3px;\">{E(NeedsYouBody(input, item))}</div>\n");
            html.Append($"<div style=\"font-size:12.5px;color:{Muted};padding-top:3px;\">{E(NeedsYouWhen(item, input.Zone))}</div>\n");
            html.Append("</td></tr>\n");
        }
        if (truncated)
            html.Append($"<tr><td style=\"padding:8px 0 0;{Font}font-size:12.5px;color:{Muted};\">{E(TruncatedSentence)}</td></tr>\n");
        html.Append("</table>\n</td></tr>\n");
    }

    // "DevThrottle Marketing - make a video public": the factory, then what the item is about (its subject, or the seat
    // that queued it when it names none).
    private static string NeedsYouTitle(FactoryEmailInput input, FactoryActivityDto item) =>
        $"{TitleOf(input, item.Factory)} - {(string.IsNullOrWhiteSpace(item.Subject) ? SeatName(input, item.Factory, item.FactoryAgent) : item.Subject.Trim())}";

    // "Content: ...": the seat that queued it, then the record's own sentence.
    private static string NeedsYouBody(FactoryEmailInput input, FactoryActivityDto item) =>
        $"{SeatName(input, item.Factory, item.FactoryAgent)}: {item.What}";

    private static string NeedsYouWhen(FactoryActivityDto item, TimeZoneInfo zone) =>
        $"Queued {Local(item.OccurredUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)} - {NextRunActs}";

    // The item on its factory page, which marks each waiting item with this anchor.
    private static string? NeedsYouHref(FactoryEmailInput input, FactoryActivityDto item) =>
        input.BaseUrl is null ? null : $"{input.BaseUrl.TrimEnd('/')}/factories/{Uri.EscapeDataString(item.Factory)}#item-{item.Id:D}";

    // The factory page; its problems card when the factory has an open problem, as before.
    private static string? FactoryHref(FactoryEmailInput input, CronFactoryDayDto d) =>
        input.BaseUrl is null
            ? null
            : $"{input.BaseUrl.TrimEnd('/')}/factories/{Uri.EscapeDataString(d.Factory)}{(d.OpenProblems > 0 ? "#problems" : "")}";

    private static void FactoryLines(StringBuilder html, FactoryEmailInput input, IReadOnlyList<CronFactoryDayDto> days)
    {
        html.Append("<tr><td style=\"padding:0 18px 6px;\">\n");
        html.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"{Font}\">\n");
        var ordered = OrderedDays(input, days);
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            var rule = i < ordered.Count - 1 ? $"border-bottom:1px solid {Line};" : "";
            var title = E(TitleOf(input, d.Factory));
            var name = FactoryHref(input, d) is { } href
                ? $"<a href=\"{E(href)}\" style=\"color:{Accent};text-decoration:none;font-weight:600;\">{title}</a>"
                : $"<span style=\"color:{Ink};font-weight:600;\">{title}</span>";
            var alarming = d.DidNotRun > 0 || d.CheckFailed > 0 || d.LeftOpen > 0;
            html.Append($"<tr><td style=\"padding:10px 0;{rule}\">\n");
            html.Append($"<div style=\"font-size:14px;\">{name} <span style=\"color:{(alarming ? Bad : Muted)};font-size:12.5px;\">- {E(RunCounts(d))}</span></div>\n");
            html.Append($"<div style=\"font-size:13px;color:{Ink};padding-top:3px;\">{E(WhatItDid(input, d))}</div>\n");
            html.Append("</td></tr>\n");
        }
        html.Append("</table>\n</td></tr>\n");
    }

    /// <summary>"6 runs done", then each other count of the day, only when it is not nought.</summary>
    private static string RunCounts(CronFactoryDayDto d)
    {
        var parts = new List<string> { $"{Count(d.Done, "run")} done" };
        if (d.NothingToDo > 0) parts.Add($"{Count(d.NothingToDo, "run")} with nothing to do");
        if (d.DidNotRun > 0) parts.Add($"{Count(d.DidNotRun, "run")} did not run");
        if (d.CheckFailed > 0) parts.Add($"{Count(d.CheckFailed, "check")} failed or did not run");
        if (d.LeftOpen > 0) parts.Add($"{Count(d.LeftOpen, "seat")} left open");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// What the factory's done runs did, seat by seat in the order they first ran: each seat's name and run count, then
    /// the lines its runs reported, verbatim and once each. A run that reported nothing is said to have reported nothing.
    /// Never composed by a model.
    /// </summary>
    private static string WhatItDid(FactoryEmailInput input, CronFactoryDayDto d)
    {
        if (d.DoneRuns.Count == 0)
            return "No run finished in the last 24 hours.";
        var seats = new List<string>();
        foreach (var seat in d.DoneRuns.GroupBy(r => r.Seat, StringComparer.Ordinal))
        {
            var runs = seat.ToList();
            var lines = runs.Where(r => r.Reported is not null).Select(r => r.Reported!).Distinct(StringComparer.Ordinal).ToList();
            var silent = runs.Count(r => r.Reported is null);
            var said = new StringBuilder($"{SeatName(input, d.Factory, seat.Key)} ({Count(runs.Count, "run")}):");
            foreach (var line in lines.Take(MaxLinesPerSeat))
                said.Append(' ').Append(Sentence(line));
            if (lines.Count > MaxLinesPerSeat)
                said.Append($" And {Count(lines.Count - MaxLinesPerSeat, "more line")} on the factory page.");
            if (silent > 0)
                said.Append(lines.Count == 0 ? $" {Sentence(ReportedNothing)}" : $" {Count(silent, "run")} {ReportedNothing}.");
            seats.Add(said.ToString());
        }
        return string.Join(" ", seats);
    }

    // A reported line as a sentence: a full stop added when it has no ending of its own. Its words are not touched.
    private static string Sentence(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return t;
        return t[^1] is '.' or '!' or '?' ? t : t + ".";
    }

    // A seat's name (its role word) when the registry knows it, otherwise its id.
    private static string SeatName(FactoryEmailInput input, string? factory, string seat) =>
        factory is not null && input.Factories.TryGetValue(factory, out var f) && f.SeatNames.TryGetValue(seat, out var name)
            && !string.IsNullOrWhiteSpace(name)
            ? name
            : seat;

    // ------------------------------------------------------------------------------------------------------------
    // 08:00: the Mail Desk section, laid out exactly as the Gateway folded it

    private static void MailDeskSection(StringBuilder html, MailDeskEmailSection md)
    {
        SectionHeading(html, MailDeskEmailFold.Heading, "20px 18px 4px");
        html.Append($"<tr><td style=\"padding:0 18px 6px;{Font}\">\n");
        foreach (var box in md.Mailboxes)
            html.Append(CountsBlock(box.Address, box.Counts));
        html.Append(CountsBlock("All mailboxes", md.Total));

        MessageList(html, "Junked", md.Junked, m => $"{m.Mailbox} - {m.Layer}: {m.Reason}");
        MessageList(html, "Kept for you", md.KeptForOwner, m => m.Mailbox);
        MessageList(html, "Could not be screened", md.NotScreened, m => $"{m.Mailbox} - {m.Reason}");

        SubHeading(html, $"Changes to the Mail Desk ({md.Changes.Count.ToString(CultureInfo.InvariantCulture)})");
        if (md.Changes.Count == 0) None(html);
        foreach (var c in md.Changes)
            Item(html, $"{c.AtText} {c.What}", $"by {c.Who}: {c.Why}");

        SubHeading(html, "Spend");
        html.Append($"<div style=\"font-size:13.5px;color:{Ink};padding-top:3px;\">{E(md.SpendLine)}</div>\n");
        if (md.SpendStoppedSentence is not null)
            html.Append($"<div style=\"font-size:13.5px;color:{Bad};padding-top:3px;font-weight:600;\">{E(md.SpendStoppedSentence)}</div>\n");
        if (md.ObserveLine is not null)
            html.Append($"<div style=\"font-size:13.5px;color:{Muted};padding-top:10px;\">{E(md.ObserveLine)}</div>\n");
        html.Append("</td></tr>\n");
    }

    private static string CountsBlock(string name, MailDeskEmailCounts counts) =>
        $"<div style=\"font-size:14px;color:{Ink};padding-top:8px;font-weight:600;\">{E(name)}: {counts.Total.ToString(CultureInfo.InvariantCulture)}</div>\n" +
        $"<div style=\"font-size:12.5px;color:{Muted};padding-top:2px;\">{E(CountsLine(counts))}</div>\n";

    private static string CountsLine(MailDeskEmailCounts counts) =>
        string.Join(" - ", counts.ByDecision.Select(c => $"{c.Label} {c.Count.ToString(CultureInfo.InvariantCulture)}"));

    private static void MessageList(StringBuilder html, string title, IReadOnlyList<MailDeskEmailMessage> messages,
        Func<MailDeskEmailMessage, string> second)
    {
        SubHeading(html, $"{title} ({messages.Count.ToString(CultureInfo.InvariantCulture)})");
        if (messages.Count == 0) None(html);
        foreach (var m in messages)
            Item(html, $"{m.Sender} - {m.Subject}", second(m));
    }

    private static void SubHeading(StringBuilder html, string text) =>
        html.Append($"<div style=\"font-size:13.5px;color:{Ink};padding-top:14px;font-weight:700;\">{E(text)}</div>\n");

    private static void None(StringBuilder html) =>
        html.Append($"<div style=\"font-size:13px;color:{Faint};padding-top:3px;\">None.</div>\n");

    private static void Item(StringBuilder html, string first, string second)
    {
        html.Append($"<div style=\"font-size:13.5px;color:{Ink};padding-top:5px;\">{E(first)}</div>\n");
        html.Append($"<div style=\"font-size:12.5px;color:{Muted};\">{E(second)}</div>\n");
    }

    private static void TextMailDesk(StringBuilder text, MailDeskEmailSection md)
    {
        text.AppendLine(MailDeskEmailFold.Heading.ToUpperInvariant());
        foreach (var box in md.Mailboxes)
            text.AppendLine($"{box.Address}: {box.Counts.Total.ToString(CultureInfo.InvariantCulture)} ({CountsLine(box.Counts)})");
        text.AppendLine($"All mailboxes: {md.Total.Total.ToString(CultureInfo.InvariantCulture)} ({CountsLine(md.Total)})");
        TextMessages(text, "Junked", md.Junked, m => $"{m.Mailbox} - {m.Layer}: {m.Reason}");
        TextMessages(text, "Kept for you", md.KeptForOwner, m => m.Mailbox);
        TextMessages(text, "Could not be screened", md.NotScreened, m => $"{m.Mailbox} - {m.Reason}");
        text.AppendLine($"Changes to the Mail Desk ({md.Changes.Count.ToString(CultureInfo.InvariantCulture)}):");
        if (md.Changes.Count == 0) text.AppendLine("  None.");
        foreach (var c in md.Changes)
            text.AppendLine($"- {c.AtText} {c.What}").AppendLine($"  by {c.Who}: {c.Why}");
        text.AppendLine($"Spend: {md.SpendLine}");
        if (md.SpendStoppedSentence is not null) text.AppendLine(md.SpendStoppedSentence);
        if (md.ObserveLine is not null) text.AppendLine(md.ObserveLine);
        text.AppendLine();
    }

    private static void TextMessages(StringBuilder text, string title, IReadOnlyList<MailDeskEmailMessage> messages,
        Func<MailDeskEmailMessage, string> second)
    {
        text.AppendLine($"{title} ({messages.Count.ToString(CultureInfo.InvariantCulture)}):");
        if (messages.Count == 0) text.AppendLine("  None.");
        foreach (var m in messages)
            text.AppendLine($"- {OneLine(m.Sender)} - {OneLine(m.Subject)}").AppendLine($"  {OneLine(second(m))}");
    }

    // A sender's text on one line of the plain-text body: a line break in a subject cannot start a line of its own.
    private static string OneLine(string s) => new(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

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
