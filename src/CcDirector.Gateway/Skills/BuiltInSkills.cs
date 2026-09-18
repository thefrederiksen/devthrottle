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
