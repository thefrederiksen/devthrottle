using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The session's declared PURPOSE, chosen once at creation (issue #211, refined for the
/// CenCon four-role workflow in #254). This is Type, not Status: ActivityState/StatusColor
/// change moment to moment; SessionType is the session's identity - it answers "what is this
/// session FOR" on the same axis as <see cref="CcDirector.Core.Agents.AgentKind"/> (which
/// agent runs) but orthogonal to it (why the session exists). Immutable after creation.
///
/// Every type must earn its place with a BEHAVIORALLY DIFFERENT playbook (see
/// <see cref="SessionTypePlaybooks"/>), not just a different label.
///
/// SERIALIZATION (issue #254): the value is persisted by its enum NAME via
/// <see cref="SessionTypeJsonConverter"/>. Two members were renamed in #254 -
/// Implement-&gt;Developer and BugReport-&gt;Product - keeping their integer values. The
/// converter reads the legacy names ("Implement", "BugReport") as aliases so every session
/// persisted before the rename still loads. Integer values are deliberately stable.
/// </summary>
[JsonConverter(typeof(SessionTypeJsonConverter))]
public enum SessionType
{
    /// <summary>Default - today's behavior. Code gets written, bugs get fixed, things ship.
    /// No playbook is seeded. Pre-#211 sessions and old clients deserialize to this. Renamed
    /// from "Implement" in #254 (legacy name still deserializes); integer value unchanged.</summary>
    Developer = 0,

    /// <summary>Talk only: answer, explore, explain - never modify files or commit.
    /// Work the discussion lands on becomes an issue or a handover, not edits here.</summary>
    Discuss = 1,

    /// <summary>Scope the work and file issues for the developer - NEVER fix here. Renamed
    /// from "BugReport" in #254 (legacy name still deserializes); integer value unchanged.
    /// Reproduce/root-cause, then file an issue complete enough that a Developer session can
    /// pick it up cold. The Product group's product member.</summary>
    Product = 2,

    /// <summary>LEGACY (issue #225, hidden in #254): the issue-only clerk. Kept defined so any
    /// session persisted as IssueSubmitter still loads, but it is no longer offered in the New
    /// Session picker or any group - the Product/Support roles cover its job now.</summary>
    IssueSubmitter = 3,

    /// <summary>Verify, never fix (issue #225): test what was built against what was asked,
    /// and report findings as issues or hand them to the product session - never edit the code
    /// itself. The Product group's QA member.</summary>
    QA = 4,

    /// <summary>Triage and file, never fix (issue #254): handle incoming questions and support
    /// requests, answer what can be answered directly, and for real bugs or feature gaps file a
    /// GitHub issue complete enough for a Developer session to pick up cold. Never edits code.
    /// The Product group's support member.</summary>
    Support = 5,
}

/// <summary>
/// String &lt;-&gt; <see cref="SessionType"/> conversion with legacy-name aliases (issue #254).
/// One canonical home for parsing a type name from JSON, the Control API, or the Gateway, so
/// the two members renamed in #254 (Implement-&gt;Developer, BugReport-&gt;Product) keep loading
/// under their old names everywhere - not just in the persistence layer.
/// </summary>
public static class SessionTypeNames
{
    /// <summary>Parse a type name, accepting the canonical member names, the #254 legacy
    /// aliases (Implement-&gt;Developer, BugReport-&gt;Product), and the integer values. Returns
    /// false (and leaves <paramref name="type"/> at Developer) for null/empty/unrecognized
    /// input so the caller decides whether that is an error or the default.</summary>
    public static bool TryParse(string? value, out SessionType type)
    {
        type = SessionType.Developer;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        // Legacy member names (pre-#254). Explicit aliases, not a fallback: these are the
        // exact strings older sessions/clients wrote and must keep meaning the same type.
        if (string.Equals(trimmed, "Implement", StringComparison.OrdinalIgnoreCase))
        {
            type = SessionType.Developer;
            return true;
        }
        if (string.Equals(trimmed, "BugReport", StringComparison.OrdinalIgnoreCase))
        {
            type = SessionType.Product;
            return true;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out type)
            && Enum.IsDefined(typeof(SessionType), type);
    }
}

/// <summary>
/// Serializes <see cref="SessionType"/> by its canonical enum NAME and reads it back through
/// <see cref="SessionTypeNames.TryParse"/> so legacy names and integer values still load
/// (issue #254). Replaces <c>JsonStringEnumConverter</c>, which has no alias support and would
/// have broken every session persisted under "Implement"/"BugReport" once those were renamed.
/// </summary>
public sealed class SessionTypeJsonConverter : JsonConverter<SessionType>
{
    public override SessionType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Integer form (defensive - older/other writers may emit the numeric value).
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return Enum.IsDefined(typeof(SessionType), number)
                ? (SessionType)number
                : SessionType.Developer; // unknown value -> documented default (old-client behavior)
        }

        var name = reader.GetString();
        // Unknown/absent name -> Developer: matches the long-standing "pre-#211 and old clients
        // deserialize to the default type" contract, so an unfamiliar value never fails a load.
        return SessionTypeNames.TryParse(name, out var type) ? type : SessionType.Developer;
    }

    public override void Write(Utf8JsonWriter writer, SessionType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
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
    /// with today's default behavior (Developer).</summary>
    public static string? For(SessionType type) => type switch
    {
        SessionType.Discuss =>
            "This is a DISCUSSION session. Answer, explore, explain - think out loud with me. " +
            "Do NOT modify files, do NOT commit, do NOT run state-changing commands; reading code " +
            "and running read-only commands to inform the discussion is fine. If we land on concrete " +
            "work to do, offer to draft a GitHub issue or a handover document for a separate " +
            "Developer session instead of doing the work here.",

        SessionType.Product =>
            "This is a PRODUCT session. NEVER fix the bug or write the feature here - no file edits, " +
            "no commits; your entire job is to scope and file. Reproduce the problem, root-cause it, then " +
            "file a GitHub issue in this repo's tracker containing: the symptom, exact reproduction " +
            "steps, the root cause, evidence (file:line references), and a step-by-step fix plan " +
            "that a developer agent could execute cold, without this conversation. If you " +
            "cannot determine a confident fix plan, label the issue needs-design and state exactly " +
            "what is unresolved - never pretend. Implementing happens later, in a separate Developer session. " +
            // Issue #236: a product/bug session is a transaction with a knowable end. Once the issue
            // exists, the mission is over - say so plainly (the issue URL in your reply is what
            // the wingman watches for to offer the one-click close), and rename yourself so the
            // rail history reads well (you have $CC_DIRECTOR_API and $CC_SESSION_ID).
            "After the issue is filed your work is COMPLETE: print the issue number and URL, then " +
            "rename this session by sending PATCH $CC_DIRECTOR_API/sessions/$CC_SESSION_ID with JSON " +
            "body {\"name\":\"Bug: #<number> <short title>\"}, and STOP. Do not start fixing - that is " +
            "a separate Developer session's job.",

        SessionType.IssueSubmitter =>
            "This is an ISSUE-SUBMITTER session - a standing clerk for filing GitHub issues. " +
            "You NEVER write code, edit files, or commit; your one job is to turn each ask into a " +
            "well-formed GitHub issue in this repo's tracker. For every request: capture the intent, " +
            "add enough context for an implementer to start cold (what is wanted, why, acceptance, " +
            "any evidence with file:line), and file it. If an ask is too vague to act on, file it " +
            "with a needs-design label naming exactly what is unresolved. Stay open for the next ask - " +
            "you are not done after one issue. Implementing happens in a separate Developer session.",

        SessionType.QA =>
            "This is a QA session. Test and verify what was built against what was ASKED - reproduce " +
            "the claimed behavior, check the acceptance criteria, probe edge cases. You NEVER fix what " +
            "you find: report each finding as a GitHub issue (or hand it to the product session) " +
            "with exact reproduction steps and evidence (file:line, output, screenshots). A clean pass " +
            "is a valid result - say so plainly. Fixing is the Developer session's job, not yours.",

        SessionType.Support =>
            "This is a SUPPORT session. Handle incoming user questions and support requests: answer " +
            "what you can answer directly from the code and docs (reading code and running read-only " +
            "commands is fine). You NEVER edit code, never commit, never fix bugs here. For a real bug " +
            "or a feature gap, file a GitHub issue in this repo's tracker complete enough for a Developer " +
            "session to pick up cold: the symptom, reproduction steps, evidence (file:line), and what a " +
            "good outcome looks like. Triage first, answer what you can, file the rest - fixing happens " +
            "in a separate Developer session.",

        _ => null,
    };

    /// <summary>
    /// Compose the one seed text dispatched at session start: the type's playbook first
    /// (ground rules), then the caller's pre-prompt (the task) - the #236 bug-session
    /// flow needs both in that order. Null when there is nothing to send (a Developer
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
