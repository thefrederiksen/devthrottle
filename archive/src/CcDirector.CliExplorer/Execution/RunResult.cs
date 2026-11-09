namespace CcDirector.CliExplorer.Execution;

public record RunResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    TimeSpan Duration,
    bool TimedOut);
