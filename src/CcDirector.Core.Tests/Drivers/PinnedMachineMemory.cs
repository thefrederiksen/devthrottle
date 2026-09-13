using CcDirector.Core.Drivers;
using CcDirector.Core.Machine;

namespace CcDirector.Core.Tests.Drivers;

/// <summary>
/// PINS THE MACHINE A SUBMIT TEST BELIEVES IT IS RUNNING ON (issue #2818).
///
/// WHY EVERY TERMINAL-SUBMIT TEST NEEDS THIS. The submit path now reads the machine's memory and
/// behaves differently when it is short: a starved machine keeps the owner's text and watches, a
/// healthy one clears and retypes exactly as it always did. That makes any test which does NOT pin a
/// reading depend on how much memory the build agent happened to have free - and it bites in practice.
/// The machine this was written on had 2.53 gigabytes available of 15.7, which sits directly on the
/// Tight threshold, so a test suite that allocated a few hundred megabytes while running flipped the
/// behaviour halfway through its own run and three long-standing tests failed intermittently.
///
/// A test that exercises the submit path must therefore say which machine it means. Wrap it in a
/// <c>using</c> and the reading is restored afterwards:
///
///     using var _ = PinnedMachineMemory.Healthy();
///
/// This is also the cheapest way to keep the suite honest about what it is really asserting: a test
/// that does not say which machine it assumes is not testing anything definite.
/// </summary>
internal sealed class PinnedMachineMemory : IDisposable
{
    private readonly IMachineMemoryProbe _was;

    private PinnedMachineMemory(IMachineMemoryProbe probe)
    {
        _was = TerminalSubmit.MemoryProbe;
        TerminalSubmit.MemoryProbe = probe;
    }

    /// <summary>A machine with room to work: the submit path behaves exactly as it did before #2818.</summary>
    public static PinnedMachineMemory Healthy() => new(Fixed.Healthy);

    /// <summary>A machine measurably short of memory: deadlines stretch and the composer is never cleared on a guess.</summary>
    public static PinnedMachineMemory Starved() => new(Fixed.Starved);

    /// <summary>A machine whose memory cannot be read at all - which must behave like a healthy one, never like a starved one.</summary>
    public static PinnedMachineMemory Unreadable() => new(Fixed.Unreadable);

    public void Dispose() => TerminalSubmit.MemoryProbe = _was;

    private sealed class Fixed : IMachineMemoryProbe
    {
        private const ulong Gigabyte = 1024UL * 1024 * 1024;

        private readonly MachineMemoryReading _reading;
        private Fixed(MachineMemoryReading reading) => _reading = reading;

        public static readonly Fixed Healthy =
            new(MachineMemoryReading.Read(16 * Gigabyte, 12 * Gigabyte, DateTime.UtcNow));

        public static readonly Fixed Starved =
            new(MachineMemoryReading.Read(16 * Gigabyte, 300UL * 1024 * 1024, DateTime.UtcNow));

        public static readonly Fixed Unreadable =
            new(MachineMemoryReading.Unreadable("pinned by a test", DateTime.UtcNow));

        // The timestamp is refreshed on every read so the probe's own cache window is never in play.
        public MachineMemoryReading Read() => _reading with { TakenAtUtc = DateTime.UtcNow };
    }
}
