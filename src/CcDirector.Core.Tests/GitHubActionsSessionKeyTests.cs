using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// devthrottle_internal#2311, review finding S2-F13: in a team the Gateway decides whose a session is from its key
/// row, and the first Director to key a session id keeps it. A GitHub Actions session runs no local agent, so it
/// used to get no key at all and was listed to the Gateway with none - a colleague's Director could key it first.
/// It now gets a key like any other session, minted before it joins the roster.
/// </summary>
public sealed class GitHubActionsSessionKeyTests
{
    private static RemoteSessionConfig NewIssueConfig() => new()
    {
        Owner = "acme",
        Repo = "widget",
        BaseBranch = "main",
        TriggerMode = RemoteTriggerMode.NewIssue,
        InitialPrompt = "do the thing",
        PollIntervalMs = 25,
    };

    [Fact]
    public void CreateGitHubActionsSession_WithAGateway_MintsTheKeyBeforeTheSessionIsListed()
    {
        using var sm = new SessionManager(new AgentOptions());
        var events = new List<string>();
        sm.GatewayUrl = "https://gateway.example";
        sm.GatewaySessionCredentialSource = id => { events.Add($"key:{id}"); return "a-session-key"; };
        sm.OnSessionCreated += s => events.Add($"listed:{s.Id}:{sm.ListSessions().Any(x => x.Id == s.Id)}");

        var session = sm.CreateGitHubActionsSession(NewIssueConfig(), new StubGitHubClient());

        Assert.Equal(new[] { $"key:{session.Id}", $"listed:{session.Id}:True" }, events);
        session.Dispose();
    }

    [Fact]
    public void CreateGitHubActionsSession_KeySourceAtTheRosterAdd_HasNotYetSeenTheSessionInTheRoster()
    {
        using var sm = new SessionManager(new AgentOptions());
        bool? listedWhenKeyed = null;
        sm.GatewayUrl = "https://gateway.example";
        sm.GatewaySessionCredentialSource = id =>
        {
            listedWhenKeyed = sm.ListSessions().Any(s => s.Id == id);
            return "a-session-key";
        };

        var session = sm.CreateGitHubActionsSession(NewIssueConfig(), new StubGitHubClient());

        Assert.False(listedWhenKeyed);
        session.Dispose();
    }

    [Fact]
    public void CreateGitHubActionsSession_NoGateway_MintsNoKey()
    {
        using var sm = new SessionManager(new AgentOptions());
        var minted = 0;
        sm.GatewayUrl = null;
        sm.GatewaySessionCredentialSource = _ => { minted++; return "a-session-key"; };

        var session = sm.CreateGitHubActionsSession(NewIssueConfig(), new StubGitHubClient());

        Assert.Equal(0, minted);
        session.Dispose();
    }

    [Fact]
    public void CreateGitHubActionsSession_KeyMintThrows_TheKeyIsRevokedAndTheSessionIsNotListed()
    {
        using var sm = new SessionManager(new AgentOptions());
        var revoked = new List<Guid>();
        Guid? keyed = null;
        sm.GatewayUrl = "https://gateway.example";
        sm.GatewaySessionCredentialSource = id => { keyed = id; throw new InvalidOperationException("the key store is broken"); };
        sm.GatewaySessionCredentialRevoker = id => revoked.Add(id);

        Assert.Throws<InvalidOperationException>(() => sm.CreateGitHubActionsSession(NewIssueConfig(), new StubGitHubClient()));

        Assert.NotNull(keyed);
        Assert.Equal(new[] { keyed!.Value }, revoked);
        Assert.Empty(sm.ListSessions());
    }
}
