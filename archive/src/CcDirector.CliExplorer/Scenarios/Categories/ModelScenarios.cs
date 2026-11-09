namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class ModelScenarios
{
    public static ScenarioCategory Create() => new(
        "Model Selection",
        "Selecting different Claude models and fallback behavior",
        new List<TestScenario>
        {
            new("MS-01", "--model sonnet", "Explicitly select Sonnet model",
                "-p --dangerously-skip-permissions --max-turns 1 --model sonnet",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("MS-02", "--model haiku", "Select cheapest Haiku model",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("MS-03", "--model opus", "Select most capable Opus model",
                "-p --dangerously-skip-permissions --max-turns 1 --model opus",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("MS-04", "--model invalid-name", "Invalid model name error behavior",
                "-p --dangerously-skip-permissions --max-turns 1 --model totally-invalid-model-xyz",
                StdinText: "Say just the word pong",
                CostsApiCredits: true,
                ExpectedExitCode: -1),

            new("MS-05", "--fallback-model haiku", "Fallback model when primary fails",
                "-p --dangerously-skip-permissions --max-turns 1 --model sonnet --fallback-model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
