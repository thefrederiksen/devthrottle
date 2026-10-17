using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>Which verb the Terminal tab's wheel button calls. The Gateway names it; the desktop only maps it to the
/// route.</summary>
public enum WheelVerb
{
    /// <summary>Take the wheel: the Director stops the protocol process and opens the agent's real terminal.</summary>
    Take,

    /// <summary>Hand back: the Director closes the terminal and resumes the conversation in API mode.</summary>
    HandBack,
}

/// <summary>
/// THE TERMINAL TAB'S WHEEL BUTTON, AS THE GATEWAY RULED IT (mission #4070, phase 4) - the desktop twin of
/// client-core's <c>wheelAction.ts</c>. Which button, its words, whether it can be pressed and why not are decided
/// once on the Gateway (CLAUDE.md rule 7), stamped down onto the session by the <c>set-display-state</c> command, and
/// rendered verbatim. Nothing that uses this reads the session's connection mode or wheel holder: a terminal-mode
/// session shows no button because the Gateway's verdict says there is none.
/// </summary>
/// <param name="Verb">Which verb pressing it calls.</param>
/// <param name="Label">The button's words, verbatim ("Take the wheel", "Hand back").</param>
/// <param name="Enabled">False when the button is shown but cannot be pressed now.</param>
/// <param name="DisabledReason">Why it cannot be pressed, verbatim; null when it is enabled.</param>
public sealed record GatewayWheelAction(WheelVerb Verb, string Label, bool Enabled, string? DisabledReason)
{
    /// <summary>The wire word for "no button".</summary>
    internal const string NoneWord = "none";

    /// <summary>
    /// THE ONE PLACE THE GATEWAY'S WIRE WORDS ARE READ on the desktop. The field names on the wire are not final
    /// (the Gateway side is adding them); whatever carries them hands its four values here, so a rename touches the
    /// caller's property names and nothing else.
    ///
    /// Returns null when the Gateway says there is no button: no verdict at all (a terminal session, or an older
    /// Gateway) or a button of "none". THROWS on a verdict this build cannot read - a verb it does not know, or a
    /// button with no words - because inventing a button is exactly what rule 7 forbids; the caller shows the failure.
    /// </summary>
    public static GatewayWheelAction? FromWire(string? kind, string? label, bool? enabled, string? disabledReason)
    {
        if (kind is null || string.Equals(kind, NoneWord, StringComparison.Ordinal))
            return null;

        var verb = kind switch
        {
            "take" => WheelVerb.Take,
            "handBack" => WheelVerb.HandBack,
            _ => throw new InvalidOperationException(
                $"The Gateway sent a Terminal tab wheel button this Director does not know (\"{kind}\"), so no button "
                + "is shown. Update the Director or the Gateway so they agree."),
        };

        var words = label?.Trim() ?? "";
        if (words.Length == 0)
            throw new InvalidOperationException(
                "The Gateway sent a Terminal tab wheel button with no words on it, so no button is shown.");

        var isEnabled = enabled == true;
        var reason = isEnabled || string.IsNullOrWhiteSpace(disabledReason) ? null : disabledReason.Trim();
        FileLog.Write($"[GatewayWheelAction] FromWire: kind={kind}, label={words}, enabled={isEnabled}, reason={reason ?? "(none)"}");
        return new GatewayWheelAction(verb, words, isEnabled, reason);
    }
}
