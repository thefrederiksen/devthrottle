namespace CcDirector.Core.Sessions;

/// <summary>
/// One member of a session group (issue #225): a typed session the group creates, with a
/// name suffix and a descriptive role. The member's behavior comes from its
/// <see cref="SessionType"/> (and that type's playbook); the role is a human label.
/// </summary>
public sealed class SessionGroupMember
{
    public SessionGroupMember() { }

    public SessionGroupMember(SessionType type, string nameSuffix, string role)
    {
        Type = type;
        NameSuffix = nameSuffix;
        Role = role;
    }

    /// <summary>The session type to create (drives the seeded playbook).</summary>
    public SessionType Type { get; set; } = SessionType.Developer;

    /// <summary>Appended to the repo name for this member's session name, e.g. " - qa".</summary>
    public string NameSuffix { get; set; } = "";

    /// <summary>Descriptive role stamped on the session, e.g. "QA". Behavior is the Type's.</summary>
    public string Role { get; set; } = "";
}

/// <summary>
/// A named group of sessions that are created together and travel together (issue #225).
/// Group definitions are DATA, not hardcoded UI: <see cref="BuiltIn"/> ships the presets and
/// the New Session flow lists whatever definitions exist, so adding a second group later
/// needs no flow redesign. Members are created in list order and keep that order in the UI.
/// </summary>
public sealed class SessionGroupDefinition
{
    public int Version { get; set; } = 1;

    /// <summary>Display name shown in the picker, e.g. "Product".</summary>
    public string Name { get; set; } = "";

    /// <summary>One-line description for the picker.</summary>
    public string? Description { get; set; }

    /// <summary>The members, in creation + display order.</summary>
    public List<SessionGroupMember> Members { get; set; } = new();

    /// <summary>
    /// The built-in group presets. The Product group (issue #225; four members in #254;
    /// shrunk to TWO in #259) is Product -> Implementation, in that fixed order. Developer
    /// and QA merged into the single Implementation session (build&lt;-&gt;verify loop) and
    /// Support left the group - all three stay selectable solo, just not grouped.
    /// </summary>
    public static IReadOnlyList<SessionGroupDefinition> BuiltIn { get; } = new[]
    {
        new SessionGroupDefinition
        {
            Name = "Product",
            Description = "Product + Implementation - two tied sessions in one repo.",
            Members = new List<SessionGroupMember>
            {
                new(SessionType.Product,        " - product",        "Product"),
                new(SessionType.Implementation, " - implementation", "Implementation"),
            },
        },
    };

    /// <summary>Look up a built-in group by name (case-insensitive). Null if unknown.</summary>
    public static SessionGroupDefinition? FindBuiltIn(string name)
        => BuiltIn.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
}
