using System.Reflection;

namespace CcDirector.Core.Claude;

/// <summary>
/// The Session Rules hook evaluator (cc-rule-hook) as this Director build carries it (Session Rules part 2,
/// phase 1, ruling P16).
///
/// The Director's publish for Windows or Linux compiles cc-rule-hook to native code for the same platform
/// and embeds it in the Director's own executable under <see cref="ResourceName"/> (see the EmbedRuleHook
/// target in CcDirector.Avalonia.csproj). <see cref="ClaudeHookInstaller"/> writes it out beside the hook
/// scripts. Carrying it, rather than shipping it as a second file, is what lets install, both update paths,
/// roll back and every slot stay one-file operations: the hook always matches the build that is running.
/// A build without it - a plain build with no runtime identifier, or a platform not given one yet - carries
/// none, and the installer then registers no rule hook and logs that loudly.
/// </summary>
public static class RuleHookBinary
{
    /// <summary>The embedded resource name. Must match the LogicalName in CcDirector.Avalonia.csproj.</summary>
    public const string ResourceName = "cc-rule-hook";

    /// <summary>True when the running Director carries the evaluator.</summary>
    public static bool IsCarried() => Assembly.GetEntryAssembly()?.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>The evaluator's bytes, or null when the running Director carries none.</summary>
    public static byte[]? Carried()
    {
        using var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
