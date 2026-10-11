using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Secrets;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Secrets;

/// <summary>
/// SECRET TRANSFERS over the real EF store on a throwaway SQLite file (the Secret Handoff mission). What these hold down:
/// a transfer waits until answered or fifteen minutes pass; the first answer wins and the second changes nothing; an
/// expired transfer cannot be approved; a session has at most three waiting; and one account never sees another's.
/// </summary>
public sealed class SecretTransferStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");
    private const string Asker = "aaaaaaaa-0000-0000-0000-000000000001";
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private SecretTransferStore Open() => new(_harness.Open());

    private static SecretTransferAsk Ask(string entry = "devlinux", string? asker = Asker)
        => new(entry, entry, "SOREN_NORTH", "devthrottle-mac-mini", false, asker, "Session 104 \"tests\"", "sudo on the mac");

    private static SecretTransferAnswer Phone(bool approve = true) => new(approve, SecretTransferPlaces.Phone, "the owner's phone", null, null);

    [Fact]
    public void Create_WithNoAnswer_Waits_ForFifteenMinutes()
    {
        var row = Open().Create(TenantA, Ask(), null, T0)!;

        Assert.Equal(SecretTransferStates.Waiting, row.State);
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), row.ExpiresAtUtc);
        Assert.Equal(32, row.TransferId.Length);
    }

    [Fact]
    public void Create_WithTheOwnersWordsFromAChat_IsBornApproved_WithTheWordsRecorded()
    {
        var answer = new SecretTransferAnswer(true, SecretTransferPlaces.Chat, "session x", "yes, send it", null);

        var row = Open().Create(TenantA, Ask(), answer, T0)!;

        Assert.Equal(SecretTransferStates.Approved, row.State);
        Assert.Equal(("chat", "yes, send it"), (row.AnsweredWhere, row.ApprovalWords));
    }

    [Fact]
    public void TryAnswer_TheFirstAnswerWins_AndTheSecondChangesNothing()
    {
        var store = Open();
        var row = store.Create(TenantA, Ask(), null, T0)!;

        var first = store.TryAnswer(TenantA, row.TransferId, Phone(), T0.AddMinutes(1));
        var second = store.TryAnswer(TenantA, row.TransferId,
            new SecretTransferAnswer(false, SecretTransferPlaces.Cockpit, "the owner's cockpit", null, null), T0.AddMinutes(2));

        Assert.True(first);
        Assert.False(second);
        var after = store.Find(TenantA, row.TransferId, T0.AddMinutes(3))!;
        Assert.Equal((SecretTransferStates.Approved, "phone"), (after.State, after.AnsweredWhere));
    }

    [Fact]
    public void TryAnswer_Deny_EndsIt_SayingWhereAndThatNothingMoved()
    {
        var store = Open();
        var row = store.Create(TenantA, Ask(), null, T0)!;

        Assert.True(store.TryAnswer(TenantA, row.TransferId, Phone(approve: false), T0.AddMinutes(1)));

        var after = store.Find(TenantA, row.TransferId, T0.AddMinutes(1))!;
        Assert.Equal(SecretTransferStates.Denied, after.State);
        Assert.Equal("Denied on the phone. Nothing was moved.", after.Outcome);
        Assert.NotNull(after.FinishedAtUtc);
    }

    [Fact]
    public void ATransferNobodyAnswered_Expires_AndCannotThenBeApproved()
    {
        var store = Open();
        var row = store.Create(TenantA, Ask(), null, T0)!;
        var late = T0 + SecretTransferStore.ApprovalLifetime + TimeSpan.FromSeconds(1);

        Assert.False(store.TryAnswer(TenantA, row.TransferId, Phone(), late));

        var after = store.Find(TenantA, row.TransferId, late)!;
        Assert.Equal(SecretTransferStates.Expired, after.State);
        Assert.Equal("Expired: nobody answered within 15 minutes. Nothing was moved.", after.Outcome);
    }

    [Fact]
    public void List_SettlesExpiry_SoNoStaleWaitingIsEverShown()
    {
        var store = Open();
        store.Create(TenantA, Ask(), null, T0);

        var rows = store.List(TenantA, T0.AddDays(-1), T0 + TimeSpan.FromMinutes(16));

        Assert.Equal(SecretTransferStates.Expired, Assert.Single(rows).State);
    }

    [Fact]
    public void AnApprovedTransferAStoppedGatewayOrphaned_Fails_SayingItIsNotKnownWhetherItWasStored()
    {
        var store = Open();
        var approved = store.Create(TenantA, Ask(), Phone(), T0)!;

        var stillMoving = store.Find(TenantA, approved.TransferId, T0 + TimeSpan.FromMinutes(9))!;
        var orphaned = store.Find(TenantA, approved.TransferId, T0 + SecretTransferStore.ApprovedOrphanedAfter)!;

        Assert.Equal(SecretTransferStates.Approved, stillMoving.State);
        Assert.Equal((SecretTransferStates.Failed, SecretTransferStore.OrphanedOutcome), (orphaned.State, orphaned.Outcome));
        Assert.False(store.TryFinish(TenantA, approved.TransferId, delivered: true, "late", T0 + TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void TryFinish_EndsOnlyAnApprovedTransfer()
    {
        var store = Open();
        var waiting = store.Create(TenantA, Ask("a-entry"), null, T0)!;
        var approved = store.Create(TenantA, Ask("b-entry"), Phone(), T0)!;

        Assert.False(store.TryFinish(TenantA, waiting.TransferId, delivered: true, "Stored on devthrottle-mac-mini.", T0));
        Assert.True(store.TryFinish(TenantA, approved.TransferId, delivered: true, "Stored on devthrottle-mac-mini.", T0));
        Assert.Equal(SecretTransferStates.Delivered, store.Find(TenantA, approved.TransferId, T0)!.State);
    }

    [Fact]
    public void Create_ASessionWithThreeWaiting_IsRefused()
    {
        var store = Open();
        for (var i = 0; i < SecretTransferStore.MaxWaitingPerSession; i++)
            Assert.NotNull(store.Create(TenantA, Ask($"entry-{i}"), null, T0));

        Assert.Null(store.Create(TenantA, Ask("one-more"), null, T0));
        Assert.NotNull(store.Create(TenantA, Ask("by-the-owner", asker: null), null, T0));
    }

    [Fact]
    public void Create_AnyAskerWithThreeWaiting_IsRefused_NotOnlyASession()
    {
        var store = Open();
        for (var i = 0; i < SecretTransferStore.MaxWaitingPerSession; i++)
            Assert.NotNull(store.Create(TenantA, Ask($"entry-{i}", asker: null), null, T0));

        Assert.Null(store.Create(TenantA, Ask("one-more", asker: null), null, T0));
    }

    [Fact]
    public void AnotherAccount_NeverSeesOrAnswersATransfer()
    {
        var store = Open();
        var row = store.Create(TenantA, Ask(), null, T0)!;

        Assert.Null(store.Find(TenantB, row.TransferId, T0));
        Assert.Empty(store.List(TenantB, T0.AddDays(-1), T0));
        Assert.False(store.TryAnswer(TenantB, row.TransferId, Phone(), T0));
        Assert.Equal(SecretTransferStates.Waiting, store.Find(TenantA, row.TransferId, T0)!.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-transfer-id")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Find_AnIdThatIsNotOne_IsNull(string id)
    {
        Assert.Null(Open().Find(TenantA, id, T0));
    }
}
