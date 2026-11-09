namespace CcDirector.CliExplorer.Scenarios;

public record TestScenario(
    string Id,
    string Name,
    string Description,
    string Arguments,
    string? StdinText = null,
    bool CostsApiCredits = false,
    int ExpectedExitCode = 0,
    int TimeoutMs = 30_000);
