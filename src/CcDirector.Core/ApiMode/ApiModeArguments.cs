using System.Text;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// Turns the launch line the Director composes for an agent into the argument list an API-mode process is started with
/// (one element each, never re-joined). The rule is the inverse of how <see cref="Sessions.SessionManager"/> composes
/// Pi's line: arguments are separated by spaces or tabs, and double quotes group one argument that contains spaces (the
/// preamble file's path is written <c>--append-system-prompt "&lt;path&gt;"</c>). Nothing is escaped in that line, so a
/// backslash is an ordinary character: a Windows path keeps every one of its separators.
/// </summary>
public static class ApiModeArguments
{
    /// <summary>Split <paramref name="line"/> into arguments. Throws when a double quote is never closed.</summary>
    public static IReadOnlyList<string> Split(string? line)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
            return arguments;

        var current = new StringBuilder();
        var inArgument = false;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                inArgument = true;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (inArgument)
                    arguments.Add(current.ToString());
                current.Clear();
                inArgument = false;
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }

        if (quoted)
            throw new ArgumentException($"The launch line has a double quote that is never closed: {line}", nameof(line));
        if (inArgument)
            arguments.Add(current.ToString());
        return arguments;
    }
}
