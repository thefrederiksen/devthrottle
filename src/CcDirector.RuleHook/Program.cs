using System.Globalization;
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

    // Not the cryptographic generator, on purpose: these eight characters only keep two decisions made in
    // the same tick apart, and are no secret. The cryptographic generator loads OpenSSL on Linux, which
    // measured at about ten milliseconds on every command a rule matched. The shared generator is seeded
    // from the operating system's random source, so separate hook processes do not repeat each other.
    private static string RandomHex() => Random.Shared.NextInt64(0, 1L << 32).ToString("x8", CultureInfo.InvariantCulture);
}
