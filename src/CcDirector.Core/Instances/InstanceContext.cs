using CcDirector.Core.Storage;

namespace CcDirector.Core.Instances;

/// <summary>
/// Process-wide identity of the named Director instance this process is running as.
///
/// A "named instance" is a settings profile: its own gateway, port, and data home,
/// selected once at startup via <c>--instance &lt;slug&gt;</c>. EVERY instance - including
/// the default (slug <see cref="DefaultSlug"/>) - runs in its own isolated home under
/// <c>{SharedRoot}\instances\{slug}</c>. There is deliberately NO migration and NO
/// shared-root fallback: the default is just an ordinary instance that happens to be
/// created automatically. Nothing old is ever carried forward.
///
/// This holder is set ONCE at the very top of <c>Program.Main</c> (before FileLog and
/// before any <see cref="Storage.CcStorage"/> use) so that the per-instance data-home
/// redirection (via the <c>CC_DIRECTOR_ROOT</c> env override) and the per-instance
/// identity slot (see <c>DirectorIdStore</c>) are in effect for the whole process.
///
/// Why a static holder: Avalonia constructs <c>App</c> itself, so there is no
/// constructor seam to thread this through - the same rationale as GatewayAppOptions.
/// </summary>
public static class InstanceContext
{
    /// <summary>The reserved slug of the always-present default instance.</summary>
    public const string DefaultSlug = "default";

    /// <summary>
    /// The machine-wide cc-director root, captured BEFORE any per-instance
    /// <c>CC_DIRECTOR_ROOT</c> override is applied. Named-instance data lives under
    /// <c>{SharedRoot}\instances\{slug}</c>; the cross-instance registry lives at the
    /// shared root so every instance and the launcher can enumerate it.
    /// </summary>
    public static string SharedRoot { get; private set; } = CcStorage.Root();

    /// <summary>This process's instance slug. Defaults to <see cref="DefaultSlug"/>.</summary>
    public static string Slug { get; private set; } = DefaultSlug;

    /// <summary>The instance's editable display name (label), when resolved from the registry.</summary>
    public static string? DisplayName { get; private set; }

    /// <summary>True when a <c>--instance</c> flag was explicitly passed on the command line.</summary>
    public static bool WasExplicitlySelected { get; private set; }

    /// <summary>True when this process is the default instance.</summary>
    public static bool IsDefault => string.Equals(Slug, DefaultSlug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// This instance's isolated data home: <c>{SharedRoot}\instances\{slug}</c> for EVERY
    /// instance, default included. This is the value fed into the <c>CC_DIRECTOR_ROOT</c>
    /// override so the whole data tree redirects here. Shared binaries/runtime
    /// (<c>bin\</c>, <c>python\</c>, <c>app\</c>) stay at the shared root by design.
    /// </summary>
    public static string InstanceHome => Path.Combine(SharedRoot, "instances", Slug);

    /// <summary>
    /// Set the instance identity for this process. Call ONCE, first thing in Main,
    /// before FileLog.Start and before any CcStorage use. Recaptures the shared root
    /// from the environment as it stands right now (i.e. before the caller applies the
    /// per-instance override), so the shared root is never polluted by our own redirect.
    /// </summary>
    public static void Initialize(string? slug, bool wasExplicit, string? displayName = null)
    {
        // Capture the shared machine root from CcStorage - the single owner of storage-root
        // resolution (it honors CC_DIRECTOR_ROOT). Called BEFORE the caller applies the
        // per-instance override, so this is the true machine root, not an instance home.
        SharedRoot = CcStorage.Root();
        Slug = Normalize(slug);
        WasExplicitlySelected = wasExplicit;
        DisplayName = displayName;
    }

    /// <summary>Normalize a raw slug: blank -&gt; default, else trimmed lower-case.</summary>
    public static string Normalize(string? slug)
        => string.IsNullOrWhiteSpace(slug) ? DefaultSlug : slug.Trim().ToLowerInvariant();

    /// <summary>
    /// Does <paramref name="root"/> have the shape of a Director's INSTANCE HOME -
    /// <c>&lt;something&gt;/instances/&lt;slug&gt;</c> - rather than a machine's shared root?
    ///
    /// WHY ANYTHING ASKS. A launcher is machine-wide and must serve the SHARED root. On 2026-09-06 one
    /// was started from a shell carrying a Director's <c>CC_DIRECTOR_ROOT</c>, inherited it, and took that
    /// Director's instance home for the machine root. It registered, heartbeated, opened its command
    /// stream and armed both lifecycle signals - all of it filed under the wrong root, where nothing
    /// computing from the real root could see or reach it. From outside it was a perfectly healthy
    /// launcher that could not restart anything.
    ///
    /// The test is STRUCTURAL - a parent directory named <c>instances</c>, which is exactly how
    /// <see cref="InstanceHome"/> composes one - and it is deliberately narrow. A launcher serving a
    /// throwaway root of its own (a test rig) is NOT this fault and answers false, which is right: an
    /// isolated root is a legitimate thing to serve, and refusing it would refuse the only safe way to
    /// exercise any of this. What this catches is the one shape that is wrong by construction.
    /// </summary>
    public static bool LooksLikeAnInstanceHome(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent)) return false;

        return string.Equals(Path.GetFileName(parent), "instances", StringComparison.OrdinalIgnoreCase);
    }
}
