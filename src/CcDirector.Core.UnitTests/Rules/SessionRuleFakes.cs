using System.Text.Json;
using CcDirector.Core.Git;
using CcDirector.Core.Rules;

namespace CcDirector.Core.Tests.Rules;

/// <summary>
/// The Gateway's two session-rule routes (W1 and W5), answered in memory. It remembers every call, so a
/// test can assert what the Director ASKED as well as what it did with the answer.
/// </summary>
internal sealed class FakeSessionRuleGateway : ISessionRuleGateway
{
    public List<(string Machine, string Repo)> Fetches { get; } = new();
    public List<IReadOnlyList<JsonElement>> Posts { get; } = new();

    /// <summary>What a fetch answers, by machine and repo. Default: no rules reach anything.</summary>
    public Func<string, string, EffectiveHookRulesFetch> FetchAnswer { get; set; }
        = (_, _) => EffectiveHookRulesFetch.Answered(Array.Empty<HookRuleEntry>());

    /// <summary>What a post answers. Default: every decision accepted.</summary>
    public Func<IReadOnlyList<JsonElement>, HookFiringsPost> PostAnswer { get; set; }
        = decisions => HookFiringsPost.Answered(new HookFiringsResponse { Accepted = decisions.Count, Refused = Array.Empty<HookFiringRefusal>() });

    public Task<EffectiveHookRulesFetch> FetchEffectiveHookRulesAsync(string machine, string repo, CancellationToken ct)
    {
        Fetches.Add((machine, repo));
        return Task.FromResult(FetchAnswer(machine, repo));
    }

    public Task<HookFiringsPost> PostHookFiringsAsync(IReadOnlyList<JsonElement> decisions, CancellationToken ct)
    {
        // Cloned so the record outlives the document the caller parsed it from.
        Posts.Add(decisions.Select(d => d.Clone()).ToList());
        return Task.FromResult(PostAnswer(decisions));
    }
}

/// <summary>
/// A git that answers <c>git remote get-url origin</c> from a table, per working directory, and starts no
/// process. An unknown directory answers the way a real checkout with no origin does: exit 2.
/// </summary>
internal sealed class FakeOriginGit : GitCommandRunner
{
    public Dictionary<string, GitCommandResult> Answers { get; } = new(StringComparer.Ordinal);
    public List<string> Asked { get; } = new();

    public void Origin(string workingDirectory, string url)
        => Answers[workingDirectory] = new GitCommandResult { Success = true, ExitCode = 0, Output = url };

    public override Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
    {
        Asked.Add(workingDirectory + " :: " + string.Join(' ', args));
        if (!args.SequenceEqual(new[] { "remote", "get-url", "origin" }))
            throw new InvalidOperationException($"This fake answers only 'remote get-url origin', was asked: {string.Join(' ', args)}");
        return Task.FromResult(Answers.TryGetValue(workingDirectory, out var answer)
            ? answer
            : new GitCommandResult { Success = false, ExitCode = 2, Error = "error: No such remote 'origin'" });
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => UtcNow += by;
}

internal static class RuleSamples
{
    public static HookRuleEntry DenyEcho(string id = "3f2c0000-0000-0000-0000-000000000001", string appliesTo = "repo", int rank = 3) => new()
    {
        Id = id,
        Rank = rank,
        AppliesTo = appliesTo,
        Event = "before_command",
        Tool = "shell",
        Match = new[] { "echo RULE-PROBE" },
        Result = "deny",
        Reason = "Do not run the probe here.",
        State = "dry_run",
    };
}
