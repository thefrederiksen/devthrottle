namespace CcDirector.Core.Machine;

/// <summary>
/// Reads the whole machine's memory. An interface so the hot paths in issue #2818 - the composer
/// submit, the roster push - can be tested against a reading the test chooses, with no clock and no
/// platform call. <see cref="MachineMemoryProbe"/> is the real one.
/// </summary>
public interface IMachineMemoryProbe
{
    /// <summary>
    /// The machine's memory now, or a cached reading a moment old. Never throws: a platform that
    /// cannot be read comes back as <see cref="MachineMemoryReading.Unreadable"/> with the reason.
    /// </summary>
    MachineMemoryReading Read();
}
