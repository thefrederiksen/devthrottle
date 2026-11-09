namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class DebugScenarios
{
    public static ScenarioCategory Create() => new(
        "Debug and Verbose",
        "Debug output and verbose logging flags",
        new List<TestScenario>
        {
            new("DB-01", "--debug flag", "Enable debug output",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --debug",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("DB-02", "--debug api", "Debug specifically API calls",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --debug api",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("DB-03", "--verbose flag", "Enable verbose logging",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --verbose",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
