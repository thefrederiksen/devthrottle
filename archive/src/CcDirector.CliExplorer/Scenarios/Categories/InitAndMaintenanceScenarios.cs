namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class InitAndMaintenanceScenarios
{
    public static ScenarioCategory Create() => new(
        "Init and Maintenance",
        "Zero-cost initialization and maintenance flags",
        new List<TestScenario>
        {
            new("IM-01", "--init-only flag", "Initialize project settings without starting session",
                "--init-only"),

            new("IM-02", "--maintenance flag", "Run maintenance tasks (cleanup, etc.)",
                "-p --maintenance",
                StdinText: "done"),
        });
}
