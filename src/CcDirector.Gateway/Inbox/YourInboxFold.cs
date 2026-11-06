using System.Globalization;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;

namespace CcDirector.Gateway.Inbox;

/// <summary>Who a message is from, as the owner reads it.</summary>
public sealed record YourInboxFromDto(string Kind, string Label);

/// <summary>One way back from a message to where it came from: its session, its report or its factory. The target is
/// what the client opens; the label is what it shows.</summary>
public sealed record YourInboxLinkDto(string Kind, string Label, string Target);

/// <summary>One thing the owner can do to a message: the state it sets, and the button's words.</summary>
public sealed record YourInboxActionDto(string State, string Label);

/// <summary>Where a reply to this message goes, in words, before the owner writes it.</summary>
public sealed record YourInboxReplyToDto(string Target, string Label);

/// <summary>One reply the owner sent, with where it went.</summary>
public sealed record YourInboxReplyDto(string Id, string Text, string RoutedTo, string Label, DateTime SentAtUtc);

/// <summary>One filter on the Your inbox screen: the view it shows, its button's words, and how many messages it holds.</summary>
public sealed record YourInboxFilterDto(string View, string Label, int Count);

/// <summary>One message in Your inbox, every word finished on the Gateway.</summary>
public sealed record YourInboxMessageDto(
    string Id,
    string Subject,
    string Body,
    string State,
    string StateLabel,
    string Tone,
    bool Unread,
    YourInboxFromDto From,
    IReadOnlyList<YourInboxLinkDto> Links,
    IReadOnlyList<YourInboxActionDto> Actions,
    YourInboxReplyToDto ReplyTo,
    DateTime SentAtUtc,
    DateTime? ReadAtUtc,
    DateTime? ArchivedAtUtc,
    IReadOnlyList<YourInboxReplyDto>? Replies);

/// <summary>An offer made when a reply cannot go where it was meant to: what it is, its words, and what the client needs
/// to act on it.</summary>
/// <param name="Kind"><c>new-session</c> or <c>factory</c>.</param>
/// <param name="DirectorId">For a new session: the Director the ended session ran on.</param>
/// <param name="RepoPath">For a new session: the folder the ended session worked in.</param>
/// <param name="Prompt">For a new session: its first prompt, carrying the reply and what it answers.</param>
/// <param name="Factory">For a factory: the factory whose inbox the reply would be left in.</param>
public sealed record YourInboxReplyOfferDto(string Kind, string Label, string? DirectorId = null, string? RepoPath = null,
    string? Prompt = null, string? Factory = null);

/// <summary>
/// WHAT A YOUR INBOX MESSAGE SAYS ON SCREEN (Factory Inbox mission, phase 3; CLAUDE.md rule 7). Pure: the rows go in, the
/// finished labels come out, and a client prints them as they are - who sent it, its state, the ways back to its session,
/// report and factory, the buttons it offers, and where a reply goes. No session id is ever put in a label: a session is
/// named by its number and name, through the same fold the dev reports use (<see cref="DevReportSessionLabel"/>).
/// </summary>
public static class YourInboxFold
{
    /// <summary>One message, folded. <paramref name="factoryTitles"/> names the account's factories by id;
    /// <paramref name="replies"/> is given only when one message is read on its own.</summary>
    public static YourInboxMessageDto Message(YourInboxMessageEntity m, IReadOnlyDictionary<string, string> factoryTitles,
        IReadOnlyList<YourInboxReplyEntity>? replies = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(factoryTitles);
        return new YourInboxMessageDto(
            Id: m.Id.ToString("D"),
            Subject: m.Subject,
            Body: m.Body,
            State: m.State,
            StateLabel: YourInboxWords.StateLabel(m.State),
            Tone: YourInboxWords.Tone(m.State),
            Unread: m.State == YourInboxWords.States.Unread,
            From: new YourInboxFromDto(m.FromKind, FromLabel(m, factoryTitles)),
            Links: Links(m, factoryTitles),
            Actions: YourInboxWords.ActionsFor(m.State).Select(a => new YourInboxActionDto(a, YourInboxWords.ActionLabelFor(m.State, a))).ToList(),
            ReplyTo: ReplyTo(m, factoryTitles),
            SentAtUtc: m.SentAtUtc,
            ReadAtUtc: m.ReadAtUtc,
            ArchivedAtUtc: m.ArchivedAtUtc,
            Replies: replies?.Select(r => Reply(r, m, factoryTitles)).ToList());
    }

    /// <summary>
    /// Who the message is from. A session's message is the session's number and name; a factory's message is the factory
    /// and the seat that wrote it, as "Booking - Boss".
    /// </summary>
    public static string FromLabel(YourInboxMessageEntity m, IReadOnlyDictionary<string, string> factoryTitles)
    {
        if (m.FromKind == YourInboxWords.Senders.Factory)
        {
            var title = FactoryTitle(m.Factory!, factoryTitles);
            return string.IsNullOrWhiteSpace(m.Seat) ? title : $"{title} - {SeatLabel(m.Seat)}";
        }
        return DevReportSessionLabel.Session(m.SessionNumber, m.SessionName);
    }

    /// <summary>The ways back from a message: its session, its report and its factory, each when it has one.</summary>
    public static IReadOnlyList<YourInboxLinkDto> Links(YourInboxMessageEntity m, IReadOnlyDictionary<string, string> factoryTitles)
    {
        var links = new List<YourInboxLinkDto>();
        if (!string.IsNullOrWhiteSpace(m.SessionId))
            links.Add(new(YourInboxWords.LinkKinds.Session, "Open " + DevReportSessionLabel.Session(m.SessionNumber, m.SessionName), m.SessionId));
        if (m.ReportId is { } report)
            links.Add(new(YourInboxWords.LinkKinds.Report,
                string.IsNullOrWhiteSpace(m.ReportTitle) ? "Open the report" : $"Open the report: {m.ReportTitle}", report.ToString("D")));
        if (!string.IsNullOrWhiteSpace(m.Factory))
            links.Add(new(YourInboxWords.LinkKinds.Factory, "Open " + FactoryTitle(m.Factory, factoryTitles), m.Factory));
        return links;
    }

    /// <summary>
    /// Where a reply goes (the owner's decision, mission section 4): a factory's message is answered into that factory's
    /// Factory inbox; a session's message goes to that session if it is still running - and if it has ended, the reply
    /// route says so and offers the factory or a new session instead.
    /// </summary>
    public static YourInboxReplyToDto ReplyTo(YourInboxMessageEntity m, IReadOnlyDictionary<string, string> factoryTitles)
        => m.FromKind == YourInboxWords.Senders.Factory
            ? new(YourInboxWords.ReplyTargets.Factory, $"Reply into the Factory inbox of {FactoryTitle(m.Factory!, factoryTitles)}")
            : new(YourInboxWords.ReplyTargets.Session, "Reply to " + DevReportSessionLabel.Session(m.SessionNumber, m.SessionName));

    /// <summary>One reply, with where it went in words.</summary>
    public static YourInboxReplyDto Reply(YourInboxReplyEntity r, YourInboxMessageEntity m, IReadOnlyDictionary<string, string> factoryTitles)
    {
        ArgumentNullException.ThrowIfNull(r);
        var label = r.RoutedTo == YourInboxWords.ReplyTargets.Factory
            ? $"Left in the Factory inbox of {FactoryTitle(r.Factory!, factoryTitles)}"
            : $"Sent to {DevReportSessionLabel.Session(m.SessionNumber, m.SessionName)}, which reads it when it is free";
        return new YourInboxReplyDto(r.Id.ToString("D"), r.Text, r.RoutedTo, label, r.SentAtUtc);
    }

    /// <summary>
    /// The text a reply is delivered to a running session as, in its inbox: what it answers, then the owner's words exactly
    /// as he wrote them.
    /// </summary>
    public static string ReplyForSession(YourInboxMessageEntity m, string reply)
        => $"The owner replied to the message you sent to his inbox, \"{m.Subject}\" (sent {m.SentAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC):\n\n{reply}";

    /// <summary>The first prompt of a new session started to carry a reply its ended session cannot read.</summary>
    public static string ReplyForNewSession(YourInboxMessageEntity m, string reply)
        => $"Session {DevReportSessionLabel.Session(m.SessionNumber, m.SessionName)} sent the owner a message in Your inbox and has since ended. " +
           $"Its subject was \"{m.Subject}\" and it said:\n\n{m.Body}\n\nThe owner's reply:\n\n{reply}";

    /// <summary>The sentence the reply route answers with when the session that sent a message has ended.
    /// <paramref name="factoryTitle"/> is the title of the factory the session was a seat of, when its Factory inbox can
    /// take the reply instead; null otherwise.</summary>
    public static string SessionEndedSentence(YourInboxMessageEntity m, bool canStartNew, string? factoryTitle)
        => $"{DevReportSessionLabel.Session(m.SessionNumber, m.SessionName)} has ended, so it cannot read a reply. " +
           (canStartNew, factoryTitle) switch
           {
               (true, null) => "Start a new session with the reply instead.",
               (true, { } title) => $"Start a new session with the reply, or leave it in the Factory inbox of {title}, instead.",
               (false, { } title) => $"Leave it in the Factory inbox of {title} instead. The Gateway does not know where the session ran, so it cannot offer a new session in its place.",
               (false, null) => "The Gateway does not know where it ran, so it cannot offer a new session in its place; nothing was sent.",
           };

    /// <summary>What an offer to leave a reply in a factory's Factory inbox is shown as.</summary>
    public static string LeaveInFactoryLabel(string factoryTitle) => $"Leave this reply in the Factory inbox of {factoryTitle}";

    private static string FactoryTitle(string factory, IReadOnlyDictionary<string, string> titles)
        => titles.TryGetValue(factory, out var title) && !string.IsNullOrWhiteSpace(title) ? title : factory;

    private static string SeatLabel(string seat)
    {
        var s = seat.Trim();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
