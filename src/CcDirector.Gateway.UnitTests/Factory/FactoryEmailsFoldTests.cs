using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Email;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The factory page's kept reports (Factory Control, step 1b on brief 5's store), without a server: the sent factory
/// emails that covered one factory, laid out in the Gateway's words, each with a link that reads it again as sent.
/// </summary>
public sealed class FactoryEmailsFoldTests
{
    // Sun 11 Oct 2026 12:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static RegisteredFactoryDto Factory() => new()
    {
        Factory = "tallyhand",
        Title = "Tallyhand",
        Folder = @"D:\f\tallyhand",
        Computer = "SOREN_NORTH",
        BossSeat = "boss",
    };

    private static FactoryEmailDto Email(string id, string kind, DateTime sent, int problems, string state = FactoryEmailStates.Sent) => new()
    {
        Id = id,
        SlotKey = sent.ToString("yyyy-MM-dd-HH"),
        Kind = kind,
        BoundaryUtc = sent,
        State = state,
        SentUtc = state == FactoryEmailStates.Sent ? sent : null,
        Title = $"Factories - {kind} - {problems} problems",
        Factories = { "tallyhand" },
        ProblemCount = problems,
    };

    private static FactoryEmailsDto Answer(params FactoryEmailDto[] emails) => new()
    {
        Count = emails.Length, Factory = "tallyhand", Emails = emails.ToList(),
    };

    [Fact]
    public void Card_KeptEmails_AreLaidOutNewestFirstInTheGatewaysWords()
    {
        var card = FactoryEmailsFold.Card(Factory(), Answer(
            Email("aaaaaaaa-0000-4000-8000-000000000001", FactoryEmailKinds.Morning, Now.AddHours(-4), 3),
            Email("aaaaaaaa-0000-4000-8000-000000000002", FactoryEmailKinds.Check, Now.AddHours(-12), 1)), TimeZoneInfo.Utc, Now);

        Assert.Equal("Reports emailed to you", card.Heading);
        Assert.Null(card.EmptyText);
        Assert.Equal(2, card.Items.Count);
        var morning = card.Items[0];
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", morning.Id);
        Assert.Equal("Factories - morning-report - 3 problems", morning.Title);
        Assert.Equal("Morning report", morning.KindText);
        Assert.Equal("Sent today 08:00", morning.WhenText);
        Assert.Equal("3 problems listed", morning.ProblemText);
        Assert.Equal("Read", morning.ReadLabel);
        Assert.Equal("/factories/tallyhand/emails/aaaaaaaa-0000-4000-8000-000000000001", morning.Href);
        Assert.Equal("Shift check", card.Items[1].KindText);
        Assert.Equal("Sent today 00:00", card.Items[1].WhenText);
        Assert.Equal("1 problem listed", card.Items[1].ProblemText);
    }

    [Fact]
    public void Card_NoKeptEmail_SaysSoAndWhy()
    {
        var card = FactoryEmailsFold.Card(Factory(), Answer(), TimeZoneInfo.Utc, Now);

        Assert.Empty(card.Items);
        Assert.Equal("No report has covered this factory yet. Reports are sent only when the factory email is switched on.", card.EmptyText);
    }

    [Fact]
    public void Card_MoreKeptEmailsThanItShows_ShowsTheNewestOnes()
    {
        var emails = Enumerable.Range(0, FactoryEmailsFold.Shown + 3)
            .Select(i => Email($"aaaaaaaa-0000-4000-8000-{i:D12}", FactoryEmailKinds.Morning, Now.AddDays(-i), 0))
            .ToArray();

        var card = FactoryEmailsFold.Card(Factory(), Answer(emails), TimeZoneInfo.Utc, Now);

        Assert.Equal(FactoryEmailsFold.Shown, card.Items.Count);
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000000", card.Items[0].Id);
        Assert.Equal("no problems listed", card.Items[0].ProblemText);
    }

    [Fact]
    public void Card_AnEmailThatWasNotSent_IsRefused()
    {
        var unsent = Email("aaaaaaaa-0000-4000-8000-000000000009", FactoryEmailKinds.Check, Now.AddHours(-4), 0, FactoryEmailStates.NothingToSend);

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryEmailsFold.Card(Factory(), Answer(unsent), TimeZoneInfo.Utc, Now));
        Assert.Contains("sent emails only", ex.Message);
    }

    [Fact]
    public void KindText_AKindTheStoreNeverWrites_IsRefusedNotGuessed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FactoryEmailsFold.KindText("evening-report"));
        Assert.Contains("'evening-report'", ex.Message);
    }
}
