namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class PrintModeScenarios
{
    public static ScenarioCategory Create() => new(
        "Print Mode",
        "Print mode (-p) basics for non-interactive usage",
        new List<TestScenario>
        {
            new("PM-01", "-p with stdin prompt", "Basic print mode with piped prompt",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("PM-02", "--print long flag", "Long form of -p flag",
                "--print --dangerously-skip-permissions --max-turns 1 --model haiku",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("PM-03", "-p with empty stdin", "Print mode behavior when stdin is empty",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku",
                StdinText: "",
                ExpectedExitCode: -1),

            new("PM-04", "-p with --no-session-persistence", "Avoids writing session to disk",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --no-session-persistence",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
