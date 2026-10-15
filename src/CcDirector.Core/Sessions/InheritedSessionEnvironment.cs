namespace CcDirector.Core.Sessions;

/// <summary>
/// The one rule for which of the Director's OWN environment variables never reach a session it starts.
///
/// Every backend starts an agent with the Director's whole environment plus the variables the
/// <see cref="SessionManager"/> stamps for that session. A Director is often itself started from inside a
/// session - an agent running the Director to test it, or the fleet opening one - so its environment
/// carries that PARENT session's variables. Each of those is a lie to the new session: it names a session,
/// a factory or an agent host the new session is not in.
///
/// The leak this was written for: a session in NO factory inherited <c>CC_FACTORY_MEMORY_DIR</c> from the
/// factory session that had started the Director, and its agent read a real factory's notes as its own
/// (FactoryMemoryAtLaunchTests, 8 October 2026). The <see cref="SessionManager"/> correctly did not SET the
/// variable; nothing REMOVED the inherited one. The same shape had already been fixed one variable at a
/// time for the agent hosts' own markers (CLAUDECODE, CLAUDE_CODE_*, CODEX_*), each in two places. This
/// class holds the rule once so the Windows and the Unix host cannot drift, and so adding a name is one
/// line, not two.
///
/// Stripping is the right fix and a stamp is not: a session in a factory gets the variable stamped AFTER the
/// strip, so it is never affected, while a session in no factory has nothing to stamp - the variable must
/// simply not be there, and "set to empty" is not "absent" to a tool that checks for the variable.
/// </summary>
public static class InheritedSessionEnvironment
{
    private static readonly HashSet<string> ParentSessionVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CC_SESSION_ID", "CC_DIRECTOR_ID", "CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY", "CC_FLEET_TOOLS",
    };

    /// <summary>
    /// True when <paramref name="name"/> is a variable of the Director's own environment that must NOT be
    /// inherited by a session. The session's own stamped variables are applied after this rule, so a name
    /// listed here is still delivered whenever the Director sets it for that session on purpose.
    /// </summary>
    public static bool IsStripped(string name)
    {
        // Agent-host markers: an agent that sees its own marker believes it is nested inside itself and
        // either refuses to start (Claude Code) or silently skips writing its transcript (the child
        // session marker). CODEX_HOME is the one Codex variable that is configuration, not a marker.
        if (name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase))
            return true;
        // A parent agent's editor hook would make every git commit in the session wait on a tool that is
        // not there.
        if (name.Equals("GIT_EDITOR", StringComparison.OrdinalIgnoreCase))
            return true;

        // The parent session's factory. A session in a factory is stamped its own folder after this rule;
        // a session in no factory must see no folder at all.
        if (name.Equals(FactoryMemoryFiles.DirectoryEnvVar, StringComparison.OrdinalIgnoreCase))
            return true;
        // The parent seat's kit, on the same terms: a seat is stamped its own kit folder after this rule, and a
        // session that is not a seat (or a child) must not read another seat's brief, rules or files as its own.
        if (name.Equals(FactoryRunKit.DirectoryEnvVar, StringComparison.OrdinalIgnoreCase))
            return true;

        // The parent session's identity and its door to the fleet. The SessionManager stamps each of these for the new
        // session whenever the new session should have it (CC_SESSION_ID always; the Director's id, the Gateway
        // address and this session's own key, and the fleet text when this Director has them). When it does not, an
        // inherited one names the PARENT session and carries the parent's key: the new session would act on the fleet
        // as the session that started its Director.
        if (ParentSessionVariables.Contains(name))
            return true;

        // The license tool's allowance to use the machine's real Claude login. The Director sets it only on its
        // own runs of the tool; a Director started from a shell that has it must not hand it to every session,
        // where a typed `cc-devthrottle license switch` would then act on the real login.
        if (name.Equals(Licenses.LicenseCommandLineOptions.AllowRealDefaultVariable, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// The current process environment with every stripped variable removed - the base every backend
    /// builds a session's environment on, before the session's own variables are applied. Keys compare
    /// with <paramref name="comparer"/> because Windows variable names are case-insensitive and Unix
    /// names are not; each host passes its own.
    /// </summary>
    public static Dictionary<string, string> Inherited(StringComparer comparer)
    {
        var inherited = new Dictionary<string, string>(comparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value)
                continue;
            if (IsStripped(key))
                continue;
            inherited[key] = value;
        }
        return inherited;
    }
}
