using System.Security.Cryptography;
using System.Text;

namespace CcDirector.RuleHook;

/// <summary>
/// cc-rule-hook: Claude Code runs it as a PreToolUse hook before every shell command of a Director
/// session. It reads the event on standard input, decides against the session's rules, records what it
/// would have done, and exits 0 with nothing on standard output - so the command always runs (phase 1 is
/// dry run only). See <see cref="HookRunner"/>.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        using var standardInput = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        return new HookRunner(Environment.GetEnvironmentVariable, () => DateTime.UtcNow, RandomHex).Run(standardInput);
    }

    private static string RandomHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}
