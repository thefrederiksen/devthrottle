namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class VersionAndHelpScenarios
{
    public static ScenarioCategory Create() => new(
        "Version and Help",
        "Zero-cost flags that return immediately without API calls",
        new List<TestScenario>
        {
            new("VH-01", "--version flag", "Returns Claude version string",
                "--version"),

            new("VH-02", "-v short flag", "Short version flag",
                "-v"),

            new("VH-03", "--help flag", "Prints help text with all flags",
                "--help"),
        });
}
