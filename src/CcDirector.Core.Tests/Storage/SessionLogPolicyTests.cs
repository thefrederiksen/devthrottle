using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// What made the per-session log a data-collection defect rather than a feature: it ran for every
/// session on every install, with no setting at all, writing every byte the terminal painted -
/// base64-encoded, uncapped, unaged - while nothing in the product ever read one. One machine held
/// 35 GB across 3,422 sessions, 99.6% of them belonging to sessions that no longer existed.
///
/// These tests hold the new default (off) and the switch that turns it on. The pair at the bottom is
/// the one that matters: they watch the MANAGER - the thing the Director actually calls - rather than
/// the config record, because a policy that reads correctly and is never consulted writes just as
/// much to disk as no policy at all.
///
/// CC_DIRECTOR_ROOT is pinned to a temp directory so the config.json and the session-logs folder read
/// and written here are this test's, never the real machine's.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class SessionLogPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevEnv;

    public SessionLogPolicyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-session-log-policy-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // The override is process-wide, and a machine set up for a terminal investigation may well
        // have it on - so a test about the DEFAULT has to clear it, or it would read the machine's
        // opinion and call it the default.
        _prevEnv = Environment.GetEnvironmentVariable(SessionLogConfig.EnvironmentVariable);
        Environment.SetEnvironmentVariable(SessionLogConfig.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable(SessionLogConfig.EnvironmentVariable, _prevEnv);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Session_logging_is_off_on_a_clean_install()
    {
        Assert.False(SessionLogConfig.IsEnabled());
    }

    [Fact]
    public void Session_logging_is_on_only_when_the_visible_setting_says_so()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionLogConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true },
        });

        Assert.True(SessionLogConfig.IsEnabled());
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void The_environment_override_wins_in_both_directions(string value, bool expected)
    {
        // Config says the opposite of the override, so a test that passed by accident - because the
        // config default happened to agree - cannot pass here.
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionLogConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = !expected },
        });
        Environment.SetEnvironmentVariable(SessionLogConfig.EnvironmentVariable, value);

        Assert.Equal(expected, SessionLogConfig.IsEnabled());
    }

    [Fact]
    public void A_nonsense_override_says_so_rather_than_guessing()
    {
        Environment.SetEnvironmentVariable(SessionLogConfig.EnvironmentVariable, "maybe");

        var ex = Assert.Throws<InvalidOperationException>(() => SessionLogConfig.IsEnabled());
        Assert.Contains(SessionLogConfig.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }
}
