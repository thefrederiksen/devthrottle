using System.Text.Json.Serialization;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The session's declared PURPOSE, chosen once at creation (issue #211). This is Type,
/// not Status: ActivityState/StatusColor change moment to moment; SessionType is the
/// session's identity - it answers "what is this session FOR" on the same axis as
/// <see cref="CcDirector.Core.Agents.AgentKind"/> (which agent runs) but orthogonal to
/// it (why the session exists). Immutable after creation.
///
/// Every type must earn its place with a BEHAVIORALLY DIFFERENT playbook (see
/// <see cref="SessionTypePlaybooks"/>), not just a different label - Review/Research/Ops
/// were considered and deferred for exactly that reason.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionType
{
    /// <summary>Default - today's behavior. Code gets written, bugs get fixed, things ship.
    /// No playbook is seeded. Pre-#211 sessions and old clients deserialize to this.</summary>
    Implement = 0,

    /// <summary>Talk only: answer, explore, explain - never modify files or commit.
    /// Work the discussion lands on becomes an issue or a handover, not edits here.</summary>
    Discuss = 1,

    /// <summary>The flagship rule: NEVER fix the bug here. Reproduce, root-cause, then
    /// file an issue complete enough that an Implement session can pick it up cold.</summary>
    BugReport = 2,

    /// <summary>The issue-only clerk (issue #225, the #220/#221 role as a first-class type):
    /// a STANDING session that files GitHub issues for any number of asks and never writes
    /// code. Broader than BugReport (one bug in, one issue out) - this is the Product group's
    /// submit member.</summary>
    IssueSubmitter = 3,

    /// <summary>Verify, never fix (issue #225): test what was built against what was asked,
    /// and report findings as issues or hand them to the submit session - never edit the code
    /// itself. The Product group's QA member.</summary>
    QA = 4,
}

/// <summary>
/// The per-type playbook prompts (issue #211), seeded into the agent when a typed
/// session starts via the EXISTING pre-prompt readiness gate - no new delivery
/// machinery. Enforcement is deliberately SOFT: the playbook instructs the agent, the
/// badge reminds the human, and drift is visible in the wingman brief. No hard blocking
/// of commits in v1 - that would fight the agent for little gain.
/// </summary>
public static class SessionTypePlaybooks
{
    /// <summary>The playbook text seeded at session start, or null for types that run
    /// with today's default behavior (Implement).</summary>
    public static string? For(SessionType type) => type switch
    {
        SessionType.Discuss =>
            "This is a DISCUSSION session. Answer, explore, explain - think out loud with me. " +
            "Do NOT modify files, do NOT commit, do NOT run state-changing commands; reading code " +
            "and running read-only commands to inform the discussion is fine. If we land on concrete " +
            "work to do, offer to draft a GitHub issue or a handover document for a separate " +
            "Implement session instead of doing the work here.",

        SessionType.BugReport =>
            "This is a BUG-REPORT session. NEVER fix the bug here - no file edits, no commits; " +
            "your entire job is to investigate and file. Reproduce the problem, root-cause it, then " +
            "file a GitHub issue in this repo's tracker containing: the symptom, exact reproduction " +
            "steps, the root cause, evidence (file:line references), and a step-by-step fix plan " +
            "that an implementation agent could execute cold, without this conversation. If you " +
            "cannot determine a confident fix plan, label the issue needs-design and state exactly " +
            "what is unresolved - never pretend. Fixing happens later, in a separate Implement session. " +
            // Issue #236: a bug session is a transaction with a knowable end. Once the issue
            // exists, the mission is over - say so plainly (the issue URL in your reply is what
            // the wingman watches for to offer the one-click close), and rename yourself so the
            // rail history reads well (you have $CC_DIRECTOR_API and $CC_SESSION_ID).
            "After the issue is filed your work is COMPLETE: print the issue number and URL, then " +
            "rename this session by sending PATCH $CC_DIRECTOR_API/sessions/$CC_SESSION_ID with JSON " +
            "body {\"name\":\"Bug: #<number> <short title>\"}, and STOP. Do not start fixing - that is " +
            "a separate Implement session's job.",

        SessionType.IssueSubmitter =>
            "This is an ISSUE-SUBMITTER session - a standing clerk for filing GitHub issues. " +
            "You NEVER write code, edit files, or commit; your one job is to turn each ask into a " +
            "well-formed GitHub issue in this repo's tracker. For every request: capture the intent, " +
            "add enough context for an implementer to start cold (what is wanted, why, acceptance, " +
            "any evidence with file:line), and file it. If an ask is too vague to act on, file it " +
            "with a needs-design label naming exactly what is unresolved. Stay open for the next ask - " +
            "you are not done after one issue. Implementing happens in a separate Implement session.",

        SessionType.QA =>
            "This is a QA session. Test and verify what was built against what was ASKED - reproduce " +
            "the claimed behavior, check the acceptance criteria, probe edge cases. You NEVER fix what " +
            "you find: report each finding as a GitHub issue (or hand it to the submit-issues session) " +
            "with exact reproduction steps and evidence (file:line, output, screenshots). A clean pass " +
            "is a valid result - say so plainly. Fixing is the Implement session's job, not yours.",

        _ => null,
    };

    /// <summary>
    /// Compose the one seed text dispatched at session start: the type's playbook first
    /// (ground rules), then the caller's pre-prompt (the task) - the #236 bug-session
    /// flow needs both in that order. Null when there is nothing to send (an Implement
    /// session with no pre-prompt). Pure - unit-tested.
    /// </summary>
    public static string? ComposeSeed(SessionType type, string? prePrompt)
    {
        var playbook = For(type);
        var hasPre = !string.IsNullOrWhiteSpace(prePrompt);
        if (playbook is null) return hasPre ? prePrompt : null;
        return hasPre ? playbook + "\n\n" + prePrompt : playbook;
    }
}
