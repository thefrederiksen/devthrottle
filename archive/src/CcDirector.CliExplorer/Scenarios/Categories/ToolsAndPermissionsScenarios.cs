namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class ToolsAndPermissionsScenarios
{
    public static ScenarioCategory Create() => new(
        "Tools and Permissions",
        "Controlling available tools and permission modes",
        new List<TestScenario>
        {
            new("TP-01", "--tools empty string", "Disable all tools with empty string",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --tools \"\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-02", "--tools default", "Explicitly request default toolset",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --tools default",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-03", "--tools specific list", "Restrict to specific tools",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --tools \"Read,Bash\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-04", "--allowedTools Read", "Allow only Read tool",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --allowedTools \"Read\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-05", "--allowedTools with pattern", "Allow Bash with git pattern only",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --allowedTools \"Bash(git *)\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-06", "--disallowedTools Bash", "Disallow Bash tool",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --disallowedTools \"Bash\"",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-07", "--permission-mode plan", "Plan permission mode",
                "-p --max-turns 1 --model haiku --permission-mode plan",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-08", "--permission-mode acceptEdits", "Accept edits permission mode",
                "-p --max-turns 1 --model haiku --permission-mode acceptEdits",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-09", "--permission-mode bypassPermissions", "Bypass all permissions (same as --dangerously-skip-permissions)",
                "-p --max-turns 1 --model haiku --permission-mode bypassPermissions",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("TP-10", "--disable-slash-commands", "Disable slash commands",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --disable-slash-commands",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
