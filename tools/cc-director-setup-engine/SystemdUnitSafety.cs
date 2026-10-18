namespace CcDirector.Setup.Engine;

/// <summary>
/// Rules every <c>systemd --user</c> unit we write must keep, in one place (issue devthrottle_internal#2475).
///
/// WHY THIS EXISTS. A branch build of the launcher (fix/3091, never merged) wrote
/// ~/.config/systemd/user/devthrottle-launcher.service from <c>Environment.ProcessPath</c>. Run as
/// <c>dotnet cc-launcher.dll</c>, that path is the runtime host, so the unit read
/// <c>ExecStart="/home/soren/.dotnet/dotnet"</c> with nothing after it. <c>dotnet</c> alone exits 129,
/// <c>Restart=on-failure</c> started it again five seconds later, and on devlinux it had restarted more than
/// 29,000 times. Two rules would have stopped that: never write a command that is only a runtime host, and
/// give up after a few fast failures instead of restarting for ever.
/// </summary>
public static class SystemdUnitSafety
{
    /// <summary>Programs that only host another program. Started with no program after them they print
    /// usage and exit, so a unit whose whole command is one of these can never run anything.</summary>
    private static readonly string[] RuntimeHosts = ["dotnet"];

    /// <summary>
    /// The start limit every unit we write carries, in the <c>[Unit]</c> section: after five starts in ten
    /// minutes systemd stops trying and marks the unit failed, so a unit that dies at once is visible and
    /// quiet rather than spinning. The same numbers the Director restart unit uses.
    /// </summary>
    public const string StartLimitLines = "StartLimitIntervalSec=600\nStartLimitBurst=5";

    /// <summary>
    /// True when <paramref name="execStart"/> (the text after <c>ExecStart=</c>) is a runtime host with no
    /// program after it - the command that can only ever fail. Quoted and unquoted forms are both read.
    /// </summary>
    public static bool IsBareRuntimeHost(string? execStart)
    {
        if (string.IsNullOrWhiteSpace(execStart)) return false;
        var tokens = Tokens(execStart);
        if (tokens.Count != 1) return false;
        var name = Path.GetFileNameWithoutExtension(tokens[0].TrimEnd('/'));
        return RuntimeHosts.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuse to build a unit whose command is only a runtime host. Called by every unit writer before it
    /// writes, so the defect is an exception at install time instead of a crash loop for ever after.
    /// </summary>
    public static void RequireRunnableCommand(string exePath, string? arguments)
    {
        var execStart = string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
        if (IsBareRuntimeHost(execStart))
            throw new InvalidOperationException(
                $"refusing to write a systemd unit that runs only the runtime host '{exePath}' with no program after it; "
                + "it would exit at once and be restarted for ever. Pass the program the host should run.");
    }

    /// <summary>Split an ExecStart command into words the way systemd does for the cases we write: a
    /// double-quoted word stays one word, otherwise words are separated by white space.</summary>
    internal static List<string> Tokens(string command)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            if (i >= command.Length) break;
            if (command[i] == '"')
            {
                var end = command.IndexOf('"', i + 1);
                if (end < 0) end = command.Length;
                tokens.Add(command[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
                tokens.Add(command[start..i]);
            }
        }
        return tokens;
    }
}
