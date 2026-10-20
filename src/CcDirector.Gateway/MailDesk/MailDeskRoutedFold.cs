using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// AN EMAIL ROUTED TO A SEAT, AS EVERY CLIENT SHOWS IT (Mail Desk mission, phase 4 contract, sections 4.1 and 4.2).
/// Every word is decided here, once, on the Gateway (repository rule 7): the routed row's <c>stateLabel</c>, the
/// owner's problem for an email handed to them, and the 08:00 email's "Returned and routed" part. Pure: every fact
/// arrives as a <see cref="MailDeskRoutedEmail"/>.
/// </summary>
public static class MailDeskRoutedFold
{
    /// <summary>The 08:00 email's heading of the part.</summary>
    public const string ReturnedAndRoutedHeading = "Returned and routed";

    /// <summary>
    /// The state in words (section 4.1): "Open - with session &lt;id&gt;", "Queued - waiting for
    /// &lt;factory&gt;/&lt;seat&gt;", "Done", or "With you - &lt;owner reason&gt;". A state this does not know is an
    /// error rather than a row quietly labelled wrong.
    /// </summary>
    public static string StateLabel(MailDeskRoutedEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.State switch
        {
            MailDeskWords.RoutedStates.Open => row.SessionId is null
                ? $"Queued - waiting for {row.Factory}/{row.Seat}"
                : $"Open - with session {row.SessionId}",
            MailDeskWords.RoutedStates.Done => "Done",
            MailDeskWords.RoutedStates.Returned => $"With you - {row.OwnerReason}",
            _ => throw new InvalidOperationException($"Routed email {row.Id} is in the state '{row.State}', which has no words."),
        };
    }

    /// <summary>The routed row every route answers with (section 4.1).</summary>
    public static MailDeskRoutedRow Row(MailDeskRoutedEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var r = email.Row;
        return new MailDeskRoutedRow(
            Id: r.Id,
            DecisionId: r.DecisionId,
            Channel: email.Mailbox,
            MessageKey: email.Decision.MessageKey,
            Sender: email.Decision.SenderAddress,
            Subject: email.Decision.Subject,
            ReceivedUtc: AsUtc(email.Decision.ReceivedUtc),
            Factory: r.Factory,
            Seat: r.Seat,
            SessionId: r.SessionId,
            State: r.State,
            StateLabel: StateLabel(r),
            Queued: r.State == MailDeskWords.RoutedStates.Open && r.SessionId is null,
            ReturnNote: r.ReturnNote,
            ReturnedCount: r.ReturnedCount,
            RerouteCount: r.RerouteCount,
            StartFailures: r.StartFailures,
            OwnerReason: r.OwnerReason,
            DoneNote: r.DoneNote,
            CreatedUtc: AsUtc(r.CreatedUtc),
            ChangedUtc: AsUtc(r.ChangedUtc));
    }

    // ---------- the owner's problems (section 4.2) ----------

    /// <summary>
    /// One problem, kind <c>mail-handed-to-owner</c>, for every email handed to the owner by
    /// <paramref name="asOfUtc"/>: "&lt;sender&gt; - &lt;subject&gt; on &lt;mailbox&gt; is with you: &lt;owner
    /// reason&gt;", under the channel's own factory (the mail-desk factory). Open until it is resolved by hand; a
    /// resolve after <paramref name="asOfUtc"/> has not happened yet.
    /// </summary>
    public static IReadOnlyList<CronRunProblemDto> OwnerProblems(IReadOnlyList<MailDeskRoutedEmail> withOwner, TimeZoneInfo zone, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(withOwner);
        ArgumentNullException.ThrowIfNull(zone);
        var found = new List<CronRunProblemDto>();
        foreach (var email in withOwner)
        {
            var row = email.Row;
            if (row.State != MailDeskWords.RoutedStates.Returned)
                throw new ArgumentException($"Routed email {row.Id} is '{row.State}', not with the owner.", nameof(withOwner));
            if (row.OwnerUtc is not { } owner)
                throw new InvalidOperationException($"Routed email {row.Id} is with the owner and says no time it was handed to them.");
            var ownerUtc = AsUtc(owner);
            if (ownerUtc > asOfUtc) continue;
            var shift = WorkShifts.ShiftOf(ownerUtc, zone);
            var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(ownerUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
            var reason = row.OwnerReason ?? "";
            var dto = new CronRunProblemDto
            {
                RunId = row.Id.ToString("D"),
                JobId = row.Id.ToString("D"),
                JobName = email.Mailbox,
                Factory = email.Factory,
                Seat = email.Seat,
                ScheduledUtc = ownerUtc,
                StartedUtc = null,
                Shift = shift.Name,
                ShiftText = shift.Text,
                Kind = CronRunProblems.MailHandedToOwner,
                KindText = CronRunProblems.Words(CronRunProblems.MailHandedToOwner),
                Reason = reason,
                Sentence = $"{OneLine(email.Decision.SenderAddress)} - {OneLine(email.Decision.Subject)} on {email.Mailbox} is with you: {reason}",
                WhenText = "Handed to you " + whenLocal,
                RaisedAtUtc = ownerUtc,
                Occurrences = 1,
                SessionId = row.SessionId,
                State = CronRunProblemStates.Open,
            };
            if (row.ResolvedUtc is { } resolved && AsUtc(resolved) <= asOfUtc)
            {
                dto.State = CronRunProblemStates.Resolved;
                dto.ClosedUtc = AsUtc(resolved);
                dto.ResolvedBy = row.ResolvedBy;
                dto.ResolvedReason = row.ResolvedReason;
                dto.StateText = $"resolved by {row.ResolvedBy}: {row.ResolvedReason}";
            }
            found.Add(dto);
        }
        return found;
    }

    // ---------- the 08:00 email (section 4.2) ----------

    /// <summary>
    /// The "Returned and routed" part for the window from <paramref name="fromUtc"/> to <paramref name="toUtc"/>
    /// (exclusive): every return made in it (both returns of an email returned twice, each with the seat that made it
    /// and its note), every
    /// email handed to the owner in it (with the reason), and the counts of emails routed and done in it and still
    /// open now. Oldest first.
    /// </summary>
    public static MailDeskEmailRouted ReturnedAndRouted(IReadOnlyList<MailDeskRoutedEmail> emails, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(emails);
        bool In(DateTime? at) => at is { } v && AsUtc(v) >= fromUtc && AsUtc(v) < toUtc;

        // An email is returned at most twice; both returns are kept on its row, and each one made in the window is listed.
        var returned = emails
            .SelectMany(e => new[]
            {
                (e, At: e.Row.EarlierReturnedUtc, By: e.Row.EarlierReturnedFrom, Note: e.Row.EarlierReturnNote),
                (e, At: e.Row.ReturnedUtc, By: e.Row.ReturnedFrom, Note: e.Row.ReturnNote),
            })
            .Where(r => In(r.At))
            .OrderBy(r => r.At).ThenBy(r => r.e.Row.Id)
            .Select(r => new MailDeskEmailReturn(r.e.Mailbox, r.e.Decision.SenderAddress, r.e.Decision.Subject, r.By ?? "", r.Note ?? ""))
            .ToList();
        var handed = emails.Where(e => e.Row.State == MailDeskWords.RoutedStates.Returned && In(e.Row.OwnerUtc))
            .OrderBy(e => e.Row.OwnerUtc)
            .Select(e => new MailDeskEmailHanded(e.Mailbox, e.Decision.SenderAddress, e.Decision.Subject, e.Row.OwnerReason ?? ""))
            .ToList();
        var routed = emails.Count(e => In(e.Row.CreatedUtc));
        var done = emails.Count(e => e.Row.State == MailDeskWords.RoutedStates.Done && In(e.Row.ChangedUtc));
        var open = emails.Count(e => e.Row.State == MailDeskWords.RoutedStates.Open);
        var counts = $"Routed {Whole(routed)} - Done {Whole(done)} - Still open {Whole(open)}";
        return new MailDeskEmailRouted(returned, handed, routed, done, open, counts);
    }

    private static string Whole(int n) => n.ToString(CultureInfo.InvariantCulture);

    // A sender's text in a one-line sentence: a line break in a subject cannot start a line of its own.
    private static string OneLine(string s) => new(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>
/// A routed email with what it is about: its row, the decision that routed it (the sender, subject, message key and
/// receive time), and the mailbox it came in on with that mailbox's own factory and seat (the Mail Desk's).
/// </summary>
public sealed record MailDeskRoutedEmail(MailDeskRoutedEntity Row, MailDeskDecisionEntity Decision, string Mailbox, string Factory, string Seat);

/// <summary>The routed row of section 4.1, as every route answers it; <see cref="StateLabel"/> is folded on the
/// Gateway by <see cref="MailDeskRoutedFold.StateLabel"/>.</summary>
public sealed record MailDeskRoutedRow(
    Guid Id,
    Guid DecisionId,
    string Channel,
    string MessageKey,
    string Sender,
    string Subject,
    DateTime ReceivedUtc,
    string Factory,
    string Seat,
    string? SessionId,
    string State,
    string StateLabel,
    bool Queued,
    string? ReturnNote,
    int ReturnedCount,
    int RerouteCount,
    int StartFailures,
    string? OwnerReason,
    string? DoneNote,
    DateTime CreatedUtc,
    DateTime ChangedUtc);

/// <summary>The 08:00 email's "Returned and routed" part, every word decided by <see cref="MailDeskRoutedFold"/>.</summary>
/// <param name="Returned">Every email returned in the window.</param>
/// <param name="HandedToOwner">Every email handed to the owner in the window.</param>
/// <param name="Routed">Emails routed in the window.</param>
/// <param name="Done">Emails a seat said done in the window.</param>
/// <param name="StillOpen">Emails still open now, queued or with a session.</param>
/// <param name="CountsLine">The three counts in words: "Routed 3 - Done 1 - Still open 2".</param>
public sealed record MailDeskEmailRouted(
    IReadOnlyList<MailDeskEmailReturn> Returned,
    IReadOnlyList<MailDeskEmailHanded> HandedToOwner,
    int Routed,
    int Done,
    int StillOpen,
    string CountsLine);

/// <summary>One returned email: the mailbox, sender, subject, the <c>factory/seat</c> that returned it, and its note.</summary>
public sealed record MailDeskEmailReturn(string Mailbox, string Sender, string Subject, string ReturnedBy, string Note);

/// <summary>One email handed to the owner, with the reason.</summary>
public sealed record MailDeskEmailHanded(string Mailbox, string Sender, string Subject, string Reason);
