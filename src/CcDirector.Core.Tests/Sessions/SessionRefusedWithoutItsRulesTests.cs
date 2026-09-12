using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A session that starts without the fleet preamble is a session that does not know the rules it
/// runs under - including the rule that keeps an assistant's name out of the owner's repositories
/// and his clients' deliverables. It used to start anyway and write one line to a file log.
///
/// THESE TESTS EXIST ON A BOUNDARY THAT WAS PREVIOUSLY UNCOVERED, and the boundary is the point.
/// Both installers already had tests for their own failure branches - CodexHookInstaller even has
/// one asserting it returns false and does not clobber - because making a file malformed and calling
/// a function is cheap. What the CALLER did with that false was untested, because reaching it means
/// creating a session. So the producer's failure was covered, the consumer's response was not, and a
/// green suite testing the producer read as though the pair were covered.
/// </summary>
public sealed class SessionRefusedWithoutItsRulesTests
{
    private static SessionManager ManagerWith(Func<string?> claudeHooks, Func<bool> codexHooks) =>
        new(new AgentOptions
        {
            ClaudePath = TestShell.Path,
            CodexPath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2,
        })
        {
            InstallClaudeHooks = claudeHooks,
            InstallCodexHooks = codexHooks,
        };

    /// <summary>
    /// THE REGRESSION TEST for Claude. Before this change the session started and the only trace was
    /// a file-log line, so a session that did not know the rules was indistinguishable from one that
    /// did.
    /// </summary>
    [Fact]
    public void ClaudeHookInstallFails_TheSessionIsRefused_NotLaunchedQuietly()
    {
        var manager = ManagerWith(claudeHooks: () => null, codexHooks: () => true);

        var ex = Assert.Throws<InvalidOperationException>(
            () => manager.CreateSession(Path.GetTempPath(), AgentKind.ClaudeCode, null, SessionBackendType.ConPty, null));

        // The message has to carry the CONSEQUENCE, not just the mechanism - the person reading it at
        // the moment it happens is the only one who can act on it.
        Assert.Contains("without the fleet preamble", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rules", ex.Message, StringComparison.OrdinalIgnoreCase);
        // AND IT MUST NAME THE EXACT PATH. A refusal without a fix is a wall, and this one has to be
        // actionable by somebody who has never heard of a hook - a path they can look at beats a
        // description of a mechanism they do not know exists.
        Assert.Contains(CcDirector.Core.Claude.ClaudeHookInstaller.HookDirectory(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("writable", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing was left running.
        Assert.Empty(manager.ListSessions());
    }

    /// <summary>The same for Codex, whose hook is the preamble channel outright.</summary>
    [Fact]
    public void CodexHookInstallFails_TheSessionIsRefused_NotLaunchedQuietly()
    {
        var manager = ManagerWith(claudeHooks: () => "settings.json", codexHooks: () => false);

        var ex = Assert.Throws<InvalidOperationException>(
            () => manager.CreateSession(Path.GetTempPath(), AgentKind.Codex, null, SessionBackendType.ConPty, null));

        Assert.Contains("without the fleet preamble", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CcDirector.Core.Codex.CodexHookInstaller.HooksJsonPath(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.ListSessions());
    }

    /// <summary>
    /// The refusal must not over-correct. When the hooks install, the session starts exactly as
    /// before - a refusal that fired always would be just as wrong in the other direction, and this
    /// is what stops the fix being "never launch anything".
    /// </summary>
    [Fact]
    public void HooksInstall_TheSessionStartsAsBefore()
    {
        var manager = ManagerWith(claudeHooks: () => "settings.json", codexHooks: () => true);

        var session = manager.CreateSession(Path.GetTempPath(), AgentKind.ClaudeCode, null, SessionBackendType.ConPty, null);

        Assert.NotNull(session);
        Assert.Single(manager.ListSessions());
    }

    /// <summary>
    /// AN AGENT THAT WAS NEVER GOING TO GET A HOOK IS NOT A FAILURE. Pi carries the preamble on its
    /// launch system prompt, so refusing it over a missing hook would be refusing over a state that
    /// is not a fault - which is the same class of error as the fold this change removes, pointing
    /// the other way.
    /// </summary>
    [Fact]
    public void AnAgentWithNoHookChannel_IsNotRefused()
    {
        var manager = ManagerWith(claudeHooks: () => null, codexHooks: () => false);

        var session = manager.CreateSession(Path.GetTempPath(), AgentKind.Pi, null, SessionBackendType.ConPty, null);

        Assert.NotNull(session);
        Assert.Single(manager.ListSessions());
    }
}
