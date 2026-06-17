namespace CcDirector.Gateway.Running;

/// <summary>
/// The current UTC instant, behind an interface so the cron firing engine (epic #479, #483) is
/// deterministically testable - tests inject a fake clock to make a job "due" without waiting for
/// the wall clock. Production uses <see cref="SystemClock"/>.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTime UtcNow { get; }
}

/// <summary>The production clock: <see cref="DateTime.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
