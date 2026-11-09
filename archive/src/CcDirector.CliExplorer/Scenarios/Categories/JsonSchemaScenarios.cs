namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class JsonSchemaScenarios
{
    public static ScenarioCategory Create() => new(
        "JSON Schema",
        "Structured output via --json-schema flag",
        new List<TestScenario>
        {
            new("JS-01", "--json-schema inline", "Structured output with inline JSON schema",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json --json-schema \"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"answer\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"answer\\\"]}\"",
                StdinText: "What is 2+2? Respond with just the number.",
                CostsApiCredits: true),

            new("JS-02", "--json-schema with multiple fields", "Structured output with multi-field schema",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json --json-schema \"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"answer\\\":{\\\"type\\\":\\\"string\\\"},\\\"confidence\\\":{\\\"type\\\":\\\"number\\\"}},\\\"required\\\":[\\\"answer\\\",\\\"confidence\\\"]}\"",
                StdinText: "What is 2+2? Rate your confidence from 0 to 1.",
                CostsApiCredits: true),
        });
}
