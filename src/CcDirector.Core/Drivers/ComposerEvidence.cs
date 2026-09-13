namespace CcDirector.Core.Drivers;

/// <summary>
/// What is known about whether the composer is holding the text we typed (issue #2818).
///
/// WHY THIS IS THREE VALUES AND NOT A BOOLEAN. <c>ScreenShowsText</c> answered this question with a
/// <c>bool</c>, and returned <c>false</c> in two situations that are not the same fact: when the
/// rendered screen was available and did NOT contain the text, and when there was no screen to look
/// at. The submit path then treated both as licence to press Escape over the composer. On a machine
/// that was merely slow to repaint, the second case is what deleted the owner's typed sentence.
///
/// A destructive step needs <see cref="Present"/> or <see cref="Absent"/>. <see cref="Unknown"/> is
/// never enough, and it is the default whenever we cannot see.
/// </summary>
public enum ComposerEvidence
{
    /// <summary>We looked and could not tell. Nothing destructive may be done on this.</summary>
    Unknown = 0,

    /// <summary>The text is on the screen. It arrived; press Enter.</summary>
    Present = 1,

    /// <summary>We could see the screen, and the text is not on it. Clearing and retyping is safe.</summary>
    Absent = 2,
}
