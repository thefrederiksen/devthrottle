using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Onboarding;

/// <summary>
/// The result of validating a user-entered gateway URL before the connectivity test runs:
/// whether the text is a well-formed http(s)://host[:port] URL and, if not, a human-readable
/// reason. This is a cheap syntactic check; the actual reachability test is a separate async
/// probe (<see cref="SettingsDetectionService.TestGatewayAsync"/>).
/// </summary>
/// <param name="IsValid">True when the text parses as an absolute http or https URL with a host.</param>
/// <param name="NormalizedUrl">The trimmed URL to persist when valid; empty when invalid.</param>
/// <param name="Message">A human-readable reason when invalid; empty when valid.</param>
public sealed record GatewayUrlValidation(bool IsValid, string NormalizedUrl, string Message);

/// <summary>
/// The result of checking whether at least one usable Claude Code agent exists for onboarding:
/// whether Claude Code resolves on PATH (or a configured path), the resolved executable path
/// when found, and a human-readable status line.
/// </summary>
/// <param name="IsAvailable">True when Claude Code resolved to a runnable executable.</param>
/// <param name="ResolvedPath">The resolved executable path when available; empty otherwise.</param>
/// <param name="Message">A human-readable status line for the agent step.</param>
public sealed record AgentAvailability(bool IsAvailable, string ResolvedPath, string Message);

/// <summary>
/// UI-free engine for the first-run onboarding wizard (issue #370, lean v1). Decides whether the
/// wizard should appear, validates the gateway URL, checks Claude Code availability by reusing the
/// PATH-resolution work from #448 (<see cref="ToolDetectionService"/> / <see cref="ExecutableResolver"/>),
/// persists the chosen gateway URL, and records the onboarding-complete marker. The Avalonia wizard
/// dialog is a thin shell over this; the logic lives here so it is testable without a UI thread.
///
/// The onboarding-complete state is a simple persisted marker (<c>onboarding.completed = true</c> in
/// config.json) PLUS the presence of <c>gateway.url</c>: the wizard appears only when neither is set,
/// and once the user completes (or explicitly dismisses) it the marker stops it reappearing.
/// </summary>
public sealed class OnboardingModel
{
    /// <summary>The official Claude Code install page shown when no agent is available.</summary>
    public const string ClaudeInstallUrl = "https://docs.claude.com/en/docs/claude-code/setup";

    private readonly ToolDetectionService _detector;

    public OnboardingModel(ToolDetectionService detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <summary>
    /// True when the first-run onboarding wizard should be shown automatically: there is no
    /// <c>gateway.url</c> configured AND the <c>onboarding.completed</c> marker is not set. Once
    /// either is true the wizard never auto-opens again. (It can still be re-run on demand from
    /// Settings.)
    /// </summary>
    public static bool ShouldShowOnboarding()
    {
        FileLog.Write("[OnboardingModel] ShouldShowOnboarding");
        var root = CcDirectorConfigService.ReadRaw();

        var hasGatewayUrl = root["gateway"] is JsonObject gateway
            && gateway["url"] is JsonNode urlNode
            && !string.IsNullOrWhiteSpace(urlNode.GetValue<string>());

        var completed = IsCompletedMarkerSet(root);

        var show = !hasGatewayUrl && !completed;
        FileLog.Write($"[OnboardingModel] ShouldShowOnboarding: hasGatewayUrl={hasGatewayUrl}, completed={completed}, result={show}");
        return show;
    }

    /// <summary>True when the <c>onboarding.completed</c> marker is set to true in config.json.</summary>
    public static bool IsOnboardingComplete()
    {
        var root = CcDirectorConfigService.ReadRaw();
        return IsCompletedMarkerSet(root);
    }

    private static bool IsCompletedMarkerSet(JsonObject root)
    {
        if (root["onboarding"] is not JsonObject onboarding)
            return false;
        if (onboarding["completed"] is not JsonNode completed)
            return false;
        return completed.GetValue<bool>();
    }

    /// <summary>
    /// Syntactically validate a user-entered gateway URL. Accepts only an absolute
    /// <c>http://</c> or <c>https://</c> URL with a host. A blank URL, a bare host with no scheme,
    /// or a non-http scheme is rejected with an actionable message - this is the gate the wizard
    /// applies before persisting, so an invalid URL is never silently accepted.
    /// </summary>
    public static GatewayUrlValidation ValidateGatewayUrl(string? url)
    {
        var trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0)
            return new GatewayUrlValidation(false, "", "Enter a gateway URL, for example http://gateway-host:7878.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return new GatewayUrlValidation(false, "", "That is not a valid URL. Use the form http://host:port, for example http://gateway-host:7878.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new GatewayUrlValidation(false, "", "The URL must start with http:// or https://.");

        if (string.IsNullOrWhiteSpace(uri.Host))
            return new GatewayUrlValidation(false, "", "The URL is missing a host name.");

        return new GatewayUrlValidation(true, trimmed, "");
    }

    /// <summary>
    /// Check whether Claude Code is available to launch by resolving it through the shared
    /// detection service (which uses <see cref="ExecutableResolver"/> / PATH from #448). Returns an
    /// available/not-available verdict with the resolved path and a human-readable message - never
    /// throws, so the agent step can always render a clear state.
    /// </summary>
    public AgentAvailability CheckClaudeAvailable(AgentOptions options)
    {
        FileLog.Write("[OnboardingModel] CheckClaudeAvailable");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var detect = _detector.DetectTool(AgentKind.ClaudeCode, options);
        if (detect.Found && detect.ResolvedPath is not null)
        {
            FileLog.Write($"[OnboardingModel] CheckClaudeAvailable: available at {detect.ResolvedPath}");
            return new AgentAvailability(true, detect.ResolvedPath, $"Claude Code is ready ({detect.ResolvedPath}).");
        }

        FileLog.Write("[OnboardingModel] CheckClaudeAvailable: not available on PATH");
        return new AgentAvailability(false, "",
            "Claude Code was not found on your PATH. Install it, then click Re-check.");
    }

    /// <summary>
    /// Persist a validated gateway URL to the <c>gateway.url</c> key in config.json via the merge
    /// patcher, leaving every other config section untouched. Validates the URL first and THROWS on
    /// an invalid one (no silent acceptance) - callers validate at the UI boundary before calling.
    /// </summary>
    public static void PersistGatewayUrl(string url)
    {
        FileLog.Write($"[OnboardingModel] PersistGatewayUrl: url={url}");
        var validation = ValidateGatewayUrl(url);
        if (!validation.IsValid)
            throw new ArgumentException($"Refusing to persist an invalid gateway URL: {validation.Message}", nameof(url));

        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = validation.NormalizedUrl },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[OnboardingModel] PersistGatewayUrl: persisted");
    }

    /// <summary>
    /// Record that onboarding is complete by setting <c>onboarding.completed = true</c> in
    /// config.json. After this, <see cref="ShouldShowOnboarding"/> returns false, so the wizard
    /// never auto-opens again (whether the user finished it or explicitly dismissed it).
    /// </summary>
    public static void MarkComplete()
    {
        FileLog.Write("[OnboardingModel] MarkComplete");
        var patch = new JsonObject
        {
            ["onboarding"] = new JsonObject { ["completed"] = true },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write("[OnboardingModel] MarkComplete: marker set");
    }
}
