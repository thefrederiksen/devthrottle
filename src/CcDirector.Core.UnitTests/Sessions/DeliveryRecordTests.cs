using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The Director's durable record of delivery ids (Voice Delivery mission, phase 1): what it answers, that it survives
/// a restart, that a torn or foreign file refuses rather than reads as "never seen", and that it stays bounded.
/// </summary>
public sealed class DeliveryRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-delivery-record-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _session = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Read_NeverSeen_IsUnknown()
    {
        // Proves a delivery id the Director never saw answers Unknown - and that no file is needed to say so.
        var record = new DeliveryRecord(_dir);

        var lookup = record.Read(_session, "upload-1");

        Assert.Equal(DeliveryState.Unknown, lookup.State);
        Assert.Null(lookup.At);
    }

    [Fact]
    public void TryBeginDelivery_Unknown_BeginsAndWritesDelivering()
    {
        // Proves the first attempt of an id begins, and Delivering is on disk before anything could be typed.
        var record = new DeliveryRecord(_dir);

        var claim = record.TryBeginDelivery(_session, "upload-1");

        Assert.True(claim.Began);
        Assert.Equal(DeliveryState.Unknown, claim.Existing.State);
        Assert.Equal(DeliveryState.Delivering, record.Read(_session, "upload-1").State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryBeginDelivery_AlreadyDeliveredOrDelivering_IsRefused(bool delivered)
    {
        // Proves a second copy of an id that is delivered, or still being delivered, may not begin.
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");
        if (delivered) record.MarkDelivered(_session, "upload-1");

        var claim = record.TryBeginDelivery(_session, "upload-1");

        Assert.False(claim.Began);
        Assert.Equal(delivered ? DeliveryState.Delivered : DeliveryState.Delivering, claim.Existing.State);
    }

    [Fact]
    public void TryBeginDelivery_NotDelivered_BeginsAgain()
    {
        // Proves a failed send can be retried: NotDelivered is not a reason to refuse.
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");
        record.MarkNotDelivered(_session, "upload-1", "the composer never echoed");

        var claim = record.TryBeginDelivery(_session, "upload-1");

        Assert.True(claim.Began);
        Assert.Equal(DeliveryState.NotDelivered, claim.Existing.State);
        Assert.Equal("the composer never echoed", claim.Existing.Reason);
    }

    [Fact]
    public void Record_SurvivesARestart_IncludingADeliveringLeftByACrash()
    {
        // Proves a fresh record over the same directory - a restarted Director - reads back every state, and that an
        // id the old process was still typing when it died reads back Delivering, never Unknown.
        var before = new DeliveryRecord(_dir);
        before.TryBeginDelivery(_session, "delivered-id");
        before.MarkDelivered(_session, "delivered-id");
        before.TryBeginDelivery(_session, "failed-id");
        before.MarkNotDelivered(_session, "failed-id", "the session exited");
        before.TryBeginDelivery(_session, "crashed-id"); // the process dies here, mid-send

        var after = new DeliveryRecord(_dir);

        Assert.Equal(DeliveryState.Delivered, after.Read(_session, "delivered-id").State);
        var failed = after.Read(_session, "failed-id");
        Assert.Equal(DeliveryState.NotDelivered, failed.State);
        Assert.Equal("the session exited", failed.Reason);
        Assert.Equal(DeliveryState.Delivering, after.Read(_session, "crashed-id").State);
        Assert.False(after.TryBeginDelivery(_session, "crashed-id").Began);
    }

    [Fact]
    public void Record_IsPerSession()
    {
        // Proves an id delivered to one session says nothing about another session.
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");
        record.MarkDelivered(_session, "upload-1");

        Assert.Equal(DeliveryState.Unknown, record.Read(Guid.NewGuid(), "upload-1").State);
    }

    [Theory]
    [InlineData("{\"id\":\"upload-1\",\"state\":\"deliv")]
    [InlineData("{\"id\":\"upload-1\",\"state\":\"sent\",\"at\":\"2026-09-25T09:05:00Z\"}")]
    [InlineData("{\"id\":\"\",\"state\":\"delivered\",\"at\":\"2026-09-25T09:05:00Z\"}")]
    public void Record_UnreadableFile_RefusesBothTheReadAndTheBegin(string badLine)
    {
        // Proves a record that cannot be read is never treated as "never seen" (issue #2745 re-delivered speech that
        // way): the read throws naming the file, and so does the step before typing.
        var record = new DeliveryRecord(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(record.FileFor(_session), badLine + "\n");

        var read = Assert.Throws<DeliveryRecordUnreadableException>(() => record.Read(_session, "upload-1"));
        Assert.Equal(record.FileFor(_session), read.FilePath);
        Assert.Contains(record.FileFor(_session), read.Message);
        Assert.Throws<DeliveryRecordUnreadableException>(() => record.TryBeginDelivery(_session, "upload-1"));
    }

    [Fact]
    public void Write_DropsEntriesOlderThanTheRetention()
    {
        // Proves the file stays bounded: an entry older than the stated retention is gone after the next write,
        // and a recent one is kept.
        var now = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var clock = now;
        var record = new DeliveryRecord(_dir, () => clock);
        record.TryBeginDelivery(_session, "old-id");
        record.MarkDelivered(_session, "old-id");
        clock = now + DeliveryRecord.Retention - TimeSpan.FromHours(1);
        record.TryBeginDelivery(_session, "recent-id");
        record.MarkDelivered(_session, "recent-id");

        clock = now + DeliveryRecord.Retention + TimeSpan.FromMinutes(1);
        record.TryBeginDelivery(_session, "new-id");

        Assert.Equal(DeliveryState.Unknown, record.Read(_session, "old-id").State);
        Assert.Equal(DeliveryState.Delivered, record.Read(_session, "recent-id").State);
        Assert.DoesNotContain("old-id", File.ReadAllText(record.FileFor(_session)));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        // Proves the atomic write replaces the file in one move and leaves only the record itself.
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");
        record.MarkDelivered(_session, "upload-1");

        Assert.Equal(new[] { record.FileFor(_session) }, Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task TryBeginDelivery_ConcurrentCopies_ExactlyOneBegins()
    {
        // Proves the lookup and the write of Delivering are one step: of many simultaneous copies of one id, one begins.
        var record = new DeliveryRecord(_dir);
        using var start = new ManualResetEventSlim(false);

        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => { start.Wait(); return record.TryBeginDelivery(_session, "upload-1").Began; }))
            .ToArray();
        start.Set();
        var began = await Task.WhenAll(attempts);

        Assert.Equal(1, began.Count(b => b));
    }

    [Theory]
    [InlineData(DeliveryState.Unknown, "unknown")]
    [InlineData(DeliveryState.Delivering, "delivering")]
    [InlineData(DeliveryState.Delivered, "delivered")]
    [InlineData(DeliveryState.NotDelivered, "not-delivered")]
    public void DeliveryState_TravelsAsItsWord(DeliveryState state, string word)
    {
        // Proves the wire carries the four words, never the enum's number, and reads them back.
        var json = System.Text.Json.JsonSerializer.Serialize(new DeliveryStateResponse { DeliveryId = "x", State = state },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains($"\"state\":\"{word}\"", json);
        Assert.Equal(word, DeliveryStates.Format(state));
        Assert.True(DeliveryStates.TryParse(word, out var parsed));
        Assert.Equal(state, parsed);
        Assert.False(DeliveryStates.TryParse("NotDelivered", out _));
    }
}
