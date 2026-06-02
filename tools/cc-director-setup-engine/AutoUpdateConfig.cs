using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Auto-update settings, read from the shared config.json "autoUpdate" section. Controls whether the
/// resident apps (Director, Gateway) silently pull newer releases and how often they check.
///
/// Defaults: enabled, every 6 hours. A missing section, a missing file, or a parse error all fall back
/// to the defaults (no config required to get auto-update). The env var CC_AUTOUPDATE=0 is a global
/// kill switch that overrides the config (handy for a machine you never want auto-updating).
/// </summary>
public sealed record AutoUpdateConfig(bool Enabled, double IntervalHours)
{
    public static readonly AutoUpdateConfig Default = new(Enabled: true, IntervalHours: 6);

    /// <summary>The check interval, clamped to a sane floor so a bad config can't cause a hammering loop.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours < 0.25 ? 6 : IntervalHours);

    public static AutoUpdateConfig Load(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (string.Equals(Environment.GetEnvironmentVariable("CC_AUTOUPDATE"), "0", StringComparison.Ordinal))
            return Default with { Enabled = false };

        try
        {
            if (!File.Exists(layout.ConfigPath)) return Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(layout.ConfigPath));
            if (!doc.RootElement.TryGetProperty("autoUpdate", out var au) || au.ValueKind != JsonValueKind.Object)
                return Default;

            var enabled = au.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? e.GetBoolean()
                : Default.Enabled;
            var interval = au.TryGetProperty("intervalHours", out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetDouble()
                : Default.IntervalHours;
            return new AutoUpdateConfig(enabled, interval);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[AutoUpdateConfig] load failed ({layout.ConfigPath}): {ex.Message}; using defaults");
            return Default;
        }
    }
}
