using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The microphones <see cref="MacMicAudioCapture"/> can offer on macOS: the system
/// default input, named through CoreAudio so a capture failure can say which device
/// produced no sound instead of a generic "the microphone".
///
/// SCOPE
/// -----
/// One device. <see cref="MacMicAudioCapture"/> records from whatever macOS currently
/// considers the default input - what the user picks in System Settings > Sound > Input -
/// so that is the one entry the picker offers. Choosing a different device inside the
/// Director would mean setting the AudioQueue CurrentDevice property from a device UID;
/// offering entries that could not be honoured would be worse than offering one that can.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacMicDevices
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;
    private const uint DefaultInputDeviceSelector = 0x64496E20; // 'dIn '
    private const uint ObjectNameSelector = 0x6C6E616D;         // 'lnam'
    private const uint ScopeGlobal = 0x676C6F62;                // 'glob'
    private const uint ElementMain = 0;
    private const uint Utf8 = 0x08000100;

    /// <summary>Shown when macOS reports no default input device at all - a real state, not an error to mask.</summary>
    public const string NoInputLabel = "No microphone (macOS reports no default input device)";

    /// <summary>True when macOS has a default input device to record from.</summary>
    public static bool HasDefaultInput() => DefaultInputId() != 0;

    /// <summary>The single selectable device: the system default input.</summary>
    public static IReadOnlyList<MicDevice> Enumerate()
    {
        var device = new MicDevice(MicDevices.DefaultDeviceNumber, DescribeDefaultInput());
        FileLog.Write($"[MacMicDevices] Enumerate: system default input only ({device.Name})");
        return new List<MicDevice> { device };
    }

    /// <summary>Every device number names the system default: it is the only one we can open.</summary>
    public static string DescribeDevice(int number) => DescribeDefaultInput();

    /// <summary>A saved device name always resolves to the system default on macOS.</summary>
    public static int ResolveByName(string? name) => MicDevices.DefaultDeviceNumber;

    /// <summary>The default device and its current name, for callers resolving off the interface thread.</summary>
    public static MicDevice ResolveWithDescription(string? name)
        => new(MicDevices.DefaultDeviceNumber, DescribeDefaultInput());

    /// <summary>
    /// Display name of the current default input, prefixed the way the Windows picker
    /// prefixes its default entry (e.g. "Default - Yealink UH36").
    /// </summary>
    public static string DescribeDefaultInput()
    {
        var name = DefaultInputName();
        return name is null ? NoInputLabel : $"Default - {name}";
    }

    /// <summary>
    /// Friendly name of the default input device, or null when there is none. A machine
    /// with no input device legitimately has none - that is reported, not masked behind
    /// an invented name.
    /// </summary>
    public static string? DefaultInputName()
    {
        uint deviceId = DefaultInputId();
        if (deviceId == 0)
        {
            FileLog.Write("[MacMicDevices] DefaultInputName: no default input device");
            return null;
        }

        var address = new AudioObjectPropertyAddress(ObjectNameSelector, ScopeGlobal, ElementMain);
        uint size = (uint)IntPtr.Size;
        IntPtr cfName = IntPtr.Zero;
        int status = AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, ref cfName);
        if (status != 0 || cfName == IntPtr.Zero)
        {
            FileLog.Write($"[MacMicDevices] device {deviceId} has no readable name: OSStatus {status}");
            return null;
        }

        var name = CFStringToManaged(cfName);
        CFRelease(cfName);
        FileLog.Write($"[MacMicDevices] default input: {name} (id {deviceId})");
        return name;
    }

    private static uint DefaultInputId()
    {
        var address = new AudioObjectPropertyAddress(DefaultInputDeviceSelector, ScopeGlobal, ElementMain);
        uint deviceId = 0;
        uint size = sizeof(uint);
        int status = AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, ref deviceId);
        return status == 0 ? deviceId : 0;
    }

    private static string? CFStringToManaged(IntPtr cfString)
    {
        long length = CFStringGetLength(cfString);
        // Worst case for UTF-8 is 3 bytes per UTF-16 unit, plus the terminator.
        long capacity = (length * 3) + 1;
        var buffer = new byte[capacity];
        if (!CFStringGetCString(cfString, buffer, capacity, Utf8))
            return null;
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;

        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector;
            Scope = scope;
            Element = element;
        }
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref AudioObjectPropertyAddress address,
                                                         uint qualifierDataSize, IntPtr qualifierData,
                                                         ref uint dataSize, ref uint data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref AudioObjectPropertyAddress address,
                                                         uint qualifierDataSize, IntPtr qualifierData,
                                                         ref uint dataSize, ref IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}

/// <summary>
/// The system audio player macOS playback runs on. The Director carries no native macOS audio output:
/// capture is in-process on AudioQueue because it has to be lossless and continuous, but a one-off
/// courtesy cue does not, so it goes to <c>afplay</c> - part of every macOS install, with nothing to
/// detect and nothing for the user to install. This mirrors <c>LinuxAudioTools</c>; the difference is
/// that <c>afplay</c> reads a FILE rather than raw bytes on standard input, so the cue is wrapped in a
/// WAV header and written to a temporary file for the length of the playback.
/// </summary>
internal static class MacAudioTools
{
    private const string Player = "/usr/bin/afplay";

    /// <summary>
    /// Play 16-bit mono PCM through afplay and wait for it to finish. Returns null when the whole cue
    /// sounded, or the reason it did not - the caller reports an end ONLY on null, because that is the
    /// evidence the dictation recorder blanks the cue out of the recording on (issue #2925, ruling L1).
    ///
    /// MEASURED, because afplay's timing looks alarming and is not: it takes about 1.15 s to run a
    /// 0.20 s cue, but recording the cue through the microphone and locating it with
    /// <c>ReadyCue.Find</c> puts the sound 118 ms after the call, with a match score of 0.92 against a
    /// threshold of 0.5. The extra second is afplay lingering AFTER the sound, not latency before it.
    /// So the user hears the bloop promptly and the blanking finds it; the only cost is that the end is
    /// reported about a second late, which stays well inside
    /// <see cref="DesktopAudioCue.PlaybackEndAllowance"/> and inside the window the recorder searches
    /// for the cue in. A Send pressed during that second waits for the end, as it does on any platform.
    /// </summary>
    public static string? PlayPcm16Mono(byte[] pcm, int sampleRate, TimeSpan allowance)
    {
        if (!File.Exists(Player)) return $"no playback tool installed ({Player})";

        var wav = WavWriter.WrapPcm16(pcm, sampleRate, channels: 1, bitsPerSample: 16);
        var path = Path.Combine(Path.GetTempPath(), $"cc-director-cue-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, wav);

            var psi = new ProcessStartInfo(Player)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null) return $"{Player} could not be started";
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)allowance.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return $"no end reported within {allowance.TotalSeconds:F0} s";
            }
            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"{Player} exited with code {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}";
        }
        finally
        {
            // The player has exited by now either way, so the file has served its purpose. A temporary
            // file left behind per dictation turn would accumulate for as long as the Director runs.
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { FileLog.Write($"[MacAudioTools] could not delete the cue file {path}: {ex.Message}"); }
        }
    }
}
