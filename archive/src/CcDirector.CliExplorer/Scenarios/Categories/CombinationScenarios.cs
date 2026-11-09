namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class CombinationScenarios
{
    public static ScenarioCategory Create() => new(
        "Combinations",
        "Multi-flag combinations testing real-world usage patterns",
        new List<TestScenario>
        {
            new("CB-01", "JSON schema + haiku", "Cheapest structured output: haiku + json schema",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json --json-schema \"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"answer\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"answer\\\"]}\"",
                StdinText: "What is 2+2?",
                CostsApiCredits: true),

            new("CB-02", "stream-json + verbose", "Streaming JSON with verbose debug output",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json --verbose",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("CB-03", "system prompt + JSON output", "Custom system prompt with JSON output format",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json --system-prompt \"You are a math tutor. Always show your work.\"",
                StdinText: "What is 2+2?",
                CostsApiCredits: true),

            new("CB-04", "no tools + custom system prompt", "Disable all tools with custom system prompt",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --tools \"\" --system-prompt \"You are a simple calculator.\"",
                StdinText: "What is 2+2?",
                CostsApiCredits: true),

            new("CB-05", "haiku + budget limit + max-turns 2", "Budget-constrained multi-turn with haiku",
                "-p --dangerously-skip-permissions --max-turns 2 --model haiku --max-budget-usd 0.01",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("CB-06", "plan mode + verbose", "Plan permission mode with verbose output",
                "-p --max-turns 1 --model haiku --permission-mode plan --verbose",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("CB-07", "haiku + no-session + text output", "Ephemeral single-turn: no session persistence + text output",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --no-session-persistence --output-format text",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("CB-08", "JSON input + JSON output", "Full JSON pipeline: stream-json in and out",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format stream-json --output-format stream-json --verbose",
                StdinText: "{\"type\":\"user_message\",\"content\":\"Say just the word pong\"}",
                CostsApiCredits: true),

            new("CB-09", "add-dir + system prompt + haiku", "Multi-directory context with custom system prompt",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --add-dir ../CcDirector.Core --system-prompt \"You are a code reviewer.\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("CB-10", "disallowedTools + allowedTools", "Both allow and disallow tool filters together",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --allowedTools \"Read,Bash\" --disallowedTools \"Write\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
