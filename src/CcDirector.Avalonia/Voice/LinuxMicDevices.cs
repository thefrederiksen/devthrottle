using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>Which Linux sound system the microphone is reached through.</summary>
internal enum LinuxAudioBackend
{
    /// <summary>PipeWire, through <c>pw-record</c> / <c>pw-play</c>. Lists every input and can pick one.</summary>
    PipeWire,

    /// <summary>ALSA, through <c>arecord</c> / <c>aplay</c>. The system default input only.</summary>
    Alsa,
}

/// <summary>One PipeWire audio input: the serial it is selected by, the node name it is opened by, and its display name.</summary>
internal readonly record struct LinuxMicSource(int Serial, string NodeName, string Description);

/// <summary>What one look at the machine's audio inputs found.</summary>
internal sealed record LinuxMicSnapshot(LinuxAudioBackend Backend, IReadOnlyList<LinuxMicSource> Sources, string? DefaultNodeName)
{
    /// <summary>Display name of the default input, or null when the sound system does not name one.</summary>
    public string? DefaultDescription
    {
        get
        {
            foreach (var source in Sources)
            {
                if (string.Equals(source.NodeName, DefaultNodeName, StringComparison.Ordinal))
                    return source.Description;
            }
            return null;
        }
    }
}

/// <summary>
/// The command line audio tools Linux capture and playback run on. The Director carries no native Linux audio
/// library: it starts the sound system's own recorder and reads raw PCM from its standard output, which works
/// on every distribution that ships PipeWire or ALSA without a native dependency to build per architecture.
/// </summary>
internal static class LinuxAudioTools
{
    public const string PipeWireRecord = "pw-record";
    public const string PipeWirePlay = "pw-play";
    public const string PipeWireDump = "pw-dump";
    public const string AlsaRecord = "arecord";
    public const string AlsaPlay = "aplay";

    private static readonly Lazy<bool> PipeWireRawFlag = new(DetectPipeWireRawFlag);

    /// <summary>PipeWire's recorder and its device lister are both installed.</summary>
    public static bool PipeWireToolsPresent => FindOnPath(PipeWireRecord) is not null && FindOnPath(PipeWireDump) is not null;

    /// <summary>ALSA's recorder is installed.</summary>
    public static bool AlsaToolsPresent => FindOnPath(AlsaRecord) is not null;

    /// <summary>Whether this machine has any tool the microphone can be read with. Reads the file system only.</summary>
    public static bool CaptureToolsPresent => PipeWireToolsPresent || AlsaToolsPresent;

    /// <summary>
    /// Newer PipeWire tools need <c>--raw</c> to write headerless PCM to a pipe; older ones do that already and
    /// reject the flag. The tool's own help text says which this one is.
    /// </summary>
    public static bool PipeWireNeedsRawFlag => PipeWireRawFlag.Value;

    /// <summary>Full path of an executable on PATH, or null when it is not installed.</summary>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Run a short-lived tool to completion and return its exit code and output. Throws when it cannot start or overruns.</summary>
    public static (int ExitCode, string Output, string Error) Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish within {timeout.TotalSeconds:F0} s.");
        }
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Play 16-bit mono PCM through the default output and return once it has finished sounding. Returns null
    /// when the player reported success, otherwise the reason it did not.
    /// </summary>
    public static string? PlayPcm16Mono(byte[] pcm, int sampleRate, LinuxAudioBackend backend, TimeSpan allowance)
    {
        string tool;
        var arguments = new List<string>();
        if (backend == LinuxAudioBackend.PipeWire && FindOnPath(PipeWirePlay) is not null)
        {
            tool = PipeWirePlay;
            arguments.AddRange(["--rate", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "--channels", "1", "--format", "s16"]);
            if (PipeWireNeedsRawFlag) arguments.Add("--raw");
            arguments.Add("-");
        }
        else if (FindOnPath(AlsaPlay) is not null)
        {
            tool = AlsaPlay;
            arguments.AddRange(["-q", "-t", "raw", "-f", "S16_LE", "-r", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", "1", "-"]);
        }
        else
        {
            return $"no playback tool installed ({PipeWirePlay} or {AlsaPlay})";
        }

        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process is null) return $"{tool} could not be started";
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(pcm, 0, pcm.Length);
        process.StandardInput.Close();
        if (!process.WaitForExit((int)allowance.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return $"no end reported within {allowance.TotalSeconds:F0} s";
        }
        process.WaitForExit();
        return process.ExitCode == 0 ? null : $"{tool} exited with code {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}";
    }

    private static bool DetectPipeWireRawFlag()
    {
        try
        {
            var (_, output, error) = Run(PipeWireRecord, ["--help"], TimeSpan.FromSeconds(5));
            var needsFlag = output.Contains("--raw", StringComparison.Ordinal) || error.Contains("--raw", StringComparison.Ordinal);
            FileLog.Write($"[LinuxAudioTools] {PipeWireRecord} raw flag: {(needsFlag ? "required" : "not offered")}");
            return needsFlag;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinuxAudioTools] reading {PipeWireRecord} help FAILED, assuming no raw flag: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Enumerates and resolves the microphones available for dictation on Linux; <see cref="MicDevices"/> hands
/// every call here when the Director runs on Linux.
///
/// PipeWire is asked first, because it names every input and the default one. A machine whose PipeWire has no
/// audio input (it runs for screen sharing only, with PulseAudio owning sound) or has no PipeWire tools falls
/// back to ALSA's default input, which every sound system on Linux answers.
///
/// A <see cref="MicDevice.Number"/> is the PipeWire object serial. The last look is remembered so the capture,
/// which is built on the interface thread and must not query anything, can turn a serial into the node to open.
/// </summary>
internal static class LinuxMicDevices
{
    private const string DefaultFallbackLabel = "System default input";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private static volatile LinuxMicSnapshot? _last;

    // The look at the inputs that is running now, shared by every caller that asks while it runs.
    private static readonly object LookGate = new();
    private static TaskCompletionSource<LinuxMicSnapshot>? _lookInFlight;

    /// <summary>The Windows-shaped device list: the default entry first, then every PipeWire input.</summary>
    public static IReadOnlyList<MicDevice> Enumerate()
    {
        var snapshot = TakeSnapshot();
        var devices = new List<MicDevice> { new(MicDevices.DefaultDeviceNumber, DescribeDefault(snapshot)) };
        foreach (var source in snapshot.Sources)
            devices.Add(new MicDevice(source.Serial, source.Description));
        FileLog.Write($"[LinuxMicDevices] Enumerate: {snapshot.Sources.Count} concrete capture device(s) + default, backend={snapshot.Backend}");
        return devices;
    }

    public static string DescribeDevice(int number) => DescribeDevice(TakeSnapshot(), number);

    public static MicDevice ResolveWithDescription(string? name)
    {
        var snapshot = TakeSnapshot();
        var number = ResolveByName(snapshot, name);
        return new MicDevice(number, DescribeDevice(snapshot, number));
    }

    public static int ResolveByName(string? name) => ResolveByName(TakeSnapshot(), name);

    /// <summary>
    /// The sound system to record through and the node to open for a device number, from the last look at the
    /// machine. Null node means the system default. Only looks again when nothing has been looked up yet.
    /// </summary>
    public static (LinuxAudioBackend Backend, string? NodeName) CaptureTargetFor(int number)
    {
        var snapshot = _last ?? TakeSnapshot();
        if (number == MicDevices.DefaultDeviceNumber)
            return (snapshot.Backend, null);
        foreach (var source in snapshot.Sources)
        {
            if (source.Serial == number) return (snapshot.Backend, source.NodeName);
        }
        throw new InvalidOperationException("The selected microphone is no longer connected.");
    }

    /// <summary>The sound system the last look found, for playing the ready cue. Null before any look.</summary>
    public static LinuxAudioBackend? LastBackend => _last?.Backend;

    /// <summary>
    /// Look at the machine's audio inputs now, and remember the answer for <see cref="CaptureTargetFor"/>. A call
    /// made while another look is running waits for that look and shares its answer: the Speak dialog resolves
    /// the saved microphone and fills the selector at the same moment, and two <c>pw-dump</c>s at once each saw
    /// the other leave the sound system, which made a routine extra array in both outputs.
    /// </summary>
    internal static LinuxMicSnapshot TakeSnapshot()
    {
        TaskCompletionSource<LinuxMicSnapshot>? running;
        TaskCompletionSource<LinuxMicSnapshot> look;
        lock (LookGate)
        {
            running = _lookInFlight;
            look = running ?? new TaskCompletionSource<LinuxMicSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lookInFlight = look;
        }
        if (running is not null)
            return running.Task.GetAwaiter().GetResult();

        try
        {
            var snapshot = LookNow();
            look.SetResult(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            look.SetException(ex);
            throw;
        }
        finally
        {
            lock (LookGate) _lookInFlight = null;
        }
    }

    private static LinuxMicSnapshot LookNow()
    {
        LinuxMicSnapshot? snapshot = null;
        if (LinuxAudioTools.PipeWireToolsPresent)
        {
            var (exitCode, output, error) = LinuxAudioTools.Run(LinuxAudioTools.PipeWireDump, [], QueryTimeout);
            if (exitCode == 0)
            {
                snapshot = ParsePipeWireDump(output);
                // Asked here, off the interface thread, so starting a capture only has to start the recorder.
                _ = LinuxAudioTools.PipeWireNeedsRawFlag;
            }
            else
                FileLog.Write($"[LinuxMicDevices] {LinuxAudioTools.PipeWireDump} exited with code {exitCode}: {error.Trim()}");
        }

        if (snapshot is null || snapshot.Sources.Count == 0)
        {
            if (!LinuxAudioTools.AlsaToolsPresent)
            {
                throw new InvalidOperationException(snapshot is null
                    ? "No way to reach the microphone was found. Install PipeWire's pw-record or ALSA's arecord."
                    : "No microphone was found. PipeWire reports no audio input on this machine.");
            }
            snapshot = new LinuxMicSnapshot(LinuxAudioBackend.Alsa, [], null);
        }

        _last = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Read the audio inputs and the default input's node name out of <c>pw-dump</c>'s JSON. An input is a node
    /// whose media class is an audio source; a sink's monitor is not one, so what the speakers play is never
    /// offered as a microphone.
    /// </summary>
    internal static LinuxMicSnapshot ParsePipeWireDump(string json)
    {
        var sources = new List<LinuxMicSource>();
        string? defaultNodeName = null;

        foreach (var item in MergePipeWireArrays(json))
        {
            var type = StringProperty(item, "type");
            if (string.Equals(type, "PipeWire:Interface:Node", StringComparison.Ordinal))
            {
                if (!item.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                if (!info.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) continue;
                var mediaClass = StringProperty(props, "media.class");
                if (mediaClass is null) continue;
                if (!mediaClass.StartsWith("Audio/Source", StringComparison.Ordinal) && !string.Equals(mediaClass, "Audio/Duplex", StringComparison.Ordinal))
                    continue;
                var nodeName = StringProperty(props, "node.name");
                if (string.IsNullOrWhiteSpace(nodeName)) continue;
                if (!IntProperty(props, "object.serial", out var serial) && !IntProperty(item, "id", out serial)) continue;
                var description = StringProperty(props, "node.description") ?? StringProperty(props, "node.nick") ?? nodeName;
                sources.Add(new LinuxMicSource(serial, nodeName, description));
            }
            else if (string.Equals(type, "PipeWire:Interface:Metadata", StringComparison.Ordinal))
            {
                if (!item.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(StringProperty(props, "metadata.name"), "default", StringComparison.Ordinal)) continue;
                if (!item.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in metadata.EnumerateArray())
                {
                    if (!string.Equals(StringProperty(entry, "key"), "default.audio.source", StringComparison.Ordinal)) continue;
                    if (entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                        defaultNodeName = StringProperty(value, "name");
                }
            }
        }

        return new LinuxMicSnapshot(LinuxAudioBackend.PipeWire, sources, defaultNodeName);
    }

    /// <summary>
    /// Every object in <c>pw-dump</c>'s output, as it stood when the dump ended. <c>pw-dump</c> prints one array of
    /// every object and then, when the sound system changed while it was dumping, one more array per change. Any
    /// client coming or going is such a change - two dumps at once see each other leave - so the extra array is
    /// routine, and reading the output as one JSON value failed the Speak dialog with "'[' is invalid after a
    /// single JSON value". Every array is read in order:
    /// <list type="bullet">
    /// <item>A removed object is printed as <c>{ "id": n, "info": null }</c>, and is dropped.</item>
    /// <item>A changed node is printed in full, and replaces the earlier one.</item>
    /// <item>A changed metadata object carries only the entries that changed, so it is kept after the earlier one
    /// rather than replacing it; <see cref="ParsePipeWireDump"/> takes the last value it sees for a key.</item>
    /// </list>
    /// </summary>
    private static List<JsonElement> MergePipeWireArrays(string json)
    {
        var order = new List<int>();
        var versionsById = new Dictionary<int, List<JsonElement>>();
        var withoutId = new List<JsonElement>();

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            using var document = JsonDocument.ParseValue(ref reader);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var item = entry.Clone();
                if (!IntProperty(item, "id", out var id))
                {
                    withoutId.Add(item);
                    continue;
                }

                var removed = item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Null
                    && !item.TryGetProperty("type", out _);
                if (removed)
                {
                    versionsById.Remove(id);
                    continue;
                }

                if (!versionsById.TryGetValue(id, out var versions))
                {
                    versionsById[id] = [item];
                    order.Remove(id);
                    order.Add(id);
                }
                else if (IsMetadata(item) && versions.Count > 0 && IsMetadata(versions[0]))
                    versions.Add(item);
                else
                {
                    versions.Clear();
                    versions.Add(item);
                }
            }
        }

        var merged = new List<JsonElement>();
        foreach (var id in order)
        {
            if (versionsById.TryGetValue(id, out var versions)) merged.AddRange(versions);
        }
        merged.AddRange(withoutId);
        return merged;
    }

    private static bool IsMetadata(JsonElement item)
        => string.Equals(StringProperty(item, "type"), "PipeWire:Interface:Metadata", StringComparison.Ordinal);

    private static string DescribeDefault(LinuxMicSnapshot snapshot)
        => snapshot.DefaultDescription is { } name ? $"Default - {name}" : DefaultFallbackLabel;

    private static string DescribeDevice(LinuxMicSnapshot snapshot, int number)
    {
        if (number == MicDevices.DefaultDeviceNumber) return DescribeDefault(snapshot);
        foreach (var source in snapshot.Sources)
        {
            if (source.Serial == number) return source.Description;
        }
        throw new InvalidOperationException("The selected microphone is no longer connected.");
    }

    private static int ResolveByName(LinuxMicSnapshot snapshot, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return MicDevices.DefaultDeviceNumber;
        foreach (var source in snapshot.Sources)
        {
            if (string.Equals(source.Description, name, StringComparison.Ordinal))
                return source.Serial;
        }
        FileLog.Write($"[LinuxMicDevices] ResolveByName: '{name}' is not a current capture device; using the system default");
        return MicDevices.DefaultDeviceNumber;
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IntProperty(JsonElement element, string name, out int result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out result);
    }
}
