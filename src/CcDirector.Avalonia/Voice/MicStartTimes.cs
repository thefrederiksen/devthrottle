using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// How long each microphone takes to deliver its first audio, measured on this machine (issue #2928).
///
/// Some microphones are slow to wake: a webcam microphone was measured losing about half a second at every
/// start, and the user had no way to tell which of their microphones it was. Every dictation start records
/// how long the device took from being asked to start recording to its first audio, and the Speak dialog's
/// selector shows the typical figure beside each device so a faster one can be picked.
///
/// The figure is the MEDIAN of the device's most recent <see cref="HistoryLength"/> starts - one slow start
/// does not brand a device, and a device that got faster stops being called slow within twenty uses. It is
/// kept in config.json under <c>dictation.mic_start_ms</c>, beside the persisted microphone choice, keyed by
/// the same device name the selector shows. A device with no recorded start shows no figure at all: a
/// guessed number would be worse than none.
/// </summary>
public static class MicStartTimes
{
    /// <summary>How many recent starts per device the typical figure is taken over.</summary>
    public const int HistoryLength = 20;

    private static readonly object RecordLock = new();

    /// <summary>The history with <paramref name="ms"/> appended, keeping only the most recent <see cref="HistoryLength"/>.</summary>
    public static IReadOnlyList<int> Append(IReadOnlyList<int> history, int ms)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms), ms, "a start time cannot be negative");
        return history.Append(ms).TakeLast(HistoryLength).ToList();
    }

    /// <summary>The median of the history in milliseconds, or null when the device has never been used.</summary>
    public static int? TypicalMs(IReadOnlyList<int> history)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (history.Count == 0) return null;
        var sorted = history.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Every device's recorded start times from a config document. Malformed entries are skipped and logged.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> Read(JsonObject config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        if (config["dictation"]?["mic_start_ms"] is not JsonObject perDevice) return result;

        foreach (var (name, node) in perDevice)
        {
            if (node is not JsonArray array)
            {
                FileLog.Write($"[MicStartTimes] Read: entry for '{name}' is not a list; ignored");
                continue;
            }
            var values = new List<int>();
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<int>(out var ms) && ms >= 0) values.Add(ms);
            }
            result[name] = values;
        }
        return result;
    }

    /// <summary>The devices with each one's typical start time filled in from <paramref name="history"/>.</summary>
    public static IReadOnlyList<MicDevice> Decorate(IReadOnlyList<MicDevice> devices, IReadOnlyDictionary<string, IReadOnlyList<int>> history)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (history is null) throw new ArgumentNullException(nameof(history));
        return devices
            .Select(d => d with { TypicalStartMs = history.TryGetValue(d.Name, out var h) ? TypicalMs(h) : null })
            .ToList();
    }

    /// <summary>
    /// Record one measured start for a device in config.json. Reads the current history, appends, and
    /// writes the device's list back through the merge-patch writer, which leaves every other key alone.
    /// Returns the device's typical figure including this start, so an open selector can show it at once.
    /// </summary>
    public static int Record(string deviceName, int ms)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("device name is required", nameof(deviceName));
        lock (RecordLock)
        {
            var existing = Read(CcDirectorConfigService.ReadRaw());
            var history = Append(existing.TryGetValue(deviceName, out var h) ? h : Array.Empty<int>(), ms);
            var list = new JsonArray();
            foreach (var value in history) list.Add(value);
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["dictation"] = new JsonObject { ["mic_start_ms"] = new JsonObject { [deviceName] = list } },
            });
            var typical = TypicalMs(history)!.Value;   // never null: the history holds this start
            FileLog.Write($"[MicStartTimes] Record: device=\"{deviceName}\", startMs={ms}, typicalMs={typical}, starts={history.Count}");
            return typical;
        }
    }
}
