namespace CcDirector.CliExplorer.Scenarios.Categories;

public static class DirectoryAndSettingsScenarios
{
    public static ScenarioCategory Create() => new(
        "Directories and Settings",
        "Additional directory context and settings source control",
        new List<TestScenario>
        {
            new("DS-01", "--add-dir with relative path", "Add additional directory context",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --add-dir ../CcDirector.Core",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("DS-02", "--setting-sources user", "Restrict settings to user-level only",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --setting-sources user",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),

            new("DS-03", "--settings with file", "Pass custom settings file",
                "-p --dangerously-skip-permissions --max-turns 1 --model haiku --settings Resources/test-settings.json",
                StdinText: "Say just the word pong",
                CostsApiCredits: true),
        });
}
