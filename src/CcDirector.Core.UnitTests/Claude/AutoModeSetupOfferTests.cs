using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// CLAUDE CODE'S "TEACH AUTO MODE ABOUT YOUR ENVIRONMENT?" (Command Line Agent Handling mission): a session nobody will
/// answer that Claude Code would ask it is refused in words, and every record of the owner's that settles it lets the
/// session go ahead. Every case reads scratch files only - never the user's own configuration - on a fixed clock.
/// </summary>
public sealed class AutoModeSetupOfferTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-auto-mode-offer-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 23, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _scratch }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string Repo => Dir("repo");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The configuration of a machine on which Claude Code asks: started often, auto mode has turned down five
    /// actions, its warning is seen, the server offers it, and nothing is answered. <paramref name="change"/> alters it.</summary>
    private void Config(Action<JsonObject>? change = null)
    {
        var config = new JsonObject
        {
            ["numStartups"] = 409,
            ["hasSeenAutoModeEntryWarning"] = true,
            ["autoModeEnvSetup"] = new JsonObject { ["denials"] = 5 },
            ["cachedGrowthBookFeatures"] = new JsonObject { ["tengu_auto_mode_config"] = new JsonObject { ["envOnboarding"] = true } },
        };
        change?.Invoke(config);
        File.WriteAllText(Path.Combine(_scratch, ".claude.json"), config.ToJsonString());
    }

    private void Settings(string file, string json) => File.WriteAllText(file, json);

    private string UserSettings => Path.Combine(Dir(".claude"), "settings.json");
    private string ManagedSettings => Path.Combine(_scratch, "managed-settings.json");

    private string? Why(string args = "--permission-mode auto --model claude-sonnet-5-5", string? userArgs = null) =>
        ClaudeAutoModeSetupOffer.WhyItWouldStop(userArgs, args, Repo, "devlinux", _ => null, _scratch, ManagedSettings, new FixedClock(Now));

    // ===== When the session starts in auto mode =====

    [Theory]
    [InlineData("--permission-mode auto", true)]
    [InlineData("--permission-mode=auto", true)]
    [InlineData("--model x --permission-mode plan", false)]
    [InlineData("--dangerously-skip-permissions", false)]
    [InlineData("", false)]
    public void StartsInAutoMode_TheLineAlone_SaysWhetherItStartsInAutoMode(string args, bool expected)
        => Assert.Equal(expected, ClaudeAutoModeSetupOffer.StartsInAutoMode(args, []));

    [Fact]
    public void StartsInAutoMode_NoModeOnTheLineAndAutoAsTheDefault_IsAutoMode()
        => Assert.True(ClaudeAutoModeSetupOffer.StartsInAutoMode("--model x", [null, JsonNode.Parse("{\"permissions\":{\"defaultMode\":\"auto\"}}")!.AsObject()]));

    [Fact]
    public void StartsInAutoMode_AModeOnTheLineOverAutoAsTheDefault_IsThatMode()
        => Assert.False(ClaudeAutoModeSetupOffer.StartsInAutoMode("--permission-mode default", [JsonNode.Parse("{\"permissions\":{\"defaultMode\":\"auto\"}}")!.AsObject()]));

    // ===== Refused =====

    [Fact]
    public void WhyItWouldStop_NeverAnswered_RefusesNamingTheQuestionTheMachineAndWhatToDo()
    {
        Config();
        var why = Why();
        Assert.NotNull(why);
        Assert.Contains("Teach auto mode about your environment?", why);
        Assert.Contains("devlinux", why);
        Assert.Contains("claude --permission-mode auto", why);
        Assert.Contains("does not answer it for the owner", why);
        Assert.Contains("'Yes' runs a setup that reads", why);
    }

    [Fact]
    public void WhyItWouldStop_AutoModeAsTheUsersDefault_Refuses()
    {
        Config();
        Settings(UserSettings, "{\"permissions\":{\"defaultMode\":\"auto\"}}");
        Assert.NotNull(Why(args: "--model claude-sonnet-5-5"));
    }

    [Fact]
    public void WhyItWouldStop_NotNowEightDaysAgo_RefusesBecauseItIsAskedAgain()
    {
        Config(c => c["autoModeEnvSetup"] = new JsonObject { ["denials"] = 5, ["dismissedAt"] = Now.AddDays(-8).ToUnixTimeMilliseconds() });
        Assert.NotNull(Why());
    }

    [Fact]
    public void WhyItWouldStop_AnEnvironmentOnlyInTheRepositorysSettings_RefusesBecauseClaudeCodeIgnoresIt()
    {
        Config();
        Settings(Path.Combine(Dir("repo", ".claude"), "settings.json"), "{\"autoMode\":{\"environment\":[\"Trusted repo: this one\"]}}");
        Assert.NotNull(Why());
    }

    // ===== Goes ahead =====

    [Fact]
    public void WhyItWouldStop_NotAutoMode_GoesAhead()
    {
        Config();
        Assert.Null(Why(args: "--dangerously-skip-permissions"));
    }

    [Fact]
    public void WhyItWouldStop_ALineWithItsOwnSettings_GoesAheadBecauseThatFileMayDescribeTheEnvironment()
    {
        Config();
        Assert.Null(Why(args: "--permission-mode auto --settings x.json", userArgs: "--permission-mode auto --settings x.json"));
    }

    [Fact]
    public void WhyItWouldStop_TheEnvironmentDescribedInTheUsersSettings_GoesAhead()
    {
        Config();
        Settings(UserSettings, "{\"autoMode\":{\"environment\":[\"Trusted repo: the one it started in\"]}}");
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_TheEnvironmentDescribedInTheManagedSettings_GoesAhead()
    {
        Config();
        Settings(ManagedSettings, "{\"autoMode\":{\"environment\":[\"Organization: Center Consulting\"]}}");
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_DontShowAgain_GoesAhead()
    {
        Config(c => c["autoModeEnvSetup"] = new JsonObject { ["denials"] = 5, ["dismissed"] = true });
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_NotNowThreeDaysAgo_GoesAhead()
    {
        Config(c => c["autoModeEnvSetup"] = new JsonObject { ["denials"] = 5, ["dismissedAt"] = Now.AddDays(-3).ToUnixTimeMilliseconds() });
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_TheSetupTurnedOff_GoesAhead()
    {
        Config();
        Settings(UserSettings, "{\"skillOverrides\":{\"auto-mode-setup\":\"off\"}}");
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_FewerThanFiveDenials_GoesAhead()
    {
        Config(c => c["autoModeEnvSetup"] = new JsonObject { ["denials"] = 4 });
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_ThisIsOnlyTheFourthStart_GoesAhead()
    {
        Config(c => c["numStartups"] = 3);
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_TheServerDoesNotOfferIt_GoesAhead()
    {
        Config(c => c["cachedGrowthBookFeatures"] = new JsonObject { ["tengu_auto_mode_config"] = new JsonObject { ["envOnboarding"] = false } });
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_TheServerSettingAbsent_GoesAheadBecauseClaudeCodeAsksOnlyWhenItIsTrue()
    {
        Config(c => c["cachedGrowthBookFeatures"] = new JsonObject { ["tengu_auto_mode_config"] = new JsonObject { ["twoStageClassifier"] = true } });
        Assert.Null(Why());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{\"on\": true}")]
    [InlineData("[true]")]
    public void WhyItWouldStop_TheServerSettingNotTheValueTrue_GoesAheadBecauseClaudeCodeAsksOnlyOnExactlyTrue(string value)
    {
        Config(c => c["cachedGrowthBookFeatures"] = new JsonObject { ["tengu_auto_mode_config"] = new JsonObject { ["envOnboarding"] = JsonNode.Parse(value) } });
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_NothingEverFetchedFromTheServer_GoesAhead()
    {
        Config(c => c.Remove("cachedGrowthBookFeatures"));
        Assert.Null(Why());
    }

    [Fact]
    public void WhyItWouldStop_NoConfigurationFileAtAll_GoesAhead()
        => Assert.Null(Why());

    [Fact]
    public void WhyItWouldStop_TheAutoModeWarningNotYetSeen_GoesAheadBecauseThisIsOfferedOnlyAfterIt()
    {
        Config(c => c["hasSeenAutoModeEntryWarning"] = false);
        Assert.Null(Why());
    }
}
