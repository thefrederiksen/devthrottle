namespace CcDirector.CliExplorer.Scenarios;

public record ScenarioCategory(
    string Name,
    string Description,
    IReadOnlyList<TestScenario> Scenarios);
