using System.Collections.Concurrent;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Test double for <see cref="ICronNotifier"/> (issue #622). Records every run-complete
/// notification the firing engine delivers so a test can assert WHETHER it fired and with WHAT
/// payload, without standing up the real fleet event ring or a webhook listener. A
/// <see cref="NullCronNotifier"/> is also provided for the many existing cron tests that do not
/// care about notifications and just need a constructor argument.
/// </summary>
public sealed class RecordingCronNotifier : ICronNotifier
{
    private readonly ConcurrentQueue<(CronJobDto Job, string DirectorId, CronRunCompletedPayload Payload)> _calls = new();

    /// <summary>Every notification delivered, in order.</summary>
    public IReadOnlyList<(CronJobDto Job, string DirectorId, CronRunCompletedPayload Payload)> Calls => _calls.ToList();

    /// <summary>How many notifications were delivered.</summary>
    public int CallCount => _calls.Count;

    /// <summary>The most recent payload, or null when none was delivered.</summary>
    public CronRunCompletedPayload? Last => _calls.LastOrDefault().Payload;

    /// <summary>A fixed link to return from <see cref="BuildSessionLink"/> when a session id is present.</summary>
    public string LinkTemplate { get; set; } = "https://host.ts.net/sessions/{sid}/view";

    public string BuildSessionLink(string? directorId, string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? "" : LinkTemplate.Replace("{sid}", sessionId);

    public Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct)
    {
        _calls.Enqueue((job, directorId, payload));
        return Task.CompletedTask;
    }
}

/// <summary>A do-nothing <see cref="ICronNotifier"/> for cron tests that do not assert notification behavior.</summary>
public sealed class NullCronNotifier : ICronNotifier
{
    public string BuildSessionLink(string? directorId, string? sessionId) => "";
    public Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct) =>
        Task.CompletedTask;
}
