using System.Reflection;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Reads the product version stamped into an assembly at build time. The single
/// version source is Directory.Build.props at the repo root; the SDK derives
/// AssemblyInformationalVersion from it and appends the git commit SHA
/// (SourceLink), e.g. "0.6.3+1cc1abd9c2...". This class parses that string so
/// every UI surface shows the version the same way instead of hardcoding it.
/// See docs/architecture/VERSIONING.md.
/// </summary>
public static class AppVersion
{
    /// <summary>Semantic version without the commit suffix, e.g. "0.6.3" or "0.6.3-rc1".</summary>
    public static string Semver { get; }

    /// <summary>Short (7-char) git commit SHA the binary was built from, or "" when absent.</summary>
    public static string ShortSha { get; }

    /// <summary>Full informational version as stamped, e.g. "0.6.3+1cc1abd9c2...".</summary>
    public static string Full { get; }

    /// <summary>Display form for UI surfaces: "v0.6.3 (1cc1abd)" or "v0.6.3" without a SHA.</summary>
    public static string Display { get; }

    static AppVersion()
    {
        // Entry assembly = the app exe (Director, Gateway host, Cockpit, ...).
        // Null only in odd hosts (unit tests use this assembly's own stamp then).
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        Full = Parse(assembly, out var semver, out var shortSha);
        Semver = semver;
        ShortSha = shortSha;
        Display = shortSha.Length > 0 ? $"v{semver} ({shortSha})" : $"v{semver}";
    }

    /// <summary>
    /// Parse an assembly's informational version into semver + short SHA.
    /// Exposed for surfaces that need a non-entry assembly's stamp.
    /// </summary>
    public static string Parse(Assembly assembly, out string semver, out string shortSha)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            // No informational stamp (should not happen with the SDK): fall back to the assembly version.
            var v = assembly.GetName().Version ?? new Version(0, 0, 0);
            semver = $"{v.Major}.{v.Minor}.{v.Build}";
            shortSha = "";
            return semver;
        }
        return Parse(info, out semver, out shortSha);
    }

    /// <summary>Parse an informational version string ("0.6.3+sha") into semver + short SHA.</summary>
    public static string Parse(string info, out string semver, out string shortSha)
    {
        var plus = info.IndexOf('+');
        if (plus < 0)
        {
            semver = info;
            shortSha = "";
        }
        else
        {
            semver = info[..plus];
            var sha = info[(plus + 1)..];
            shortSha = sha.Length > 7 ? sha[..7] : sha;
        }
        return info;
    }
}
