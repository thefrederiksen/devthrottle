using System.Globalization;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE QUIET LINE (voice mode auto-off, step 5, owner ruling 28 September 2026). The owner finds out that voice mode
/// switched itself off from one line where voice mode is shown - no notification. The Gateway writes the finished
/// sentence and the phone and the Cockpit render it verbatim: the client is dumb (CLAUDE.md rule 7), so it never
/// formats a time or picks a word here.
///
/// The time is the account's own clock time, from the account's time zone, as the owner would read it off his watch.
/// A switch-off on an earlier day names the day, so "at 14:32" can never be read as this afternoon when it was not.
/// </summary>
public static class VoiceAutoOffLine
{
    /// <summary>
    /// The line for a recorded switch-off, or null when there is nothing to say: no switch-off is recorded, or voice
    /// mode is on again (switching it on clears the record, so this is belt and braces for a read between the two).
    /// </summary>
    public static string? For(VoiceListeningLedger.SwitchOff? switchOff, bool voiceModeOn, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (switchOff is not { } off || voiceModeOn) return null;

        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(off.AtUtc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var clock = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = at.Date == today
            ? $"at {clock}"
            : at.Date == today.AddDays(-1)
                ? $"yesterday at {clock}"
                : $"on {at.ToString("dddd d MMMM", CultureInfo.InvariantCulture)} at {clock}";
        return $"Voice mode switched off {when} - {off.Reason}";
    }
}
