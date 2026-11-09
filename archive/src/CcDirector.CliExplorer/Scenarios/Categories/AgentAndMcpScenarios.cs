namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class AgentAndMcpScenarios
{
    public static ScenarioCategory Create() => new(
        "Agents and MCP",
        "Agent definitions and MCP server configuration",
        new List<TestScenario>
        {
            new("AM-01", "--mcp-config with file", "Load MCP server configuration from file",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --mcp-config Resources/test-mcp-config.json",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("AM-02", "--strict-mcp-config flag", "Strict MCP config validation",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --mcp-config Resources/test-mcp-config.json --strict-mcp-config",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("AM-03", "--agents with inline JSON", "Define agents via inline JSON",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --agents '{\"testAgent\":{\"model\":\"haiku\",\"tools\":[\"Read\"]}}'",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
