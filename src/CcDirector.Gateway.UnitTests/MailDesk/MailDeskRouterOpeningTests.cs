using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Running;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskRoutingRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE ROUTED SESSION OPENS AS ITS SEAT'S SCHEDULED RUN OPENS (Mail Desk phase 4 contract, section 3.3, "Prompt"): who
/// the seat is, its brief at the published version and where its kit is, from <see cref="ScheduledRunOpening"/> - the
/// one place that wording lives - then a blank line and the Mail Desk's text. What is compared is a call to
/// <see cref="ScheduledRunOpening"/>, never a pasted copy of its words.
/// </summary>
public sealed class MailDeskRouterOpeningTests : IAsyncLifetime
{
    private const string BoxA = "qa-one@example.com";
    private const string ClerksBrief = "BRIEF-OF-THE-CLERK: book every invoice the day it arrives.";
    private MailDeskRoutingRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskRoutingRig();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    [Fact]
    public async Task RouteAsync_RoutedSession_OpensAsTheSeatsScheduledRunOpens_ThenHoldsEveryEmail()
    {
        // Arrange: qa-books/clerk is a named seat with a brief of its own, published at version 3.
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        var first = _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk);
        var second = _rig.Decide(channel, "k-2", MailDeskWords.Decisions.Route, Books, Clerk);
        var published = GiveTheClerkABrief();

        // Act
        await _rig.RouteAsync();

        // Assert: the prompt starts with the opening ScheduledRunOpening gives that seat, then a blank line.
        var (_, request) = Assert.Single(_rig.Starts);
        var seat = ScheduledRunOpening.SeatOf(published, published.Seat(Clerk)!);
        var opening = ScheduledRunOpening.MailDeskOpening(seat);
        Assert.StartsWith(opening + "\n\n", request.PrePrompt, StringComparison.Ordinal);
        Assert.StartsWith("You are Clara, Bookkeeper of QA Books (factory id: qa-books; your agent id: clerk).", opening, StringComparison.Ordinal);
        Assert.Contains(FactorySeatOpening.BriefBlock(published.BriefOf(published.Seat(Clerk)!), "\n"), opening, StringComparison.Ordinal);
        Assert.Contains("definition version 3", opening, StringComparison.Ordinal);
        Assert.Contains(FactorySeatOpening.KitSentence, opening, StringComparison.Ordinal);

        // Then the Mail Desk's text, with every email's block after the opening.
        var rest = request.PrePrompt![(opening.Length + 2)..];
        Assert.StartsWith("The Mail Desk routed 2 emails to you (qa-books/clerk).", rest, StringComparison.Ordinal);
        foreach (var decision in new[] { first, second })
        {
            var row = _rig.Row(decision.Id);
            Assert.Contains($"Routed id: {row.Id:D}\n", rest, StringComparison.Ordinal);
            Assert.Contains($"Sender: {decision.SenderAddress}\n", rest, StringComparison.Ordinal);
            Assert.Contains($"Subject: {decision.Subject}\n", rest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MailDeskOpening_ComparedWithTheScheduledRunsOpening_SaysTheSameWhoYouAreBriefAndKit()
    {
        // Arrange
        var published = GiveTheClerkABrief();
        var seat = ScheduledRunOpening.SeatOf(published, published.Seat(Clerk)!);

        // Act
        var routed = ScheduledRunOpening.MailDeskOpening(seat);
        var scheduled = ScheduledRunOpening.Opening("Clerk morning run", "cj_example", seat);

        // Assert: the same first line (who the seat is) and the same text from the brief on (the brief, the kit and the
        // goal); only the sentence saying which run this is differs, and the routed one names no schedule.
        Assert.Equal(scheduled.Split('\n')[0], routed.Split('\n')[0]);
        const string briefStarts = "--- your brief,";
        Assert.Equal(scheduled[scheduled.IndexOf(briefStarts, StringComparison.Ordinal)..],
            routed[routed.IndexOf(briefStarts, StringComparison.Ordinal)..]);
        Assert.Contains("the Mail Desk", routed, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule", routed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAsync_MailDeskAgentsOwnSeat_OpensAsTheBossOfTheMailDeskFactory()
    {
        // Arrange: an unsure email goes to the channel's own seat, mail-desk/mail-desk, the boss of its factory.
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        _rig.Decide(channel, "k-1", MailDeskWords.Decisions.MailDeskAgent);

        // Act
        await _rig.RouteAsync();

        // Assert
        var (_, request) = Assert.Single(_rig.Starts);
        var published = _rig.Published[MailDeskFactory];
        var opening = ScheduledRunOpening.MailDeskOpening(ScheduledRunOpening.SeatOf(published, published.Seat(MailDeskSeat)!));
        Assert.StartsWith(opening + "\n\nThe Mail Desk routed 1 email to you (mail-desk/mail-desk).", request.PrePrompt, StringComparison.Ordinal);
        Assert.StartsWith("You are the boss of ", opening, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAsync_SeatOfAFactoryWithNoPublishedDefinition_StartsNoSessionAsThatSeat_AndTheMailDeskSeatOpensAsItself()
    {
        // Arrange: qa-books is not defined, so the Gateway holds no brief for its clerk (a defined seat's brief cannot be
        // blank: seat.schema.json), and the router makes no start for it.
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);
        _rig.Decide(channel, "k-1", MailDeskWords.Decisions.Route, Books, Clerk);
        _rig.Published.Remove(Books);

        // Act: the first run sends the email to the Mail Desk seat; the second starts it.
        await _rig.RouteAsync();
        Assert.Empty(_rig.Starts);
        await _rig.RouteAsync();

        // Assert: the one session is the Mail Desk seat's, opened as that seat, and nothing in it speaks as the clerk.
        var (_, request) = Assert.Single(_rig.Starts);
        Assert.Equal((MailDeskFactory, MailDeskSeat), (request.Factory, request.FactorySeat));
        var published = _rig.Published[MailDeskFactory];
        var opening = ScheduledRunOpening.MailDeskOpening(ScheduledRunOpening.SeatOf(published, published.Seat(MailDeskSeat)!));
        Assert.StartsWith(opening + "\n\n", request.PrePrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("your agent id: clerk", request.PrePrompt, StringComparison.Ordinal);
    }

    /// <summary>Publish qa-books at version 3 with its clerk named Clara, a Bookkeeper, holding its own brief.</summary>
    private PublishedFactoryDefinition GiveTheClerkABrief()
    {
        var was = _rig.Published[Books];
        var seats = was.Definition.Seats
            .Select(s => s.Seat == Clerk ? s with { Name = "Clara", Role = "Bookkeeper", Brief = ClerksBrief } : s)
            .ToList();
        var published = new PublishedFactoryDefinition(3, was.Definition with { Title = "QA Books", BossSeat = "someone-else", Seats = seats });
        _rig.Published[Books] = published;
        return published;
    }
}
