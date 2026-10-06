namespace CcDirector.Engine.Storage;

public sealed class RunRecord
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public bool TimedOut { get; set; }
    public double? DurationSeconds { get; set; }

    /// <summary>The Director process that started this run; null on a run recorded before owners were kept.</summary>
    public EngineRunOwner? Owner { get; set; }

    /// <summary>The timeout this run was claimed with; editing the job later does not change it.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>The command process this run started, once it started.</summary>
    public int? ChildProcessId { get; set; }

    /// <summary>A result its owner reported after the claim had already been released; kept, never applied.</summary>
    public string? LateCompletion { get; set; }
}
