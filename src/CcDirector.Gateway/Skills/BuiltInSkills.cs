namespace CcDirector.Gateway.Skills;

/// <summary>
/// A skill the Gateway ships: identity, the one line that rides every briefing, and the phrases that
/// should bring it to mind. The BODY is not here - it lives in an embedded markdown resource, read by
/// <see cref="BuiltInSkills.BodyFor"/>, so the text stays diffable against the skill file it came from
/// and cannot drift through C# string escaping.
/// </summary>
public sealed record SkillDefinition(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<string> Triggers);

/// <summary>
/// The skills the Gateway ships with (devthrottle_internal issue 995). These are exactly the three the
/// installer used to copy onto every machine - now held centrally and served, so fixing one is an edit
/// on the Gateway rather than a release every user has to take.
///
/// This list is the SEED SOURCE, not the served set: the seeder writes these into the skill store at
/// startup and the endpoints read the store. A skill that is not part of the product does NOT belong
/// here - it is a row in the register, authored through the Cockpit or the command line. This list
/// exists only so a brand-new Gateway has the skills DevThrottle considers part of the product; the
/// Fleet Manager's commands are one of those, because every account has a Fleet Manager (issue #2933).
/// </summary>
public static class BuiltInSkills
{
    private static readonly IReadOnlyList<SkillDefinition> Definitions = new[]
    {
        new SkillDefinition(
            Id: "dev-throttle",
            Name: "DevThrottle",
            Summary: "The product itself - the app, the command line tools, and how an agent drives the fleet.",
            Triggers: new[]
            {
                "devthrottle", "cc-director", "what cc tools", "list tools", "available tools",
                "session manager", "mission control",
            }),

        new SkillDefinition(
            Id: "fleet-comms",
            Name: "Fleet communication",
            Summary: "List, rename and open sessions across the fleet, and the rare queued message: who you may " +
                     "message, the inbox, and replies.",
            Triggers: new[]
            {
                "message another session", "talk to another session", "ask another session",
                "rename this session", "list sessions", "what sessions are running", "spawn a session",
                "fleet messaging",
            }),

        new SkillDefinition(
            Id: "move-session",
            Name: "Move a session",
            Summary: "Move a live session to another Director or slot: a right-sized handover, a fresh " +
                     "session, verification it picked the work up, then DELETE the original.",
            Triggers: new[]
            {
                "move session", "migrate session", "transfer session",
                "move it to the new director",
            }),

        new SkillDefinition(
            Id: "terminology",
            Name: "Terminology",
            Summary: "The words DevThrottle uses and what each one means - one word per idea, one idea " +
                     "per word.",
            Triggers: new[]
            {
                "what do we call", "what is the right word", "glossary", "vocabulary", "naming",
                "is it hold or snooze", "controller or supervisor",
            }),

        new SkillDefinition(
            Id: "fleet-manager",
            Name: "Fleet Manager",
            Summary: "The Fleet Manager's commands: start and own sessions, read the Wingman's reading of " +
                     "each stop, answer, snooze and close, and what is not built yet.",
            Triggers: new[]
            {
                "fleet manager", "you are the fleet manager", "what's waiting on me",
                "take me through them", "what did I miss",
            }),

        // FLEET LAW (issue #3240). These were published from a session and lived ONLY on the
        // Gateway - no file in any repository, no review, no test, and any session with a key
        // could rewrite what every agent in the fleet reads. They carry rules we treat as
        // binding, so they ship with the product and change by deployment like the rest.
        // Genuinely local or experimental skills still belong on the Gateway, not here.
        new SkillDefinition(
            Id: "devthrottle-method",
            Name: "The DevThrottle Method",
            Summary: "How we build software here - the seats, the twenty laws, and the mission document every piece of " +
                     "work starts from.",
            Triggers: new[]
            {
                "devthrottle method", "the method", "how do we build", "what is my seat",
                "delivery lead", "tech lead", "release manager", "mission document",
                "who reviews this", "who shuts me down", "what proof do I owe", "am I allowed to",
            }),

        new SkillDefinition(
            Id: "fleet-naming",
            Name: "Name a session, and put it in a Mission",
            Summary: "Every session belongs to a Mission and is named to one convention: <Mission> - <Role> - <what " +
                     "this seat does>. A session with no Mission is invisible on the fleet map.",
            Triggers: new[]
            {
                "name a session", "rename a session", "what should I call this session",
                "session naming", "create a mission", "attach a session to a mission",
                "the fleet map is a mess", "sessions with no mission", "spawn a worker",
            }),

        new SkillDefinition(
            Id: "foreground-only",
            Name: "Foreground Only",
            Summary: "Agents and sessions run in the FOREGROUND - never background or detached. Sessions exist to be " +
                     "logged, tracked and improved; a hidden process is none of those.",
            Triggers: new[]
            {
                "run_in_background", "run in the background", "background agent", "background session",
                "detached", "spawn an agent", "long running job", "codex review",
                "start it and check back",
            }),

        new SkillDefinition(
            Id: "checks-that-fail-open",
            Name: "Checks That Fail Open",
            Summary: "A check whose pass condition is an ABSENCE certifies a run that never happened. Restate it as a " +
                     "specific PRESENCE - and an empty result is a broken instrument, never a clean run.",
            Triggers: new[]
            {
                "verify", "prove it", "sweep", "grep returned nothing", "zero hits", "clean run",
                "it passed", "no defects", "reviewed", "delivered", "double check", "how do we know",
            }),

        new SkillDefinition(
            Id: "proof-covers-the-wrong-thing",
            Name: "Proof That Covers The Wrong Thing",
            Summary: "A proof can be valid and still say nothing about what you changed - wrong surface, wrong reader, " +
                     "wrong caller, or an untested premise. Name what your evidence does NOT cover.",
            Triggers: new[]
            {
                "prove it", "by construction", "root cause", "reproduced it", "repro", "guard-local",
                "skipped test", "suite is green", "premise", "assumption", "self review",
                "it is settled",
            }),

        new SkillDefinition(
            Id: "destructive-sweeps-lean-to-keep",
            Name: "Destructive Sweeps Lean To Keep",
            Summary: "A destructive operation acts only on what it can positively prove is disposable. Enumerate what " +
                     "to DELETE, never what to skip - and refresh the safety signal on every real use.",
            Triggers: new[]
            {
                "sweep", "purge", "cleanup", "delete old", "prune", "reclaim space", "stale files",
                "retention", "aged out", "rm -rf", "git clean", "drop the table",
            }),

        new SkillDefinition(
            Id: "dev-reports",
            Name: "Dev reports - how you report to the owner",
            Summary: "A report FOR THE OWNER is one HTML file published with cc-dev-reports - never a file path, never " +
                     "markdown, never an artifact. Holds the required shape, including the only three allowed status " +
                     "words.",
            Triggers: new[]
            {
                "write a report", "write me a report", "dev report", "report format",
                "mission document", "status report", "write up what you found",
                "summarise what you did", "hand me a document", "cc-dev-reports", "findings",
                "how do I report",
            }),
    };

    /// <summary>Every skill the Gateway ships, in the order the register lists them.</summary>
    public static IReadOnlyList<SkillDefinition> All() => Definitions;

    /// <summary>
    /// The shipped body (the instructions an agent fetches) for a built-in skill, read from the
    /// embedded <c>Skills/Content/&lt;id&gt;.skill.md</c> resource. Fail-loud on a missing resource - a
    /// built-in skill without its body is a build defect, not a runtime condition, and it would put a
    /// line in every session's briefing that leads nowhere.
    /// </summary>
    public static string BodyFor(string id)
    {
        var resourceName = $"CcDirector.Gateway.Skills.Content.{id}.skill.md";
        var assembly = typeof(BuiltInSkills).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded skill body '{resourceName}' is missing from the Gateway binary. " +
                "Every built-in skill must ship its body.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
