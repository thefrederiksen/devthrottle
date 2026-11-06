using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Inbox;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>One factory's morning summary, as Your inbox shows it.</summary>
/// <param name="Subject">The subject line, one line.</param>
/// <param name="Body">The message, plain text.</param>
public sealed record FactoryMorningMessageText(string Subject, string Body);

/// <summary>
/// EVERY WORD OF A FACTORY'S MORNING SUMMARY IN YOUR INBOX, ONCE (Factory Inbox mission, phase 5 "Email off"; CLAUDE.md
/// rule 7). The 08:00 factory email became one Your inbox message per factory: first the factory's summary of the last 24
/// hours - exactly the summary its thread's Summary button shows (<see cref="FactoryInboxFold.Summary"/>) - then that
/// factory's part of the morning report (<see cref="FactoryEmailComposer.ForFactory"/>). Pure: no store, no clock.
/// </summary>
public static class FactoryMorningMessage
{
    /// <summary>The heading over the factory's summary.</summary>
    public const string SummaryHeading = "FACTORY INBOX - LAST 24 HOURS";

    /// <summary>The last line: what a reply does.</summary>
    public const string ReplyLine = "Reply to this message to leave a message in this factory's inbox.";

    /// <summary>The longest a message's first line is quoted in the summary.</summary>
    public const int QuoteLength = 120;

    /// <summary>The factories that may be sent a morning summary: every registered factory that is not archived, in title
    /// order. A problem whose schedule is in no registered factory stays in the kept morning report only.</summary>
    public static IReadOnlyList<string> FactoriesOf(FactoryEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Factories
            .Where(f => !f.Value.Archived)
            .OrderBy(f => f.Value.Title, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => f.Key)
            .ToList();
    }

    /// <summary>
    /// Whether a factory has anything to say this morning: a message came in or a decision was made, a run did anything at
    /// all, a problem of its is open, an item of its waits on the owner, or the Mail Desk section is its. A factory with
    /// none of these writes no message.
    /// </summary>
    public static bool HasNews(FactoryEmailInput input, string factory, FactoryInboxSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(summary);
        bool Mine(string? f) => string.Equals(f, factory, StringComparison.OrdinalIgnoreCase);
        if (summary.Count > 0 || summary.ByDecision.Count > 0) return true;
        if (input.Problems.Any(p => p.State == CronRunProblemStates.Open && Mine(p.Factory))) return true;
        if ((input.NeedsYou ?? FactoryEmailNeedsYou.None).Items.Any(i => Mine(i.Factory))) return true;
        if (input.MailDesk is { } md && Mine(md.Factory)) return true;
        return input.Days.Any(d => Mine(d.Factory)
            && d.Done + d.DidNotRun + d.NothingToDo + d.CheckFailed + d.LeftOpen + d.OpenProblems > 0);
    }

    /// <summary>The morning summary of <paramref name="factory"/> for <paramref name="input"/>'s slot.</summary>
    public static FactoryMorningMessageText Compose(FactoryEmailInput input, string factory, FactoryInboxSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentNullException.ThrowIfNull(summary);
        var title = input.Factories.TryGetValue(factory, out var f) && !string.IsNullOrWhiteSpace(f.Title) ? f.Title : factory;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(input.Slot.BoundaryUtc, DateTimeKind.Utc), input.Zone);
        var subject = $"Morning summary: {title} - {local.ToString("dddd d MMMM", CultureInfo.InvariantCulture)}";

        var body = new StringBuilder();
        body.AppendLine(SummaryHeading);
        body.AppendLine(summary.Line);
        foreach (var item in summary.Items)
            body.AppendLine(ItemLine(item, input.Zone));
        body.AppendLine();
        var report = FactoryEmailComposer.ForFactory(input, factory);
        if (report.Length > 0)
            body.AppendLine(report).AppendLine();
        body.Append(ReplyLine);
        return new FactoryMorningMessageText(subject, body.ToString());
    }

    /// <summary>One message of the summary: when it was sent, its first line, and what became of it.</summary>
    public static string ItemLine(FactoryInboxMessageDto item, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(zone);
        var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(item.SentAtUtc, DateTimeKind.Utc), zone)
            .ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        var first = item.Text.Trim().Split('\n')[0].Trim();
        if (first.Length > QuoteLength) first = first[..(QuoteLength - 3)].TrimEnd() + "...";
        var outcome = item.Decision is { } d
            ? d.Label + (d.SessionId is { } s ? $" (session {s})" : "") + (string.IsNullOrWhiteSpace(d.Why) ? "" : $": {d.Why.Trim().Split('\n')[0].Trim()}")
            : item.StateLabel;
        return $"- {when} \"{first}\" - {outcome}";
    }
}
