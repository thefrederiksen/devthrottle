namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class OutputFormatScenarios
{
    public static ScenarioCategory Create() => new(
        "Output Formats",
        "Different output format flags controlling response structure",
        new List<TestScenario>
        {
            new("OF-01", "--output-format text", "Default text output format",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format text",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-02", "--output-format json", "JSON output wrapping response in JSON",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-03", "--output-format stream-json", "Streaming JSON output (requires --verbose in print mode)",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json --verbose",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-04", "stream-json + --include-partial-messages", "Includes partial token-by-token messages in stream",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json --verbose --include-partial-messages",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-05", "--input-format text", "Explicit text input format",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format text",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("OF-06", "--input-format stream-json", "Stream-json input requires output-format stream-json",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format stream-json --output-format stream-json --verbose",
                StdinText: "{\"type\":\"user_message\",\"content\":\"Say just the word pong\"}",
                CostsApiCredits: true),
        });
}
