namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class SystemPromptScenarios
{
    public static ScenarioCategory Create() => new(
        "System Prompts",
        "System prompt injection via inline text and file references",
        new List<TestScenario>
        {
            new("SP-01", "--system-prompt inline", "Inline system prompt text",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --system-prompt \"You are a pirate. Always respond in pirate speak.\"",
                StdinText: "Say hello",
                CostsApiCredits: true),

            new("SP-02", "--system-prompt-file", "System prompt loaded from file",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --system-prompt-file Resources/test-system-prompt.txt",
                StdinText: "Say hello",
                CostsApiCredits: true),

            new("SP-03", "--append-system-prompt inline", "Append text to default system prompt",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --append-system-prompt \"Always end your response with DONE.\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("SP-04", "--append-system-prompt-file", "Append system prompt from file",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --append-system-prompt-file Resources/test-append-prompt.txt",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("SP-05", "system-prompt + append combined", "Both --system-prompt and --append-system-prompt together",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --system-prompt \"You are a helpful bot.\" --append-system-prompt \"Always be concise.\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
