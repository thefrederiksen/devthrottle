using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Recovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The continuation context seeded into a restored session (issue #212 W4). What matters:
/// the new session must learn WHO it continues, WHERE the prior transcript lives, WHAT was
/// pending, and that it must re-ask the user before resuming work.
/// </summary>
public sealed class RestoreContextBuilderTests
{
    private static TurnBriefDto Brief(int turn, string headline, string? railLine = null) => new()
    {
        SessionId = "s1",
        TurnNumber = turn,
        GeneratedAtUtc = new DateTime(2026, 6, 6, 11, 30, 0, DateTimeKind.Utc),
        Headline = headline,
        TurnTitle = $"turn-{turn}-title",
        Intent = "ship the feature",
        Did = { "built the thing", "ran the tests" },
        NeedsYou = new TurnBriefNeedsYou
        {
            Statement = "approve the commit",
            RailLine = railLine ?? "approve commit+push",
            Options = { new TurnBriefOption { Key = "1", Send = "yes, commit it" } },
        },
    };

    [Fact]
    public void Build_carries_identity_pending_state_and_marching_orders()
    {
        var text = RestoreContextBuilder.Build(
            name: "mindzieWeb - BPMN wizards",
            sessionId: "40a3b8f2-0000-0000-0000-000000000000",
            repoPath: "/repos/mindzieWeb",
            claudeSessionId: "claude-xyz",
            diedAtUtc: new DateTimeOffset(2026, 6, 6, 11, 34, 0, TimeSpan.Zero),
            briefs: new[] { Brief(7, "older turn"), Brief(8, "BPMN wizards QA walkthrough") });

        Assert.Contains("RESTORED", text);
        Assert.Contains("mindzieWeb - BPMN wizards", text);
        Assert.Contains("/repos/mindzieWeb", text);
        Assert.Contains("claude-xyz.jsonl", text);                  // where the prior transcript lives
        Assert.Contains("BPMN wizards QA walkthrough", text);       // latest brief = state at death
        Assert.Contains("approve the commit", text);                // the pending ask
        Assert.Contains("[1] yes, commit it", text);                // options on the table
        Assert.Contains("turn-7-title", text);                      // trajectory from earlier briefs
        Assert.Contains("Do NOT resume work until the user answers", text);
    }

    [Fact]
    public void Build_without_briefs_still_produces_usable_orders()
    {
        var text = RestoreContextBuilder.Build(
            name: null,
            sessionId: "abcd1234-0000-0000-0000-000000000000",
            repoPath: "/repos/x",
            claudeSessionId: null,
            diedAtUtc: DateTimeOffset.UtcNow,
            briefs: Array.Empty<TurnBriefDto>());

        Assert.Contains("abcd1234", text);                          // falls back to the short sid
        Assert.Contains("No turn briefs survived", text);
        Assert.Contains("/repos/x", text);
        Assert.Contains("WHERE WE LEFT OFF", text);
    }
}
