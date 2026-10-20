using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// WHAT IS KEPT OF EACH MAILBOX'S CHECKS (Mail Desk mission, phase 3 contract, section 6.2): every failed check, and a
/// good check whenever the mailbox's newest kept row is not already a good one - the first good check after a failure
/// (it clears the problem) and the very first good check (so the channel row can say "signed in"). A good check after
/// a good check writes nothing. Over the real store and a throwaway database.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskChannelCheckStoreTests : IDisposable
{
    private const string Mailbox = "email@devthrottle.com";
    private const string Machine = "devlinux";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskChannelCheckStore _checks;

    public MailDeskChannelCheckStoreTests()
    {
        _db = _h.Open();
        _channels = new MailDeskChannelStore(_db);
        _checks = new MailDeskChannelCheckStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private MailDeskChannelEntity Channel(TenantId account, string address = Mailbox)
        => _channels.Add(account, address, "outlook", new[] { Machine }, MailDeskWho.Person("seed"), "seeded", T0).Channel!;

    private List<MailDeskChannelCheckEntity> Rows(TenantId account)
    {
        using var ctx = _db.CreateContext(account);
        return ctx.MailDeskChannelChecks.AsNoTracking().OrderBy(c => c.CheckedUtc).ToList();
    }

    [Fact]
    public void Record_FirstGoodCheck_WritesARow()
    {
        var channel = Channel(A);

        var write = _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0, Machine));

        Assert.True(write.Written);
        var row = Assert.Single(Rows(A));
        Assert.True(row.Ok);
        Assert.Null(row.Sentence);
        Assert.Equal(Machine, row.Machine);
    }

    [Fact]
    public void Record_GoodCheckAfterAGoodCheck_WritesNothing()
    {
        var channel = Channel(A);
        _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0, Machine));

        var write = _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0.AddMinutes(10), Machine));

        Assert.False(write.Written);
        Assert.Null(write.Row);
        Assert.Single(Rows(A));
    }

    [Fact]
    public void Record_EveryFailedCheck_WritesARowWithTheSentenceAndDetail()
    {
        // Arrange
        var channel = Channel(A);
        var run = Guid.NewGuid();

        // Act - three failures in a row
        for (var i = 0; i < 3; i++)
            _checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, run, T0.AddMinutes(10 * i), Machine,
                "email@devthrottle.com is not signed in on devlinux: run cc-outlook -a support auth", "no account"));

        // Assert
        var rows = Rows(A);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal("email@devthrottle.com is not signed in on devlinux: run cc-outlook -a support auth", r.Sentence);
            Assert.Equal("no account", r.Detail);
            Assert.Equal(run, r.TriggerRunId);
        });
    }

    [Fact]
    public void Record_FirstGoodCheckAfterAFailure_WritesARowAndTheNextGoodOneDoesNot()
    {
        var channel = Channel(A);
        _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0, Machine));
        _checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, null, T0.AddMinutes(10), Machine, "not signed in", null));

        var clearing = _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0.AddMinutes(20), Machine));
        var after = _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0.AddMinutes(30), Machine));

        Assert.True(clearing.Written);
        Assert.False(after.Written);
        Assert.Equal(new[] { true, false, true }, Rows(A).Select(r => r.Ok));
    }

    [Fact]
    public void RecordAll_OneReportOfTwoMailboxes_KeepsEachByItsOwnRule()
    {
        // Arrange - the second mailbox was read before
        var first = Channel(A);
        var second = Channel(A, "support@example.com");
        _checks.Record(A, MailDeskChannelCheckInput.Read(second.Id, null, T0, Machine));

        // Act
        var writes = _checks.RecordAll(A, new[]
        {
            MailDeskChannelCheckInput.Failed(first.Id, null, T0.AddMinutes(10), Machine, "not signed in", null),
            MailDeskChannelCheckInput.Read(second.Id, null, T0.AddMinutes(10), Machine),
        });

        // Assert
        Assert.Equal(new[] { true, false }, writes.Select(w => w.Written));
        Assert.Equal(2, Rows(A).Count);
    }

    [Fact]
    public void RecordAll_OneChannelTwice_IsRefusedAndWritesNothing()
    {
        var channel = Channel(A);

        Assert.Throws<ArgumentException>(() => _checks.RecordAll(A, new[]
        {
            MailDeskChannelCheckInput.Read(channel.Id, null, T0, Machine),
            MailDeskChannelCheckInput.Failed(channel.Id, null, T0, Machine, "not signed in", null),
        }));
        Assert.Empty(Rows(A));
    }

    [Fact]
    public void Record_AFailureWithNoSentence_IsRefused()
    {
        var channel = Channel(A);

        Assert.Throws<ArgumentException>(() => _checks.Record(A,
            new MailDeskChannelCheckInput(channel.Id, null, T0, Machine, false, " ", null)));
        Assert.Empty(Rows(A));
    }

    [Fact]
    public void Record_AGoodCheckWithASentence_IsRefused()
    {
        var channel = Channel(A);

        Assert.Throws<ArgumentException>(() => _checks.Record(A,
            new MailDeskChannelCheckInput(channel.Id, null, T0, Machine, true, "fine", null)));
    }

    [Fact]
    public void Record_NoMachine_IsRefused()
    {
        var channel = Channel(A);

        Assert.Throws<ArgumentException>(() => _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0, "")));
    }

    [Fact]
    public void Record_AnotherAccountsChannel_IsRefused()
    {
        var theirs = Channel(B);

        Assert.Throws<ArgumentException>(() => _checks.Record(A, MailDeskChannelCheckInput.Read(theirs.Id, null, T0, Machine)));
        Assert.Empty(Rows(A));
        Assert.Empty(Rows(B));
    }

    [Fact]
    public void ByChannel_TwoAccounts_EachReadsOnlyItsOwn()
    {
        var mine = Channel(A);
        var theirs = Channel(B);
        _checks.Record(A, MailDeskChannelCheckInput.Failed(mine.Id, null, T0, Machine, "not signed in", null));
        _checks.Record(B, MailDeskChannelCheckInput.Read(theirs.Id, null, T0, Machine));

        var a = _checks.ByChannel(A);

        Assert.Equal(new[] { mine.Id }, a.Keys);
        Assert.Null(_checks.Find(A, Rows(B).Single().Id));
    }

    [Fact]
    public void Record_ALongSentence_IsCutToItsBound()
    {
        var channel = Channel(A);

        _checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, null, T0, Machine, new string('x', 5000), null));

        Assert.Equal(MailDeskChannelCheckStore.MaxSentenceLength, Rows(A).Single().Sentence!.Length);
    }

    [Fact]
    public void Resolve_AFailedCheck_KeepsWhoAndWhyOnceAndASecondResolveChangesNothing()
    {
        // Arrange
        var channel = Channel(A);
        var row = _checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, null, T0, Machine, "not signed in", null)).Row!;

        // Act
        var first = _checks.Resolve(A, row.Id, "you", "signed in again by hand", T0.AddHours(1));
        var second = _checks.Resolve(A, row.Id, "you", "again", T0.AddHours(2));

        // Assert
        Assert.True(first);
        Assert.False(second);
        var kept = _checks.Find(A, row.Id)!;
        Assert.Equal("you", kept.ResolvedBy);
        Assert.Equal("signed in again by hand", kept.ResolvedReason);
        Assert.Equal(T0.AddHours(1), kept.ResolvedUtc);
    }

    [Fact]
    public void Resolve_AGoodCheck_Throws()
    {
        var channel = Channel(A);
        var row = _checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, T0, Machine)).Row!;

        Assert.Throws<InvalidOperationException>(() => _checks.Resolve(A, row.Id, "you", "why", T0));
    }
}
