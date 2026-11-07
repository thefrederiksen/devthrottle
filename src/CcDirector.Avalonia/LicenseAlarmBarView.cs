using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// What the license banner across the top of the Director window shows (mission "Licenses - Always Ready, Loud When
/// Not", step 2), taken from the Gateway's answer to <c>GET /gateway/licenses/alarms</c> and nothing else. The summary
/// and the link words are the Gateway's, verbatim; the tone is the Gateway's verdict, <c>warning</c> (yellow) or
/// <c>critical</c> (red). The window copies these onto its controls. Pure, so it is tested without a window.
///
/// A tone this build does not know is not guessed at: the banner still shows, uncoloured, and says this Director cannot
/// draw it - a newer Gateway's alarm must not vanish, and must not be drawn as something it is not.
/// </summary>
public sealed record LicenseAlarmBarView(bool IsVisible, string Summary, string LinkText, string? Tone)
{
    /// <summary>The tones the window has a look for (docs/VisualStyle.md).</summary>
    public static readonly IReadOnlyList<string> AllTones = new[] { "warning", "critical" };

    /// <summary>Nothing to show: no answer read yet, or no alarm open.</summary>
    public static readonly LicenseAlarmBarView Hidden = new(false, "", "", null);

    /// <summary>The banner for the Gateway's last answer, or <see cref="Hidden"/> when there is none or no alarm is open.</summary>
    public static LicenseAlarmBarView From(LicenseAlarmsResponse? answer)
    {
        if (answer is null || answer.Count == 0) return Hidden;
        var summary = answer.Summary
                      ?? throw new InvalidOperationException("The Gateway reported open license alarms with no summary.");
        var link = answer.LinkText ?? "";
        if (answer.Tone is { } tone && AllTones.Contains(tone))
            return new LicenseAlarmBarView(true, summary, link, tone);
        return new LicenseAlarmBarView(true,
            $"{summary} (This Director cannot draw the tone \"{answer.Tone ?? "none"}\"; update it.)", link, null);
    }
}
