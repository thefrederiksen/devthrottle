namespace CcDirector.Core.Sessions;

/// <summary>
/// WHO asked for a session to exist (devthrottle_internal issue #982). Recorded at birth and never
/// changed afterwards - it is a fact about the create call, not about what the session went on to do.
///
/// The issue asked for two values, <c>human</c> and <c>agent</c>. There are three, because a scheduled
/// run is neither: nobody typed it and no agent session called for it, and folding it into either one
/// would corrupt the very number this record exists to produce ("what share of sessions do agents
/// start"). <see cref="Unknown"/> is the honest answer for a session created through a path that does
/// not state an origin, or by a Director that predates the field - it is never guessed into a real
/// value, because a guessed origin is worse than a missing one for exactly the claim this backs.
/// </summary>
public static class SessionOriginKinds
{
    /// <summary>A person asked for this session - the desktop New Session dialog, the Cockpit, the
    /// phone, or a human running the CLI outside any session.</summary>
    public const string Human = "human";

    /// <summary>Another agent session asked for this session. <see cref="Session.ParentSessionId"/>
    /// names which one.</summary>
    public const string Agent = "agent";

    /// <summary>Automation asked for this session with no live caller - a cron schedule firing, a work
    /// list running. Nobody was at a keyboard and no session made the call.</summary>
    public const string Schedule = "schedule";

    /// <summary>The create path did not say. Recorded as-is; never presented as a real origin.</summary>
    public const string Unknown = "unknown";

    public static readonly string[] All = { Human, Agent, Schedule, Unknown };

    /// <summary>The canonical lowercase token for a supplied value, or null when the value is blank or
    /// not one we know. A caller stamps <see cref="Unknown"/> for null; validation at an API boundary
    /// rejects it instead, so a mistyped origin is never silently swallowed.</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(All, v) >= 0 ? v : null;
    }

    /// <summary>True when the value names an origin we know.</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;
}

/// <summary>
/// WHERE the create call came from (devthrottle_internal issue #982) - the surface axis that sits beside
/// <see cref="SessionOriginKinds"/>. Recorded at birth and never changed.
///
/// Deliberately NOT the same set as <see cref="InputSurface"/>, which describes where a TURN was driven
/// from. The two answer different questions and share only three of their values; merging them would
/// force one enum to carry <c>cron</c> (never a place a human types) and the other to carry <c>desktop</c>
/// (never a place a spawn is relayed from). They are kept apart on purpose.
/// </summary>
public static class SessionOriginSurfaces
{
    /// <summary>The cc-director desktop app's own New Session flow, on this machine.</summary>
    public const string Desktop = "desktop";

    /// <summary>The Cockpit web app in a browser.</summary>
    public const string Cockpit = "cockpit";

    /// <summary>The mobile /m app on a phone.</summary>
    public const string Phone = "phone";

    /// <summary>The cc-devthrottle command line (<c>session spawn</c>) - whether a human or an agent
    /// session ran it. Which of those it was is the ORIGIN's job, not this one's.</summary>
    public const string Cli = "cli";

    /// <summary>A Gateway cron schedule firing a seed run.</summary>
    public const string Cron = "cron";

    /// <summary>A work-list / workflow runner opening a session for an item.</summary>
    public const string Workflow = "workflow";

    /// <summary>A factory trigger on the Gateway: a check with no model in it found work, so the Gateway
    /// started a session to do it (the Website Business Factory mission, product track).</summary>
    public const string Trigger = "trigger";

    /// <summary>A direct API call that named no more specific surface.</summary>
    public const string Api = "api";

    /// <summary>The create path did not say.</summary>
    public const string Unknown = "unknown";

    public static readonly string[] All = { Desktop, Cockpit, Phone, Cli, Cron, Workflow, Trigger, Api, Unknown };

    /// <summary>The canonical lowercase token for a supplied value, or null when blank or unknown.</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(All, v) >= 0 ? v : null;
    }

    /// <summary>True when the value names a surface we know.</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;

    /// <summary>
    /// Map a device registry <c>DeviceType</c> (resolved from a VERIFIED per-device key) to the surface
    /// that device is. The Gateway uses this to stamp a spawn relayed from a signed-in phone or browser,
    /// overwriting whatever the client claimed - the same gateway-authoritative rule
    /// <c>PromptRequest.Surface</c> already follows for turns. An unrecognized device type is
    /// <see cref="Unknown"/>, never guessed.
    /// </summary>
    public static string FromDeviceType(string? deviceType) =>
        (deviceType ?? "").Trim().ToLowerInvariant() switch
        {
            "phone" => Phone,
            "browser" => Cockpit,
            _ => Unknown,
        };
}

/// <summary>
/// The three birth facts issue #982 asked for, carried together so a create path states them in one
/// place instead of threading three loose parameters: WHO asked (<see cref="Kind"/>), WHERE from
/// (<see cref="Surface"/>), and - when an agent asked - WHICH session made the call
/// (<see cref="ParentSessionId"/>).
///
/// The issue listed <c>originAgentSessionId</c> and <c>parentSessionId</c> as two fields. They are ONE
/// fact: the session that made the create call is the parent, and there is no create path where they
/// could differ. Storing it twice would only create a way for the two copies to disagree, so it is
/// stored once, here, and the lineage tree is built from it.
/// </summary>
/// <param name="Kind">One of <see cref="SessionOriginKinds"/>.</param>
/// <param name="Surface">One of <see cref="SessionOriginSurfaces"/>.</param>
/// <param name="ParentSessionId">The session that asked for this one, or null. Only ever set alongside
/// <see cref="SessionOriginKinds.Agent"/>.</param>
public readonly record struct SessionOrigin(string Kind, string Surface, Guid? ParentSessionId = null)
{
    /// <summary>Nothing was stated. What a create path with no origin information records - honestly.</summary>
    public static SessionOrigin Unknown =>
        new(SessionOriginKinds.Unknown, SessionOriginSurfaces.Unknown);

    /// <summary>A person, at the desktop app's own New Session flow. True by construction: this path
    /// only runs in response to the local UI.</summary>
    public static SessionOrigin DesktopHuman =>
        new(SessionOriginKinds.Human, SessionOriginSurfaces.Desktop);

    /// <summary>A Gateway cron schedule firing a seed run.</summary>
    public static SessionOrigin Cron =>
        new(SessionOriginKinds.Schedule, SessionOriginSurfaces.Cron);

    /// <summary>A work-list / workflow runner opening a session for an item.</summary>
    public static SessionOrigin Workflow =>
        new(SessionOriginKinds.Schedule, SessionOriginSurfaces.Workflow);

    /// <summary>A factory trigger whose check found work. Nobody was at a keyboard and no session asked, so
    /// the kind is <see cref="SessionOriginKinds.Schedule"/>, the same as a cron firing.</summary>
    public static SessionOrigin Trigger =>
        new(SessionOriginKinds.Schedule, SessionOriginSurfaces.Trigger);

    /// <summary>An agent session asked, from the given surface.</summary>
    public static SessionOrigin AgentFrom(Guid parentSessionId, string surface) =>
        new(SessionOriginKinds.Agent, surface, parentSessionId);

    /// <summary>A person asked, from the given surface.</summary>
    public static SessionOrigin HumanFrom(string surface) =>
        new(SessionOriginKinds.Human, surface);

    /// <summary>
    /// Build a coherent origin from loose (possibly caller-supplied) values, dropping anything that does
    /// not hold together. Unknown tokens normalize to the unknown value rather than to a plausible one,
    /// and a parent id is kept ONLY on an agent origin - an origin that says "a human asked" while also
    /// naming a parent session is contradictory, and the contradiction is resolved toward the stated
    /// kind rather than being stored for a later reader to trip over.
    /// </summary>
    public static SessionOrigin Compose(string? kind, string? surface, Guid? parentSessionId)
    {
        var k = SessionOriginKinds.Normalize(kind) ?? SessionOriginKinds.Unknown;
        var s = SessionOriginSurfaces.Normalize(surface) ?? SessionOriginSurfaces.Unknown;
        var parent = k == SessionOriginKinds.Agent ? parentSessionId : null;
        return new SessionOrigin(k, s, parent);
    }
}
