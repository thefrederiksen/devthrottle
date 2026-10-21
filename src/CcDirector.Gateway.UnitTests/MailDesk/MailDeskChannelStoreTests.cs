using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE CHANNELS A MAIL DESK SCREENS (Mail Desk mission, phase 1, contract sections 1 and 2): a new channel starts in
/// observe, a removed one is kept with its decisions, the address can be added again as a new row, at most one active
/// row per address, and every change is recorded with who and why. Over a throwaway SQLite file.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskChannelStoreTests : IDisposable
{
    private const string Mailbox = "email@devthrottle.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");
    private static readonly string[] Machines = { "devlinux", "BACKUP-PC" };

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private MailDeskChannelStore Store() => new(_h.Open());

    /// <summary>How many changes account A has recorded: a refusal must leave it as it was (contract section 4).</summary>
    private int ChangesRecorded() => new MailDeskRulesStore(_h.Open()).History(A).Total;

    private static MailDeskChannelWrite AddMailbox(MailDeskChannelStore store, TenantId? tenant = null, string address = Mailbox, DateTime? at = null)
        => store.Add(tenant ?? A, address, "outlook", Machines, Owner, "the support mailbox", at ?? Now);

    // ---------- add ----------

    [Fact]
    public void Add_ANewMailbox_StartsInObserveWithTheDefaultStartAndTheMachinesInOrder()
    {
        // Act
        var write = AddMailbox(Store(), address: "  Email@DevThrottle.com ");

        // Assert
        Assert.Equal(MailDeskChannelOutcome.Added, write.Outcome);
        var channel = write.Channel!;
        Assert.Equal(MailDeskWords.ChannelKinds.Email, channel.Kind);
        Assert.Equal(MailDeskWords.Providers.Outlook, channel.Provider);
        Assert.Equal(Mailbox, channel.Address);
        Assert.Equal(MailDeskWords.Modes.Observe, channel.Mode);
        Assert.Equal((MailDeskWords.DefaultFactory, MailDeskWords.DefaultSeat), (channel.Factory, channel.Seat));
        Assert.Equal(Machines, MailDeskChannelStore.Machines(channel));
        Assert.Equal((Now, "owner-device-1", Now, "owner-device-1"), (channel.CreatedUtc, channel.CreatedBy, channel.ChangedUtc, channel.ChangedBy));
        Assert.Null(channel.RemovedUtc);
    }

    [Fact]
    public void Add_ANewMailbox_RecordsAChannelChangeWithNoRulesVersions()
    {
        var store = Store();
        AddMailbox(store);

        var change = new MailDeskRulesStore(_h.Open()).History(A).Changes.Single();

        Assert.Equal((MailDeskWords.ChangeKinds.Channel, MailDeskWords.ChangeActions.Add, Mailbox), (change.Kind, change.Action, change.Subject));
        Assert.Null(change.VersionBefore);
        Assert.Null(change.VersionAfter);
        Assert.Equal("the support mailbox", change.Why);
        Assert.Contains("\"machines\":[\"devlinux\",\"BACKUP-PC\"]", change.Detail);
    }

    [Fact]
    public void Add_AnAddressThatIsAlreadyActive_IsRefusedAndRecordsNothing()
    {
        var store = Store();
        AddMailbox(store);

        var again = AddMailbox(store, address: "EMAIL@devthrottle.com");

        Assert.Equal(MailDeskChannelOutcome.AlreadyActive, again.Outcome);
        Assert.Contains("is already a Mail Desk channel", again.Refusal);
        Assert.Single(store.List(A));
        Assert.Equal(1, new MailDeskRulesStore(_h.Open()).History(A).Total);
    }

    [Theory]
    [InlineData("*@devthrottle.com")]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void Add_AnAddressThatIsNotOneMailbox_IsRefused(string address)
    {
        var write = Store().Add(A, address, "outlook", Machines, Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.BadAddress, write.Outcome);
        Assert.Contains("address", write.Refusal);
    }

    [Fact]
    public void Add_AnUnknownProvider_IsRefusedListingBothAcceptedOnes()
    {
        var write = Store().Add(A, Mailbox, "carrier-pigeon", Machines, Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.BadProvider, write.Outcome);
        Assert.Equal("provider 'carrier-pigeon' is not one the Mail Desk reads; give 'outlook' or 'gmail'", write.Refusal);
    }

    [Theory]
    [InlineData("gmail")]
    [InlineData(" Gmail ")]
    public void Add_Gmail_IsAddedWithTheProviderFolded(string provider)
    {
        var write = Store().Add(A, Mailbox, provider, Machines, Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.Added, write.Outcome);
        Assert.Equal(MailDeskWords.Providers.Gmail, write.Channel!.Provider);
    }

    [Fact]
    public void Add_NoMachines_IsRefused()
    {
        var write = Store().Add(A, Mailbox, "outlook", Array.Empty<string>(), Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.BadMachines, write.Outcome);
        Assert.Contains("name at least one machine", write.Refusal);
    }

    [Fact]
    public void Add_TheSameMachineTwiceOrABlankOne_IsRefused()
    {
        var store = Store();

        Assert.Contains("twice", store.Add(A, Mailbox, "outlook", new[] { "devlinux", "DEVLINUX" }, Owner, "why", Now).Refusal);
        Assert.Contains("entry 2 is blank", store.Add(A, Mailbox, "outlook", new[] { "devlinux", " " }, Owner, "why", Now).Refusal);
    }

    [Fact]
    public void Add_ABlankWhy_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store().Add(A, Mailbox, "outlook", Machines, Owner, " ", Now));
    }

    // ---------- remove, and adding again ----------

    [Fact]
    public void Remove_AnActiveChannel_KeepsTheRowMarkedRemovedWithWhoAndWhy()
    {
        // Arrange
        var store = Store();
        AddMailbox(store);

        // Act
        var write = store.Remove(A, Mailbox, Owner, "moved to another mailbox", Now.AddHours(1));

        // Assert
        Assert.Equal(MailDeskChannelOutcome.Removed, write.Outcome);
        Assert.Empty(store.List(A));
        var kept = store.List(A, includeRemoved: true).Single();
        Assert.Equal((Now.AddHours(1), "owner-device-1", "moved to another mailbox"), (kept.RemovedUtc, kept.RemovedBy, kept.RemovedWhy));
        var change = new MailDeskRulesStore(_h.Open()).History(A).Changes[0];
        Assert.Equal((MailDeskWords.ChangeKinds.Channel, MailDeskWords.ChangeActions.Remove), (change.Kind, change.Action));
    }

    [Fact]
    public void Remove_KeepsTheChannelsDecisions()
    {
        // Arrange
        var store = Store();
        var channel = AddMailbox(store).Channel!;
        var decisions = new MailDeskDecisionStore(_h.Open());
        decisions.Insert(A, MailDeskDecisionStoreTests.Decision(channel.Id, "m-1"));

        // Act
        store.Remove(A, Mailbox, Owner, "done with it", Now.AddHours(1));

        // Assert
        var kept = decisions.List(A, new MailDeskDecisionFilter(ChannelAddress: Mailbox));
        Assert.Equal(1, kept.Total);
        Assert.Equal(channel.Id, kept.Decisions.Single().ChannelId);
    }

    [Fact]
    public void Add_AnAddressThatWasRemoved_MakesANewRow()
    {
        // Arrange
        var store = Store();
        var first = AddMailbox(store).Channel!;
        store.Remove(A, Mailbox, Owner, "paused", Now.AddHours(1));

        // Act
        var second = AddMailbox(store, at: Now.AddHours(2));

        // Assert
        Assert.Equal(MailDeskChannelOutcome.Added, second.Outcome);
        Assert.NotEqual(first.Id, second.Channel!.Id);
        Assert.Equal(second.Channel.Id, store.FindActive(A, Mailbox)!.Id);
        Assert.Equal(2, store.List(A, includeRemoved: true).Count);
        Assert.Single(store.List(A));
    }

    [Fact]
    public void Remove_AnAddressWithNoActiveChannel_IsNoSuchChannel()
    {
        var write = Store().Remove(A, Mailbox, Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.NoSuchChannel, write.Outcome);
        Assert.Contains("is not an active Mail Desk channel", write.Refusal);
        Assert.Equal(0, ChangesRecorded());
    }

    [Fact]
    public void Database_ASecondActiveRowForOneAddress_IsRefusedByTheUniqueIndex()
    {
        // The index over active rows is what holds across the hosted Gateway's replicas.
        AddMailbox(Store());

        using var ctx = _h.Open().CreateContext(A);
        ctx.MailDeskChannels.Add(new MailDeskChannelEntity
        {
            TenantId = A.Value, Kind = "email", Provider = "outlook", Address = Mailbox, MachinesJson = "[\"x\"]",
            Factory = "mail-desk", Seat = "mail-desk", Mode = "observe", CreatedUtc = Now, CreatedBy = "x", ChangedUtc = Now, ChangedBy = "x",
        });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void Database_ARemovedRowBesideAnActiveOne_IsAllowed()
    {
        AddMailbox(Store());

        using var ctx = _h.Open().CreateContext(A);
        ctx.MailDeskChannels.Add(new MailDeskChannelEntity
        {
            TenantId = A.Value, Kind = "email", Provider = "outlook", Address = Mailbox, MachinesJson = "[\"x\"]",
            Factory = "mail-desk", Seat = "mail-desk", Mode = "observe", CreatedUtc = Now, CreatedBy = "x", ChangedUtc = Now, ChangedBy = "x",
            RemovedUtc = Now, RemovedBy = "x", RemovedWhy = "old",
        });
        ctx.SaveChanges();
        Assert.Equal(2, ctx.MailDeskChannels.Count());
    }

    // ---------- mode ----------

    [Fact]
    public void SetMode_ObserveToLive_ChangesTheRowAndRecordsFromAndTo()
    {
        // Arrange
        var store = Store();
        AddMailbox(store);
        var session = MailDeskWho.Session("7a000000-0000-4000-8000-000000000002", "Mail Desk - Lead");

        // Act
        var write = store.SetMode(A, Mailbox, "Live", session, "a week of good decisions", Now.AddDays(7));

        // Assert
        Assert.Equal(MailDeskChannelOutcome.ModeSet, write.Outcome);
        Assert.Equal(MailDeskWords.Modes.Live, store.FindActive(A, Mailbox)!.Mode);
        Assert.Equal((Now.AddDays(7), session.Id), (write.Channel!.ChangedUtc, write.Channel.ChangedBy));
        var change = new MailDeskRulesStore(_h.Open()).History(A, kind: MailDeskWords.ChangeKinds.Mode).Changes.Single();
        Assert.Equal(MailDeskWords.ChangeActions.Set, change.Action);
        Assert.Equal("""{"from":"observe","to":"live"}""", change.Detail);
        Assert.Equal("Mail Desk - Lead", change.WhoName);
        Assert.Null(change.VersionBefore);
    }

    [Fact]
    public void SetMode_AModeOutsideObserveAndLive_IsRefused()
    {
        var store = Store();
        AddMailbox(store);

        var before = ChangesRecorded();

        var write = store.SetMode(A, Mailbox, "auto", Owner, "why", Now);

        Assert.Equal(MailDeskChannelOutcome.BadMode, write.Outcome);
        Assert.Equal("mode 'auto' is not a Mail Desk mode; give 'observe' or 'live'", write.Refusal);
        Assert.Equal(MailDeskWords.Modes.Observe, store.FindActive(A, Mailbox)!.Mode);
        Assert.Equal(before, ChangesRecorded());
    }

    [Fact]
    public void SetMode_ARemovedChannel_IsNoSuchChannel()
    {
        var store = Store();
        AddMailbox(store);
        store.Remove(A, Mailbox, Owner, "why", Now);
        var before = ChangesRecorded();

        Assert.Equal(MailDeskChannelOutcome.NoSuchChannel, store.SetMode(A, Mailbox, "live", Owner, "why", Now).Outcome);
        Assert.Equal(before, ChangesRecorded());
    }

    // ---------- accounts ----------

    [Fact]
    public void Store_AccountA_NeverSeesAccountBsChannels()
    {
        // Arrange
        var store = Store();
        AddMailbox(store, B);

        // Act and Assert
        Assert.Empty(store.List(A, includeRemoved: true));
        Assert.Null(store.FindActive(A, Mailbox));
        Assert.Equal(MailDeskChannelOutcome.NoSuchChannel, store.Remove(A, Mailbox, Owner, "why", Now).Outcome);
        Assert.Equal(MailDeskChannelOutcome.NoSuchChannel, store.SetMode(A, Mailbox, "live", Owner, "why", Now).Outcome);
        // And the same address may be a channel of both accounts.
        Assert.Equal(MailDeskChannelOutcome.Added, AddMailbox(store, A).Outcome);
        Assert.Equal(MailDeskWords.Modes.Observe, store.FindActive(B, Mailbox)!.Mode);
    }
}
