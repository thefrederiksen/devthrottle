namespace CcDirector.Core.Machine;

/// <summary>
/// Reasons for an unreadable figure that more than one probe gives, held once so every platform words
/// them the same way and a test or a screen can recognise them.
/// </summary>
public static class ResourceUnreadableReasons
{
    /// <summary>
    /// A rate - processor busy, a disk's milliseconds per write, a process's share of the machine - is the
    /// difference between two cumulative readings. The first read after start-up has only one, so its
    /// rates cannot be known yet. Saying so is honest; reporting zero would show an idle machine that may
    /// be on fire.
    /// </summary>
    public const string FirstSample = "first sample, no interval yet";
}
