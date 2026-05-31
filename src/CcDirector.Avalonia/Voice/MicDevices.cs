using CcDirector.Core.Utilities;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>One selectable microphone: the NAudio WaveIn device number plus its display name.</summary>
public readonly record struct MicDevice(int Number, string Name);

/// <summary>
/// Enumerates and resolves the microphones available for dictation.
///
/// WHY THIS EXISTS
/// ---------------
/// <see cref="MicAudioCapture"/> previously created a <c>WaveInEvent</c> with no
/// device set, which defaults to WaveIn device 0 - the FIRST device in the
/// legacy winmm enumeration, NOT the Windows default capture device. On a
/// machine with several mics (or where device 0 is a dead/virtual endpoint) that
/// silently opened the wrong device and captured nothing. This helper lets the
/// app open the real Windows default (<see cref="DefaultDeviceNumber"/> =
/// WAVE_MAPPER) or a user-picked device, and name whichever one is active so the
/// Dictate dialog can show it.
/// </summary>
public static class MicDevices
{
    /// <summary>
    /// WAVE_MAPPER. Passed as the WaveIn device number, this opens the device
    /// Windows currently considers the default capture device, instead of a
    /// fixed index that may not be the right (or a working) microphone.
    /// </summary>
    public const int DefaultDeviceNumber = -1;

    private const string DefaultFallbackLabel = "Windows default input";

    /// <summary>
    /// All selectable devices: the Windows-default entry first (named after the
    /// device it currently maps to), then every concrete WaveIn capture device.
    /// </summary>
    public static IReadOnlyList<MicDevice> Enumerate()
    {
        var devices = new List<MicDevice> { new(DefaultDeviceNumber, DescribeDefault()) };
        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++)
            devices.Add(new MicDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
        FileLog.Write($"[MicDevices] Enumerate: {count} concrete capture device(s) + default");
        return devices;
    }

    /// <summary>Display name for a device number (<see cref="DefaultDeviceNumber"/> resolves to the current default).</summary>
    public static string DescribeDevice(int number)
        => number == DefaultDeviceNumber
            ? DescribeDefault()
            : WaveInEvent.GetCapabilities(number).ProductName;

    /// <summary>
    /// Resolve a persisted device NAME back to its current WaveIn index. Names
    /// are persisted rather than indices because winmm indices reorder when
    /// devices are plugged/unplugged. A null/blank name, or a name no longer
    /// present, selects the Windows default - the right behaviour when a saved
    /// mic has been removed.
    /// </summary>
    public static int ResolveByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DefaultDeviceNumber;

        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(WaveInEvent.GetCapabilities(i).ProductName, name, StringComparison.Ordinal))
                return i;
        }

        FileLog.Write($"[MicDevices] ResolveByName: '{name}' is not a current capture device; using Windows default");
        return DefaultDeviceNumber;
    }

    private static string DescribeDefault()
    {
        var name = DefaultCaptureName();
        return name is null ? DefaultFallbackLabel : $"Default - {name}";
    }

    /// <summary>
    /// Friendly name of the Windows default capture endpoint, for DISPLAY. A
    /// machine with no default capture endpoint legitimately has none - that is
    /// handled explicitly (returns null, so callers show the generic label), not
    /// caught and masked. Any other failure surfaces at the calling boundary
    /// rather than being swallowed.
    /// </summary>
    private static string? DefaultCaptureName()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return device.FriendlyName;
    }
}
