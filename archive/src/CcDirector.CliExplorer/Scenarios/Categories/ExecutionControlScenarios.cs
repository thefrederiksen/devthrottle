namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class ExecutionControlScenarios
{
    public static ScenarioCategory Create() => new(
        "Execution Control",
        "Controlling turn limits and budget constraints",
        new List<TestScenario>
        {
            new("EC-01", "--max-turns 0", "Zero turns -- Claude treats as 1 turn and succeeds",
                "-p --dangerously-skip-permissions --max-turns 0 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true,
                ExpectedExitCode: 0),

            new("EC-02", "--max-turns 1", "Single turn execution",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("EC-03", "--max-turns 2", "Two turns allowing tool use then response",
                "-p --dangerously-skip-permissions --max-turns 2 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("EC-04", "--max-budget-usd 0.01", "Very small budget constraint",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --max-budget-usd 0.01",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("EC-05", "--max-budget-usd 0", "Zero budget -- should fail or return immediately",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --max-budget-usd 0",
                StdinText: "Say just the word pong",
                CostsApiCredits: true,
                ExpectedExitCode: -1),
        });
}
