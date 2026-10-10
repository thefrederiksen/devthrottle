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
