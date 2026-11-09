namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class SessionManagementScenarios
{
    public static ScenarioCategory Create() => new(
        "Session Management",
        "Session persistence, continuation, and forking",
        new List<TestScenario>
        {
            new("SM-01", "--session-id with UUID", "Create session with explicit UUID",
                $"-p --dangerously-skip-permissions --max-turns 1 --model haiku --session-id {Guid.NewGuid()}",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("SM-02", "-c / --continue flag", "Continue most recent session",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --continue",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("SM-03", "--resume with invalid id", "Resume non-existent session -- error behavior",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --resume non-existent-session-id",
                StdinText: "Say just the word pong",
                CostsApiCredits: true,
                ExpectedExitCode: -1),

            new("SM-04", "--fork-session flag", "Fork a session to create a branch",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --continue --fork-session",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
