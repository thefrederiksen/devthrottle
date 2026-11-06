using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Email;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// The factory emails the account was sent that covered one factory, as its page shows them (Factory Control, step 1b
/// on brief 5's store): newest first, each one readable again exactly as it was sent. It does NOT decide what an email
/// says or which factories it covered - <see cref="FactoryEmailStore"/> keeps that as composed. This only lays the kept
/// emails out for the page, in the Gateway's words.
/// </summary>
public static class FactoryEmailsFold
{
    /// <summary>The card's heading. Since the Factory Inbox mission's phase 5 ("Email off") the morning report is no longer
    /// emailed: each factory's morning summary is written into Your inbox, and the report is kept here.</summary>
    public const string MorningReportsHeading = "Morning reports";

    /// <summary>What the card says under its heading.</summary>
    public const string MorningReportsNote =
        "The 08:00 reports that covered this factory, kept for 30 days. Each morning's summary goes to Your inbox; no email is sent. Read opens one as it was written.";

    /// <summary>What the card says when no report covered the factory.</summary>
    public const string MorningReportsEmpty =
        "No morning report has covered this factory yet. They are written only when the morning summary is switched on.";

    /// <summary>How many kept emails the factory page shows; the store keeps 30 days.</summary>
    public const int Shown = 10;

    /// <summary>The kept-emails card for one factory.</summary>
    /// <param name="factory">The registered factory.</param>
    /// <param name="emails">The SENT emails that covered this factory, newest first, as <c>GET /gateway/factory-emails?factory=</c> answers.</param>
    /// <param name="zone">The account's time zone.</param>
    /// <param name="nowUtc">Now.</param>
    public static FactoryPageEmailsDto Card(RegisteredFactoryDto factory, FactoryEmailsDto emails, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(emails);
        ArgumentNullException.ThrowIfNull(zone);
        var items = emails.Emails.Take(Shown).Select(e => Item(factory, e, zone, nowUtc)).ToList();
        return new FactoryPageEmailsDto
        {
            Heading = MorningReportsHeading,
            Note = MorningReportsNote,
            Items = items,
            EmptyText = items.Count == 0
                ? MorningReportsEmpty
                : null,
        };
    }

    private static FactoryPageEmailItemDto Item(RegisteredFactoryDto factory, FactoryEmailDto e, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (e.State != FactoryEmailStates.Sent)
            throw new InvalidOperationException(
                $"Factory email {e.Id} is '{e.State}', and the factory page lists sent emails only - ask the store for sent ones.");
        var sent = e.SentUtc ?? throw new InvalidOperationException($"Factory email {e.Id} is sent but carries no sent time.");
        return new FactoryPageEmailItemDto
        {
            Id = e.Id,
            Title = e.Title,
            KindText = KindText(e.Kind),
            WhenText = $"Sent {FactoriesScreenFold.When(sent, zone, nowUtc, capital: false)}",
            ProblemText = e.ProblemCount switch
            {
                0 => "no problems listed",
                1 => "1 problem listed",
                var n => $"{n} problems listed",
            },
            ReadLabel = "Read",
            Href = $"/factories/{Uri.EscapeDataString(factory.Factory)}/emails/{Uri.EscapeDataString(e.Id)}",
        };
    }

    /// <summary>An email's kind in words. Every kind is named, a check kept from before one email a day included; any other
    /// value throws.</summary>
    public static string KindText(string kind) => kind switch
    {
        FactoryEmailKinds.Morning => "Morning report",
        FactoryEmailKinds.Check => "Shift check",
        _ => throw new InvalidOperationException(
            $"A kept factory email has the kind '{kind}', which is not one FactoryEmailKinds defines. Name it in FactoryEmailsFold.KindText."),
    };
}
