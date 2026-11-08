using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// What the license warning triangle in the Director toolbar shows (issue 4728), taken from the Gateway's answer to
/// <c>GET /gateway/licenses/alarms</c> and nothing else. It replaced the full-width banner across the top of the window:
/// the owner, 7 November 2026, found the strip too big and asked for a small warning triangle beside the resource meter.
///
/// Every word is the Gateway's, verbatim: the count beside the triangle is its <c>Count</c>, and the tooltip is its
/// summary, then one line for each open alarm (each alarm's own <c>Text</c>, every one, in the Gateway's order), then its
/// link words. The colour is the
/// Gateway's tone, <c>warning</c> (yellow) or <c>critical</c> (red). The window copies these onto its controls and decides
/// nothing. Pure, so it is tested without a window.
///
/// A tone this build does not know is not guessed at: the triangle still shows, uncoloured, and its tooltip says this
/// Director cannot draw it - a newer Gateway's alarm must not vanish, and must not be drawn as something it is not.
/// </summary>
public sealed record LicenseAlarmTriangleView(bool IsVisible, string Count, string ToolTip, string? Tone)
{
    /// <summary>The tones the window has a look for (docs/VisualStyle.md).</summary>
    public static readonly IReadOnlyList<string> AllTones = new[] { "warning", "critical" };

    /// <summary>Nothing to show: no answer read yet, or no alarm open.</summary>
    public static readonly LicenseAlarmTriangleView Hidden = new(false, "", "", null);

    /// <summary>The triangle for the Gateway's last answer, or <see cref="Hidden"/> when there is none or no alarm is open.</summary>
    public static LicenseAlarmTriangleView From(LicenseAlarmsResponse? answer)
    {
        if (answer is null || answer.Count == 0) return Hidden;
        var summary = answer.Summary
                      ?? throw new InvalidOperationException("The Gateway reported open license alarms with no summary.");
        var lines = new List<string> { summary };
        lines.AddRange(answer.Alarms.Select(a => a.Text));
        if (!string.IsNullOrEmpty(answer.LinkText))
            lines.Add(answer.LinkText);
        var count = answer.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (answer.Tone is { } tone && AllTones.Contains(tone))
            return new LicenseAlarmTriangleView(true, count, string.Join(Environment.NewLine + Environment.NewLine, lines), tone);
        lines.Insert(1, $"(This Director cannot draw the tone \"{answer.Tone ?? "none"}\"; update it.)");
        return new LicenseAlarmTriangleView(true, count, string.Join(Environment.NewLine + Environment.NewLine, lines), null);
    }
}
