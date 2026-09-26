using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A long wait inside the delivery record says what it is (Voice Delivery mission, phase 6). On 25 September 2026 the
/// "what became of delivery X" verb spent 61 seconds between its two log lines, and nothing said whether that was the
/// record's lock, the file, or the process not being given the processor. Each part now writes the phase 3 lines once
/// it runs past a few seconds.
/// </summary>
public sealed class DeliveryRecordWaitTests : IDisposable
{
    private readonly List<string> _lines = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cc-delivery-wait-" + Guid.NewGuid().ToString("N"));

    public DeliveryRecordWaitTests() => SendWaitNotice.LineObserver += Observe;

    public void Dispose()
    {
        SendWaitNotice.LineObserver -= Observe;
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void Observe(string line)
    {
        lock (_lines)
            if (line.Contains(_sessionId.ToString(), StringComparison.Ordinal))
                _lines.Add(line);
    }

    private List<string> Lines() { lock (_lines) return _lines.ToList(); }

    [Fact]
    public async Task Read_TheSessionsLockIsHeldLong_SaysItWaitsForTheLockThenHowLong()
    {
        // Arrange: another thread holds this session's record lock for six seconds, as a long write would.
        var record = new DeliveryRecord(_directory);
        record.MarkDelivered(_sessionId, "earlier");
        var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (record.SessionLockForTests(_sessionId))
            {
                held.Set();
                Thread.Sleep(TimeSpan.FromSeconds(6));
            }
        });
        holder.Start();
        held.Wait();

        // Act
        var lookup = await Task.Run(() => record.Read(_sessionId, "earlier"));
        holder.Join();

        // Assert
        Assert.Equal(Gateway.Contracts.DeliveryState.Delivered, lookup.State);
        var lines = Lines();
        Assert.Contains(lines, l => l.Contains("[DeliveryRecord] WAITING") && l.Contains("the delivery record lock"));
        Assert.Contains(lines, l => l.Contains("[DeliveryRecord] WAIT ENDED") && l.Contains("the delivery record lock"));
    }

    [Fact]
    public void Read_TheFileTakesLong_SaysHowLongTheReadTook()
    {
        // Arrange
        var record = new DeliveryRecord(_directory);
        record.MarkDelivered(_sessionId, "earlier");
        record.BeforeFileReadForTests = () => Thread.Sleep(TimeSpan.FromSeconds(6));

        // Act
        record.Read(_sessionId, "earlier");

        // Assert
        var lines = Lines();
        Assert.Contains(lines, l => l.Contains("[DeliveryRecord] WAIT ENDED") && l.Contains("to read the delivery record"));
    }

    [Fact]
    public void Read_TheFileTakesLongThenFails_TheWaitIsEndedWithTheFailure()
    {
        // Arrange: the read runs past the notice threshold and then the file cannot be read - the exact path
        // (a slow file under load, then a failure) where the diagnosis is hardest, and the one place a WAITING
        // line was left without its WAIT ENDED (phase 6 review, Pi finding 2).
        var record = new DeliveryRecord(_directory);
        record.MarkDelivered(_sessionId, "earlier");
        var path = record.FileFor(_sessionId);
        record.BeforeFileReadForTests = () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(6));
            File.Delete(path);
            Directory.CreateDirectory(path); // the read now fails: the path is a directory
        };

        // Act
        Assert.Throws<DeliveryRecordUnreadableException>(() => record.Read(_sessionId, "earlier"));
        Directory.Delete(path);

        // Assert: the WAITING line is answered by a WAIT ENDED line that names the failure - a wait names its
        // end in one place, on the failure path too.
        var lines = Lines();
        Assert.Contains(lines, l => l.Contains("[DeliveryRecord] WAITING") && l.Contains("to read the delivery record"));
        Assert.Contains(lines, l => l.Contains("[DeliveryRecord] WAIT ENDED") && l.Contains("to read the delivery record")
                                     && l.Contains("FAILED"));
    }

    [Fact]
    public void Read_QuickRead_WritesNoWaitLine()
    {
        // Arrange
        var record = new DeliveryRecord(_directory);
        record.MarkDelivered(_sessionId, "earlier");

        // Act
        record.Read(_sessionId, "earlier");

        // Assert: an ordinary read is no noisier than before.
        Assert.Empty(Lines());
    }
}
