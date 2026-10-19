using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// EACH ACCOUNT'S MAIL DESK RULES, VERSIONED, AND THE RECORD OF EVERY CHANGE (Mail Desk mission, phase 1, contract
/// sections 2 and 3). Runs over the real Entity Framework store on a throwaway SQLite file, exactly as the Gateway runs
/// it locally.
///
/// The cases that matter most are the two-writer pair: <see cref="Save_TwoWritersOnTheSameVersion_TheSecondIsStaleAndWritesNothing"/>
/// and <see cref="Database_ASecondRowOfTheSameVersion_IsRefusedByTheUniqueIndex"/>. A store that simply took "newest
/// plus one" without either would pass every other test here and give two different documents the same version.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskRulesStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");
    private static readonly MailDeskWho Seat = MailDeskWho.Session("7a000000-0000-4000-8000-000000000001", "Mail Desk - Seat - screening");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private MailDeskRulesStore Store() => new(_h.Open());

    private static MailDeskAllowEntry Notifications => new() { From = "*@notifications.example.com", What = "build notifications", Do = MailDeskAllowDo.Archive };
    private static MailDeskBlockEntry Spam => new() { From = "*@spammy.example", Why = "cold sales" };

    private static MailDeskChange AllowAdded(string subject = "*@notifications.example.com", MailDeskWho? who = null, string why = "I read these")
        => new(MailDeskWords.ChangeKinds.Allow, MailDeskWords.ChangeActions.Add, subject, """{"from":"*@notifications.example.com"}""", who ?? Owner, why);

    private static MailDeskChange BlockAdded(MailDeskWho? who = null)
        => new(MailDeskWords.ChangeKinds.Block, MailDeskWords.ChangeActions.Add, "*@spammy.example", "{}", who ?? Owner, "never answered");

    // ---------- versions ----------

    [Fact]
    public void Newest_AnAccountThatNeverSaved_IsTheEmptyDocumentAtVersionZero()
    {
        var newest = Store().Newest(A);

        Assert.Equal(0, newest.Version);
        Assert.Null(newest.SavedUtc);
        Assert.Null(newest.SavedBy);
        Assert.Empty(newest.Document.Rules);
        Assert.Empty(newest.Document.Allow);
        Assert.Empty(newest.Document.Block);
        Assert.Empty(newest.Document.Routes);
    }

    [Fact]
    public void Read_VersionZero_IsTheEmptyDocumentEvenAfterSaves()
    {
        var store = Store();
        store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        var zero = store.Read(A, 0)!;

        Assert.Equal(0, zero.Version);
        Assert.Empty(zero.Document.Allow);
    }

    [Fact]
    public void Save_TheFirstSave_IsVersionOne()
    {
        // Act
        var write = Store().Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        // Assert
        Assert.Equal(MailDeskRulesOutcome.Saved, write.Outcome);
        Assert.Equal(1, write.Version!.Version);
        Assert.Equal(Now, write.Version.SavedUtc);
        Assert.Equal(MailDeskWords.WhoKinds.Person, write.Version.SavedByKind);
        Assert.Equal("owner-device-1", write.Version.SavedBy);
        Assert.Equal("*@notifications.example.com", write.Version.Document.Allow.Single().From);
    }

    [Fact]
    public void Save_ASecondSave_AddsAVersionAndTheOldVersionStillReadsBack()
    {
        // Arrange
        var store = Store();
        var v1 = store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now).Version!;

        // Act
        var v2 = store.Save(A, 1, v1.Document.WithBlock(Spam), BlockAdded(), Now.AddMinutes(1)).Version!;

        // Assert
        Assert.Equal(2, v2.Version);
        Assert.Equal(2, store.Newest(A).Version);
        var old = store.Read(A, 1)!;
        Assert.Empty(old.Document.Block);
        Assert.Single(old.Document.Allow);
        Assert.Single(store.Read(A, 2)!.Document.Block);
    }

    [Fact]
    public void Save_NeverOverwrites_EveryVersionIsItsOwnRow()
    {
        var store = Store();
        store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);
        store.Save(A, 1, MailDeskRulesDocument.Empty, new MailDeskChange(MailDeskWords.ChangeKinds.Allow,
            MailDeskWords.ChangeActions.Remove, "*@notifications.example.com", "{}", Owner, "too noisy"), Now.AddMinutes(1));

        using var ctx = _h.Open().CreateContext(A);
        Assert.Equal(new[] { 1, 2 }, ctx.MailDeskRules.OrderBy(r => r.Version).Select(r => r.Version).ToArray());
    }

    [Fact]
    public void Read_AVersionTheAccountDoesNotHave_IsNull()
    {
        var store = Store();
        store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        Assert.Null(store.Read(A, 2));
        Assert.Null(store.Read(A, -1));
    }

    [Fact]
    public void Save_ADocumentInMixedCase_IsStoredInItsOneSpelling()
    {
        var write = Store().Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications with { From = "*@Notifications.Example.COM" }), AllowAdded(), Now);

        Assert.Equal("*@notifications.example.com", write.Version!.Document.Allow.Single().From);
    }

    // ---------- refusals write nothing ----------

    [Fact]
    public void Save_AnInvalidDocument_IsRefusedWithTheValidatorsSentenceAndWritesNothing()
    {
        // Arrange
        var store = Store();
        var bad = MailDeskRulesDocument.Empty.WithAllow(Notifications).WithBlock(Spam with { From = "*@notifications.example.com" });

        // Act
        var write = store.Save(A, 0, bad, AllowAdded(), Now);

        // Assert
        Assert.Equal(MailDeskRulesOutcome.Invalid, write.Outcome);
        Assert.Contains("on both the allow-list and the block list", write.Refusal);
        Assert.Single(write.Problems);
        Assert.Equal(0, store.Newest(A).Version);
        Assert.Equal(0, store.History(A).Total);
    }

    [Fact]
    public void Save_TwoWritersOnTheSameVersion_TheSecondIsStaleAndWritesNothing()
    {
        // Arrange: two Gateways over one database, both read version 0.
        var first = Store();
        var second = Store();

        // Act
        var won = first.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);
        var lost = second.Save(A, 0, MailDeskRulesDocument.Empty.WithBlock(Spam), BlockAdded(Seat), Now);

        // Assert
        Assert.Equal(MailDeskRulesOutcome.Saved, won.Outcome);
        Assert.Equal(MailDeskRulesOutcome.Stale, lost.Outcome);
        Assert.Equal(1, lost.NewestVersion);
        Assert.Contains("made on version 0 and the newest is now version 1. Read the rules again", lost.Refusal);
        Assert.Equal(1, first.Newest(A).Version);
        Assert.Empty(first.Newest(A).Document.Block);
        Assert.Equal(1, first.History(A).Total);
    }

    [Fact]
    public void Database_ASecondRowOfTheSameVersion_IsRefusedByTheUniqueIndex()
    {
        // The unique index is what holds across the hosted Gateway's replicas, where the lock in one process cannot.
        Store().Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        using var ctx = _h.Open().CreateContext(A);
        ctx.MailDeskRules.Add(new MailDeskRulesVersionEntity
        {
            TenantId = A.Value, Version = 1, Document = MailDeskRulesDocument.Empty.ToJson(), SavedUtc = Now, SavedByKind = "person", SavedBy = "x",
        });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void Database_TheSameVersionInTwoAccounts_IsAllowed()
    {
        var store = Store();

        var a = store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);
        var b = store.Save(B, 0, MailDeskRulesDocument.Empty.WithBlock(Spam), BlockAdded(), Now);

        Assert.Equal((1, 1), (a.Version!.Version, b.Version!.Version));
    }

    [Fact]
    public void Save_AChannelChange_Throws()
    {
        var change = new MailDeskChange(MailDeskWords.ChangeKinds.Channel, MailDeskWords.ChangeActions.Add, "email@devthrottle.com", "{}", Owner, "new mailbox");

        Assert.Throws<ArgumentException>(() => Store().Save(A, 0, MailDeskRulesDocument.Empty, change, Now));
    }

    // ---------- the change record ----------

    [Fact]
    public void Save_ByASession_RecordsWhoByIdAndNameAndWhyWithTheVersionsBeforeAndAfter()
    {
        // Arrange
        var store = Store();
        store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        // Act
        var write = store.Save(A, 1, MailDeskRulesDocument.Empty.WithAllow(Notifications).WithBlock(Spam), BlockAdded(Seat), Now.AddMinutes(5));

        // Assert
        var change = write.Change!;
        Assert.Equal((MailDeskWords.ChangeKinds.Block, MailDeskWords.ChangeActions.Add, "*@spammy.example"), (change.Kind, change.Action, change.Subject));
        Assert.Equal((1, 2), (change.VersionBefore, change.VersionAfter));
        Assert.Equal(MailDeskWords.WhoKinds.Session, change.WhoKind);
        Assert.Equal("7a000000-0000-4000-8000-000000000001", change.WhoId);
        Assert.Equal("Mail Desk - Seat - screening", change.WhoName);
        Assert.Equal("never answered", change.Why);
        Assert.Equal(Now.AddMinutes(5), change.AtUtc);
        var stored = store.History(A).Changes.First();
        Assert.Equal(change.Id, stored.Id);
        Assert.Equal("Mail Desk - Seat - screening", stored.WhoName);
    }

    [Fact]
    public void Save_ByAPerson_RecordsNoName()
    {
        var write = Store().Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);

        Assert.Equal(MailDeskWords.WhoKinds.Person, write.Change!.WhoKind);
        Assert.Null(write.Change.WhoName);
        Assert.Equal("""{"from":"*@notifications.example.com"}""", write.Change.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MailDeskChange_ABlankWhy_Throws(string? why)
    {
        Assert.Throws<ArgumentException>(() => new MailDeskChange(MailDeskWords.ChangeKinds.Allow, MailDeskWords.ChangeActions.Add,
            "*@notifications.example.com", "{}", Owner, why));
    }

    [Fact]
    public void MailDeskChange_AnUnknownKindOrAction_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MailDeskChange("allowlist", MailDeskWords.ChangeActions.Add, "x", "{}", Owner, "why"));
        Assert.Throws<ArgumentException>(() => new MailDeskChange(MailDeskWords.ChangeKinds.Allow, "append", "x", "{}", Owner, "why"));
    }

    [Fact]
    public void MailDeskWho_ABlankIdentifier_Throws()
    {
        Assert.Throws<ArgumentException>(() => MailDeskWho.Person(" "));
        Assert.Throws<ArgumentException>(() => MailDeskWho.Session("", "name"));
    }

    [Fact]
    public void WhyRefusal_BlankOrTooLong_SaysSoAndAGoodReasonPasses()
    {
        Assert.Contains("'why' is required", MailDeskWho.WhyRefusal(" "));
        Assert.Contains("at most", MailDeskWho.WhyRefusal(new string('x', MailDeskWho.MaxWhyLength + 1)));
        Assert.Null(MailDeskWho.WhyRefusal("the owner asked"));
    }

    // ---------- history ----------

    [Fact]
    public void History_NewestFirst_WithTheTotalAndTheKindFilter()
    {
        // Arrange
        var store = Store();
        store.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);
        store.Save(A, 1, MailDeskRulesDocument.Empty.WithAllow(Notifications).WithBlock(Spam), BlockAdded(), Now.AddMinutes(1));
        store.Save(A, 2, MailDeskRulesDocument.Empty.WithBlock(Spam), new MailDeskChange(MailDeskWords.ChangeKinds.Allow,
            MailDeskWords.ChangeActions.Remove, "*@notifications.example.com", "{}", Owner, "too noisy"), Now.AddMinutes(2));

        // Act
        var all = store.History(A);
        var firstTwo = store.History(A, n: 2);
        var allowOnly = store.History(A, kind: MailDeskWords.ChangeKinds.Allow);

        // Assert
        Assert.Equal(3, all.Total);
        Assert.Equal(new int?[] { 3, 2, 1 }, all.Changes.Select(c => c.VersionAfter));
        Assert.Equal(2, firstTwo.Changes.Count);
        Assert.Equal(3, firstTwo.Total);
        Assert.Equal(2, allowOnly.Total);
        Assert.All(allowOnly.Changes, c => Assert.Equal(MailDeskWords.ChangeKinds.Allow, c.Kind));
        Assert.Equal(MailDeskWords.ChangeActions.Remove, allowOnly.Changes[0].Action);
    }

    [Fact]
    public void History_AnUnknownKindOrAnOutOfRangeCount_Throws()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.History(A, kind: "allowlist"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.History(A, n: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.History(A, n: MailDeskRulesStore.MaxHistory + 1));
    }

    // ---------- accounts ----------

    [Fact]
    public void Store_AccountA_NeverSeesAccountBsRulesOrChanges()
    {
        // Arrange
        var store = Store();
        store.Save(B, 0, MailDeskRulesDocument.Empty.WithAllow(Notifications), AllowAdded(), Now);
        store.Save(B, 1, MailDeskRulesDocument.Empty.WithAllow(Notifications).WithBlock(Spam), BlockAdded(), Now);

        // Act and Assert
        Assert.Equal(0, store.Newest(A).Version);
        Assert.Null(store.Read(A, 1));
        Assert.Equal(0, store.History(A).Total);
        Assert.Equal(2, store.Newest(B).Version);
    }
}
