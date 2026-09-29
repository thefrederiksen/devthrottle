using System.Diagnostics;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The start-up settlement of the delivery record (issue #3487): a delivery a stopped Director was typing is no longer
/// held as "delivering" forever. It becomes "unconfirmed" - never offered again, never typed again - but only when the
/// process that began it is provably gone. An entry a live send owns is left alone.
/// </summary>
public sealed class DeliveryRecordSettlementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-delivery-settle-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _session = Guid.NewGuid();

    // A Director that ran earlier and has since stopped: a made-up process id and start time.
    private static readonly DeliveryOwner StoppedDirector = new(424242, new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc));

    // The Director starting now, in these tests: another made-up process.
    private static readonly DeliveryOwner StartingDirector = new(515151, new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private DeliveryRecord RecordAs(DeliveryOwner owner, Func<DeliveryOwner, DeliveryOwnerLiveness>? liveness = null)
        => new(_dir, owner: owner, ownerLiveness: liveness ?? (_ => throw new InvalidOperationException("liveness was not expected to be asked")));

    [Fact]
    public void Settle_DeliveringLeftByADirectorThatIsGone_BecomesUnconfirmed_WithTheStoppedWhileTypingReason()
    {
        // Arrange: a stopped Director began typing and never wrote an end.
        RecordAs(StoppedDirector).TryBeginDelivery(_session, "upload-1");
        var starting = RecordAs(StartingDirector, owner => owner == StoppedDirector ? DeliveryOwnerLiveness.Gone : DeliveryOwnerLiveness.Alive);

        // Act
        var report = starting.SettleOrphanedDeliveries();

        // Assert
        Assert.Equal(1, report.Settled);
        Assert.Equal(0, report.KeptLiveOwner);
        Assert.Equal(0, report.KeptUnprovable);
        Assert.Empty(report.UnreadableFiles);
        var lookup = starting.Read(_session, "upload-1");
        Assert.Equal(DeliveryState.Unconfirmed, lookup.State);
        Assert.Equal(DeliveryRecord.DirectorStoppedWhileTypingReason, lookup.Reason);
        Assert.Equal("could not be confirmed: the Director stopped while it was being typed", lookup.Reason);
    }

    [Fact]
    public void Settle_ASettledId_CanNeverBeginAgain_EvenAfterAnotherRestart()
    {
        // Arrange: settled at one start-up.
        RecordAs(StoppedDirector).TryBeginDelivery(_session, "upload-1");
        RecordAs(StartingDirector, _ => DeliveryOwnerLiveness.Gone).SettleOrphanedDeliveries();

        // Act: the Gateway sends the same delivery id again, to this Director and to one started later.
        var claim = RecordAs(StartingDirector).TryBeginDelivery(_session, "upload-1");
        var later = new DeliveryOwner(616161, StartingDirector.StartedAtUtc.AddHours(1));
        var laterClaim = RecordAs(later).TryBeginDelivery(_session, "upload-1");

        // Assert: refused both times, and nothing moved it back to delivering.
        Assert.False(claim.Began);
        Assert.False(laterClaim.Began);
        Assert.Equal(DeliveryState.Unconfirmed, claim.Existing.State);
        Assert.Equal(DeliveryRecord.DirectorStoppedWhileTypingReason, laterClaim.Existing.Reason);
        Assert.Equal(DeliveryState.Unconfirmed, RecordAs(later).Read(_session, "upload-1").State);
    }

    [Fact]
    public void Settle_DeliveringThisProcessBegan_IsLeftAlone_WithTheOperatingSystemAsked()
    {
        // Arrange: THIS process began the delivery (the default owner is the process itself), as a send would at the
        // same moment the start-up settlement runs. The real operating-system check is used.
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");

        // Act: settled by this process, and by a second record in this process asking the operating system.
        var own = record.SettleOrphanedDeliveries();
        var asked = new DeliveryRecord(_dir, owner: StartingDirector).SettleOrphanedDeliveries();

        // Assert
        Assert.Equal(0, own.Settled);
        Assert.Equal(1, own.KeptLiveOwner);
        Assert.Equal(0, asked.Settled);
        Assert.Equal(1, asked.KeptLiveOwner);
        Assert.Equal(DeliveryState.Delivering, record.Read(_session, "upload-1").State);
    }

    [Fact]
    public void Settle_DeliveringAnotherLiveDirectorOwns_IsLeftAlone()
    {
        // Arrange: a second Director on the same state files is typing it right now.
        var otherLiveDirector = new DeliveryOwner(737373, StartingDirector.StartedAtUtc.AddMinutes(-5));
        RecordAs(otherLiveDirector).TryBeginDelivery(_session, "upload-1");
        var asked = new List<DeliveryOwner>();
        var starting = RecordAs(StartingDirector, owner => { asked.Add(owner); return DeliveryOwnerLiveness.Alive; });

        // Act
        var report = starting.SettleOrphanedDeliveries();

        // Assert: its owner was asked about, by id and start time, and the entry is untouched.
        Assert.Equal(otherLiveDirector, Assert.Single(asked));
        Assert.Equal(0, report.Settled);
        Assert.Equal(1, report.KeptLiveOwner);
        Assert.Equal(DeliveryState.Delivering, starting.Read(_session, "upload-1").State);
    }

    [Fact]
    public void Settle_AnOwnerThatCannotBeRead_IsLeftAlone_NeverReadAsGone()
    {
        RecordAs(StoppedDirector).TryBeginDelivery(_session, "upload-1");
        var starting = RecordAs(StartingDirector, _ => DeliveryOwnerLiveness.Unreadable);

        var report = starting.SettleOrphanedDeliveries();

        Assert.Equal(0, report.Settled);
        Assert.Equal(1, report.KeptUnprovable);
        Assert.Equal(DeliveryState.Delivering, starting.Read(_session, "upload-1").State);
    }

    [Fact]
    public void Settle_ADeliveringLineWithNoOwner_WrittenByAnOlderDirector_IsLeftAlone()
    {
        // Arrange: the line a Director older than the owner stamp writes.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, $"{_session:D}.jsonl"),
            "{\"id\":\"upload-1\",\"state\":\"delivering\",\"reason\":null,\"at\":\"2026-09-29T08:00:00Z\"}\n");
        var starting = RecordAs(StartingDirector);

        // Act
        var report = starting.SettleOrphanedDeliveries();

        // Assert: nothing shows its writer is gone, so it is not touched - and liveness was never asked.
        Assert.Equal(0, report.Settled);
        Assert.Equal(1, report.KeptUnprovable);
        Assert.Equal(DeliveryState.Delivering, starting.Read(_session, "upload-1").State);
    }

    [Fact]
    public void Settle_OnlyTheLatestDeliveringLineOfEachId_AndNoOtherState_IsTouched()
    {
        // Arrange: across two sessions - one delivered, one not delivered, one unconfirmed, one delivering that ended,
        // and one still delivering.
        var other = Guid.NewGuid();
        var stopped = RecordAs(StoppedDirector);
        stopped.TryBeginDelivery(_session, "delivered");
        stopped.MarkDelivered(_session, "delivered");
        stopped.TryBeginDelivery(_session, "not-delivered");
        stopped.MarkNotDelivered(_session, "not-delivered", "the send threw");
        stopped.TryBeginDelivery(_session, "unconfirmed");
        stopped.MarkUnconfirmed(_session, "unconfirmed", DeliveryRecord.NoRecordsToWatchReason);
        stopped.TryBeginDelivery(other, "orphan");
        var starting = RecordAs(StartingDirector, _ => DeliveryOwnerLiveness.Gone);

        // Act
        var report = starting.SettleOrphanedDeliveries();

        // Assert
        Assert.Equal(1, report.Settled);
        Assert.Equal(DeliveryState.Delivered, starting.Read(_session, "delivered").State);
        var notDelivered = starting.Read(_session, "not-delivered");
        Assert.Equal(DeliveryState.NotDelivered, notDelivered.State);
        Assert.Equal("the send threw", notDelivered.Reason);
        Assert.Equal(DeliveryRecord.NoRecordsToWatchReason, starting.Read(_session, "unconfirmed").Reason);
        Assert.Equal(DeliveryState.Unconfirmed, starting.Read(other, "orphan").State);
    }

    [Fact]
    public void Settle_AnUnreadableFile_IsNamedAndLeftUntouched_AndEveryOtherFileIsStillSettled()
    {
        // Arrange
        RecordAs(StoppedDirector).TryBeginDelivery(_session, "upload-1");
        var torn = Path.Combine(_dir, $"{Guid.NewGuid():D}.jsonl");
        File.WriteAllText(torn, "{not json\n");
        var starting = RecordAs(StartingDirector, _ => DeliveryOwnerLiveness.Gone);

        // Act
        var report = starting.SettleOrphanedDeliveries();

        // Assert
        Assert.Equal(1, report.Settled);
        Assert.Contains(torn, Assert.Single(report.UnreadableFiles));
        Assert.Equal("{not json\n", File.ReadAllText(torn));
    }

    [Fact]
    public void Settle_NoRecordsYet_SettlesNothing()
    {
        var report = RecordAs(StartingDirector).SettleOrphanedDeliveries();

        Assert.Equal(0, report.Settled);
        Assert.Equal(0, report.KeptLiveOwner + report.KeptUnprovable);
        Assert.Empty(report.UnreadableFiles);
    }

    [Fact]
    public void Delivering_IsWrittenWithItsOwner_AndNoOtherStateCarriesOne()
    {
        // Proves how "owned by a live send" is decided from the file: the delivering line names its process.
        var record = RecordAs(StoppedDirector);
        record.TryBeginDelivery(_session, "upload-1");
        record.MarkDelivered(_session, "upload-1");

        var lines = File.ReadAllLines(record.FileFor(_session));

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"ownerProcessId\":424242", lines[0]);
        Assert.Contains("\"ownerStartedAt\":\"2026-09-29T08:00:00Z\"", lines[0]);
        Assert.DoesNotContain("owner", lines[1]);
    }

    [Fact]
    public void AskTheOperatingSystem_ThisProcess_IsAlive_AndTheSameIdWithAnotherStartTime_IsGone()
    {
        // Proves a reused process id is not taken for the Director that wrote the line.
        var self = DeliveryOwner.Current;

        Assert.Equal(DeliveryOwnerLiveness.Alive, DeliveryRecord.AskTheOperatingSystem(self));
        Assert.Equal(DeliveryOwnerLiveness.Gone, DeliveryRecord.AskTheOperatingSystem(self with { StartedAtUtc = self.StartedAtUtc.AddHours(-1) }));
        Assert.Equal(DeliveryOwnerLiveness.Gone, DeliveryRecord.AskTheOperatingSystem(self with { ProcessId = 0 }));
    }

    [Fact]
    public void AskTheOperatingSystem_AProcessThatHasExited_IsGone()
    {
        // Arrange: a real process that starts and exits, standing in for a Director that stopped.
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var owner = new DeliveryOwner(process.Id, process.StartTime.ToUniversalTime());
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Act and Assert
        Assert.Equal(DeliveryOwnerLiveness.Gone, DeliveryRecord.AskTheOperatingSystem(owner));
    }
}
