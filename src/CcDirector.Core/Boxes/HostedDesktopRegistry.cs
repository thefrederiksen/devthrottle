using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Boxes;

/// <summary>
/// THE DESKTOPS THIS DIRECTOR HOSTS, and the only way anything gets a port for one (the Factory Boxes mission,
/// phase 1, issue #4258).
///
/// The list comes from one file in the Director's own configuration directory
/// (<see cref="Storage.CcStorage.HostedDesktopsFile"/>):
/// <code>
/// { "desktops": [ { "boxId": "demo-desktop-soren", "displayName": "Soren's demo desktop", "port": 3001 } ] }
/// </code>
/// The target is always 127.0.0.1: the file holds a port and never a host, and a field the format does not name
/// (a "host", a "url", an "address") is refused rather than ignored.
///
/// AN INVALID FILE IS REFUSED WHOLE, LOUDLY. A bad box id, a port outside 1-65535, a missing display name, a box id
/// or a port listed twice, or a field the format does not know: the file is refused with the reason in the log, and
/// this Director then hosts NO desktop until the file is fixed. An entry is never skipped silently, and the
/// Director never keeps serving an older list the person has already changed - both would leave the person
/// believing a desktop is listed, or not listed, when it is the other way round.
///
/// THE PORT NEVER LEAVES THIS CLASS EXCEPT THROUGH <see cref="Resolve"/>. <see cref="Desktops"/> hands out the box id
/// and the display name only, and that is all the Gateway is sent. The relay asks <see cref="Resolve"/> with a box id
/// and who is asking; an unknown box id, or anything that names a host or a port instead of a box id, is refused
/// here, on the Director, and the refusal is logged with the box id and the requester.
/// </summary>
public sealed class HostedDesktopRegistry
{
    /// <summary>The address every hosted desktop is reached on. Not configurable, by design.</summary>
    public const string LoopbackAddress = "127.0.0.1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Func<string?> _readFile;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    // Replaced whole on every load; never mutated, so a reader under the lock sees one consistent list.
    private Loaded _current = Loaded.Empty;

    /// <param name="readFile">
    /// Returns the file's text, or null when the file does not exist. Production passes <see cref="ReadFileAt"/>;
    /// a test passes the text it wants loaded.
    /// </param>
    /// <param name="log">Where log lines go. Null writes to <see cref="FileLog"/>; a test passes its own list.</param>
    public HostedDesktopRegistry(Func<string?> readFile, Action<string>? log = null)
    {
        _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
        _log = log ?? FileLog.Write;
    }

    /// <summary>A reader for the file at <paramref name="path"/>: its text, or null when there is no file.</summary>
    public static Func<string?> ReadFileAt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return () => File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The desktops this Director hosts: box id and display name only, ordered by box id.</summary>
    public IReadOnlyList<HostedDesktopDto> Desktops
    {
        get
        {
            lock (_gate)
                return _current.Desktops.Select(d => new HostedDesktopDto { BoxId = d.BoxId, DisplayName = d.DisplayName }).ToList();
        }
    }

    /// <summary>Why the last load refused the file, or null when it loaded (or there was no file).</summary>
    public string? LoadError
    {
        get { lock (_gate) return _current.Error; }
    }

    /// <summary>
    /// Read the file again and replace the list with what it says now. Returns true when the list a person would see
    /// changed (a desktop added, removed or renamed, or the file became valid or invalid). A refused file replaces the
    /// list with an empty one and logs why; it does not throw, because a mistake in this one file must not take the
    /// Director's tunnel down with it.
    /// </summary>
    public bool Reload()
    {
        string? text;
        Loaded next;
        try
        {
            text = _readFile();
            next = text is null ? Loaded.Empty : Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            next = Loaded.Refused($"the file could not be read: {ex.Message}");
        }

        lock (_gate)
        {
            var changed = !Loaded.SameForAPerson(_current, next);
            _current = next;
            if (changed)
            {
                if (next.Error is not null)
                    _log($"[HostedDesktopRegistry] Reload: the hosted-desktop list was REFUSED, so this Director hosts no desktop until it is fixed: {next.Error}");
                else
                    _log($"[HostedDesktopRegistry] Reload: this Director hosts {next.Desktops.Count} desktop(s): "
                                  + (next.Desktops.Count == 0 ? "(none)" : string.Join(", ", next.Desktops.Select(d => $"{d.BoxId} on port {d.Port}"))));
            }
            return changed;
        }
    }

    /// <summary>
    /// THE ONE LOOKUP THE RELAY CALLS: the local port for <paramref name="boxId"/>, or a refusal. A refusal is
    /// logged here with the box id and <paramref name="requester"/>, so a caller never needs to remember to.
    /// </summary>
    /// <param name="boxId">The box id the Gateway asked for. Never a host or a port: anything that is not a valid box id is refused.</param>
    /// <param name="requester">Who is asking, for the log: for example the Gateway's stream id and the account it was opened for.</param>
    public HostedDesktopResolution Resolve(string? boxId, string requester)
    {
        var who = string.IsNullOrWhiteSpace(requester) ? "(unnamed requester)" : BoxIdRule.ForLog(requester);

        var invalid = BoxIdRule.WhyInvalid(boxId);
        if (invalid is not null)
            return Refuse(boxId, who, invalid);

        HostedDesktop? match;
        string? loadError;
        lock (_gate)
        {
            match = _current.Desktops.FirstOrDefault(d => string.Equals(d.BoxId, boxId, StringComparison.Ordinal));
            loadError = _current.Error;
        }

        if (match is null)
            return Refuse(boxId, who, loadError is null
                ? "this Director hosts no desktop with that box id"
                : $"this Director hosts no desktop, because its hosted-desktop list was refused: {loadError}");

        _log($"[HostedDesktopRegistry] Resolve: box={match.BoxId} -> {LoopbackAddress}:{match.Port}, requester={who}");
        return HostedDesktopResolution.Allow(match.BoxId, match.Port);
    }

    private HostedDesktopResolution Refuse(string? boxId, string who, string reason)
    {
        _log($"[HostedDesktopRegistry] Resolve REFUSED: box={BoxIdRule.ForLog(boxId)}, requester={who}: {reason}");
        return HostedDesktopResolution.Refuse(reason);
    }

    /// <summary>Parse and validate the whole file. Any invalid entry refuses the whole file, with the reason.</summary>
    private static Loaded Parse(string text)
    {
        HostedDesktopFile? file;
        try
        {
            file = JsonSerializer.Deserialize<HostedDesktopFile>(text, Json);
        }
        catch (JsonException ex)
        {
            return Loaded.Refused($"the file is not in the hosted-desktop format ({ex.Message}); "
                                  + "each entry is exactly boxId, displayName and port, and a host can never be given");
        }

        if (file?.Desktops is null)
            return Loaded.Refused("the file has no \"desktops\" list");

        var desktops = new List<HostedDesktop>();
        var boxIds = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        for (var i = 0; i < file.Desktops.Count; i++)
        {
            var entry = file.Desktops[i];
            var where = $"entry {i + 1}";
            if (entry is null)
                return Loaded.Refused($"{where} is empty");

            var invalid = BoxIdRule.WhyInvalid(entry.BoxId);
            if (invalid is not null)
                return Loaded.Refused($"{where} (box id \"{BoxIdRule.ForLog(entry.BoxId)}\"): {invalid}");
            where = $"{where} ({entry.BoxId})";

            if (entry.Port is not { } port)
                return Loaded.Refused($"{where} has no port");
            if (port is < 1 or > 65535)
                return Loaded.Refused($"{where} has port {port}, and a port is 1 to 65535");

            var name = entry.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name))
                return Loaded.Refused($"{where} has no display name");
            if (name.Length > BoxIdRule.MaxDisplayNameLength)
                return Loaded.Refused($"{where} has a display name longer than {BoxIdRule.MaxDisplayNameLength} characters");

            if (!boxIds.Add(entry.BoxId!))
                return Loaded.Refused($"{where}: the box id is listed twice");
            if (!ports.Add(port))
                return Loaded.Refused($"{where}: port {port} is already listed for another box");

            desktops.Add(new HostedDesktop(entry.BoxId!, name, port));
        }

        return new Loaded(desktops.OrderBy(d => d.BoxId, StringComparer.Ordinal).ToList(), null);
    }

    private sealed record HostedDesktop(string BoxId, string DisplayName, int Port);

    private sealed record Loaded(IReadOnlyList<HostedDesktop> Desktops, string? Error)
    {
        public static readonly Loaded Empty = new(Array.Empty<HostedDesktop>(), null);

        public static Loaded Refused(string why) => new(Array.Empty<HostedDesktop>(), why);

        public static bool SameForAPerson(Loaded a, Loaded b)
            => a.Error == b.Error && a.Desktops.SequenceEqual(b.Desktops);
    }

    /// <summary>The file's shape. A field it does not name is refused (<see cref="JsonUnmappedMemberHandling.Disallow"/>).</summary>
    private sealed class HostedDesktopFile
    {
        [JsonPropertyName("desktops")]
        public List<HostedDesktopFileEntry?>? Desktops { get; set; }
    }

    private sealed class HostedDesktopFileEntry
    {
        [JsonPropertyName("boxId")]
        public string? BoxId { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }
    }
}

/// <summary>The answer of <see cref="HostedDesktopRegistry.Resolve"/>: a port on 127.0.0.1, or a refusal with its reason.</summary>
public sealed class HostedDesktopResolution
{
    private HostedDesktopResolution(bool allowed, string? boxId, int port, string? refusal)
    {
        Allowed = allowed;
        BoxId = boxId;
        Port = port;
        Refusal = refusal;
    }

    /// <summary>True when the box id is one this Director hosts.</summary>
    public bool Allowed { get; }

    /// <summary>The box id that resolved, or null on a refusal.</summary>
    public string? BoxId { get; }

    /// <summary>The local port on <see cref="HostedDesktopRegistry.LoopbackAddress"/>, or 0 on a refusal.</summary>
    public int Port { get; }

    /// <summary>Why the lookup was refused, or null when it was allowed.</summary>
    public string? Refusal { get; }

    internal static HostedDesktopResolution Allow(string boxId, int port) => new(true, boxId, port, null);

    internal static HostedDesktopResolution Refuse(string reason) => new(false, null, 0, reason);
}
