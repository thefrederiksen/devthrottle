namespace CcDirector.Core.Machine;

/// <summary>
/// Reasons for an unreadable figure that more than one probe gives, held once so every platform words
/// them the same way and a test or a screen can recognise them.
/// </summary>
public static class ResourceUnreadableReasons
{
    /// <summary>
    /// A rate - processor busy, a disk's milliseconds per write - is the difference between two cumulative
    /// readings. The first read after start-up has only one, so its rates cannot be known yet. Saying so
    /// is honest; reporting zero would show an idle machine that may be on fire.
    ///
    /// A PROCESS'S SHARE OF THE MACHINE IS DELIBERATELY NOT IN THAT LIST, although it is just as much a
    /// rate. A process has no unreadable form of its own: on the first sample it is listed and named with
    /// <see cref="ProcessReading.CpuPercent"/> null, because its name, its parent and its memory are
    /// readable and an unreadable list would throw them away. This reason therefore never applies to one
    /// process - only to <see cref="ProcessSetReading"/> as a whole, and that is reserved for an
    /// enumeration the platform refused.
    /// </summary>
    public const string FirstSample = "first sample, no interval yet";
}
