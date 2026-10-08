using System.Text.Json;
using CcDirector.Core.Input;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A SEND'S STEPS ARE KEPT, WHOLE, WITH ITS DELIVERY RECORD (the Prompt Delivery mission, 8 October 2026). The owner's
/// "go" was refused with a reason cut at 300 characters and its steps scattered through half a million log lines.
/// </summary>
public sealed class SendTrailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-send-trail-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _session = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Step_FromTheSendsOwnFlow_LandsOnItsTrailInOrder()
    {
        var trail = SendTrail.Begin();

        SendTrail.Step("ClaudeCode", "first");
        await Task.Run(() => SendTrail.Step("ClaudeCode", "second, from work the send started"));
        await Task.Yield();
        SendTrail.Step("SessionCommandExecutor", "verdict: delivered");

        Assert.Equal(3, trail.Steps.Count);
        Assert.EndsWith("[ClaudeCode] first", trail.Steps[0]);
        Assert.EndsWith("[ClaudeCode] second, from work the send started", trail.Steps[1]);
        Assert.EndsWith("[SessionCommandExecutor] verdict: delivered", trail.Steps[2]);
        Assert.All(trail.Steps, s => Assert.StartsWith("+", s));
    }

    [Fact]
    public async Task Step_WithNoTrailBegun_KeepsNothing()
    {
        await Task.Run(() =>
        {
            SendTrail.Step("ClaudeCode", "a doorbell, which has no delivery id");
            Assert.Null(SendTrail.Current);
        });
    }

    [Fact]
    public void Step_LongText_IsKeptWhole()
    {
        var trail = SendTrail.Begin();
        var reading = "reading=HoldsText, text='" + new string('x', 2000) + "'";

        SendTrail.Step("ClaudeCode", reading);

        Assert.EndsWith(reading, trail.Steps.Single());
    }

    [Fact]
    public void Steps_PastTheLimit_AreCountedNotKept()
    {
        var trail = SendTrail.Begin();

        for (var i = 0; i < SendTrail.MaxSteps + 5; i++)
            SendTrail.Step("ClaudeCode", $"step {i}");

        Assert.Equal(SendTrail.MaxSteps + 1, trail.Steps.Count);
        Assert.Equal($"... 5 more steps were not kept (a trail keeps {SendTrail.MaxSteps})", trail.Steps[^1]);
    }

    [Fact]
    public void MarkNotDelivered_WithSteps_WritesThemWholeOnTheFinalLine()
    {
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");
        var reason = "[ClaudeCode] ResolveRetainedComposer: the composer still holds text after it was cleared - " + new string('y', 400);
        string[] steps = ["+0ms [ClaudeCode] clearing - pressing Ctrl+E, Backspace x64", "+31000ms [ClaudeCode] " + reason];

        record.MarkNotDelivered(_session, "upload-1", reason, steps);

        var lines = File.ReadAllLines(record.FileFor(_session));
        var delivering = JsonSerializer.Deserialize<DeliveryRecordEntry>(lines[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var final = JsonSerializer.Deserialize<DeliveryRecordEntry>(lines[^1], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(delivering.Steps);
        Assert.Equal("not-delivered", final.State);
        Assert.Equal(reason, final.Reason);
        Assert.Equal(steps, final.Steps);
        Assert.DoesNotContain("\"steps\"", lines[0]);
        Assert.Equal(DeliveryState.NotDelivered, record.Read(_session, "upload-1").State);
    }

    [Fact]
    public void MarkDelivered_WithNoSteps_WritesNoStepsField()
    {
        var record = new DeliveryRecord(_dir);
        record.TryBeginDelivery(_session, "upload-1");

        record.MarkDelivered(_session, "upload-1");

        Assert.DoesNotContain("\"steps\"", File.ReadAllText(record.FileFor(_session)));
    }
}
