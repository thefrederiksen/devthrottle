namespace CcDirector.Setup.Cli;

/// <summary>
/// Tiny argument parser: a leading positional command, then "--key value" pairs
/// and "--flag" switches. No external dependency; deterministic and easy to test.
/// </summary>
public sealed class CliArgs
{
    public string Command { get; }
    public IReadOnlyList<string> Positionals { get; }
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private static readonly HashSet<string> KnownFlags =
        new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "help" };

    private CliArgs(string command, List<string> positionals, Dictionary<string, string> options, HashSet<string> flags)
    {
        Command = command;
        Positionals = positionals;
        _options = options;
        _flags = flags;
    }

    public static CliArgs Parse(string[] argv)
    {
        var command = argv.Length > 0 && !argv[0].StartsWith("--", StringComparison.Ordinal) ? argv[0] : "help";
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int start = command == "help" && (argv.Length == 0 || argv[0] != "help") ? 0 : 1;
        for (int i = start; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                if (KnownFlags.Contains(key) || i + 1 >= argv.Length || argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    flags.Add(key);
                }
                else
                {
                    options[key] = argv[++i];
                }
            }
            else
            {
                positionals.Add(a);
            }
        }

        return new CliArgs(command, positionals, options, flags);
    }

    public bool HasFlag(string name) => _flags.Contains(name);
    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public string Option(string name, string fallback) => _options.TryGetValue(name, out var v) ? v : fallback;
}
