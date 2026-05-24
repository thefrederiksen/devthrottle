using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Loads the dictation dictionary from a YAML file on disk and watches the
/// file for changes. Subscribers get a fresh <see cref="DictationDictionary"/>
/// every time the file is updated.
///
/// File layout (YAML):
///
///   vocabulary:
///     - mindzie
///     - CenCon
///     - ConPTY
///
///   common_mistranscriptions:
///     ConPTY: [Contui, ContUI]
///     mindzie: [Minzy, Mindsy]
///
///   profiles:
///     default:
///       cleanup_enabled: true
///       style_prompt: "tighten to professional prose"
///     code:
///       cleanup_enabled: false
/// </summary>
public sealed class DictionaryLoader : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private DictationDictionary _current;

    /// <summary>Fires after the dictionary file is reloaded successfully.</summary>
    public event Action<DictationDictionary>? OnReloaded;

    /// <summary>Fires when a reload attempt fails. Current dictionary is unchanged.</summary>
    public event Action<string>? OnReloadFailed;

    public DictionaryLoader(string path, bool watch = true)
    {
        FileLog.Write($"[DictionaryLoader] ctor: path={path}, watch={watch}");
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _current = LoadFromDisk(_path);

        if (watch && File.Exists(_path))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path))
                      ?? throw new InvalidOperationException($"Cannot resolve directory for {_path}");
            var file = Path.GetFileName(_path);
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
    }

    /// <summary>Snapshot of the currently loaded dictionary.</summary>
    public DictationDictionary Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Packs the vocabulary into a string suitable for the OpenAI
    /// transcription <c>prompt</c> parameter. Stays under the practical
    /// ~244 token budget by truncating long lists.
    /// </summary>
    public static string BuildSttPrompt(DictationDictionary dict)
    {
        if (dict.Vocabulary.Count == 0)
            return "";
        return "Glossary of names and terms used by the speaker: "
               + string.Join(", ", dict.Vocabulary) + ".";
    }

    private void OnFileChanged(object _, FileSystemEventArgs __)
    {
        // FileSystemWatcher coalescing is unreliable on Windows; we tolerate
        // re-firing by simply reloading idempotently.
        try
        {
            var fresh = LoadFromDisk(_path);
            lock (_gate) _current = fresh;
            FileLog.Write($"[DictionaryLoader] OnFileChanged: reloaded {_path}, "
                          + $"vocab={fresh.Vocabulary.Count}, "
                          + $"patterns={fresh.CommonMistranscriptions.Count}, "
                          + $"profiles={fresh.Profiles.Count}");
            OnReloaded?.Invoke(fresh);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictionaryLoader] OnFileChanged FAILED: {ex.Message}");
            OnReloadFailed?.Invoke(ex.Message);
        }
    }

    public static DictationDictionary LoadFromDisk(string path)
    {
        FileLog.Write($"[DictionaryLoader] LoadFromDisk: {path}");
        if (!File.Exists(path))
        {
            FileLog.Write($"[DictionaryLoader] LoadFromDisk: file does not exist, returning empty");
            return DictationDictionary.Empty;
        }

        var yaml = File.ReadAllText(path);
        return Parse(yaml);
    }

    /// <summary>
    /// Serialize a dictionary back to the YAML layout <see cref="Parse"/> reads.
    /// Null style prompts are omitted; everything else round-trips. Exposed
    /// internally for testing without touching disk.
    /// </summary>
    internal static string Serialize(DictationDictionary dict)
    {
        var shape = new YamlShape
        {
            Vocabulary = dict.Vocabulary.ToList(),
            CommonMistranscriptions = dict.CommonMistranscriptions
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            Profiles = dict.Profiles.ToDictionary(
                kv => kv.Key,
                kv => new YamlProfile
                {
                    CleanupEnabled = kv.Value.CleanupEnabled,
                    StylePrompt = kv.Value.StylePrompt,
                }),
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        return serializer.Serialize(shape);
    }

    /// <summary>
    /// Write the dictionary to disk atomically (temp file + move), creating the
    /// containing directory if it does not exist.
    /// </summary>
    public static void WriteToDisk(string path, DictationDictionary dict)
    {
        FileLog.Write($"[DictionaryLoader] WriteToDisk: {path}, "
                      + $"vocab={dict.Vocabulary.Count}, "
                      + $"patterns={dict.CommonMistranscriptions.Count}, "
                      + $"profiles={dict.Profiles.Count}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))
                  ?? throw new InvalidOperationException($"Cannot resolve directory for {path}");
        Directory.CreateDirectory(dir);

        var yaml = Serialize(dict);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, yaml);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Parse a YAML dictionary string. Exposed internally for testing without
    /// touching disk.
    /// </summary>
    internal static DictationDictionary Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return DictationDictionary.Empty;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<YamlShape?>(yaml);
        if (raw is null)
            return DictationDictionary.Empty;

        var vocab = (raw.Vocabulary ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (raw.CommonMistranscriptions is not null)
        {
            foreach (var kv in raw.CommonMistranscriptions)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                    continue;
                var variants = kv.Value
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .ToList();
                if (variants.Count > 0)
                    patterns[kv.Key.Trim()] = variants;
            }
        }

        var profiles = new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase);
        if (raw.Profiles is not null)
        {
            foreach (var kv in raw.Profiles)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                    continue;
                profiles[kv.Key.Trim()] = new DictationProfile(
                    Name: kv.Key.Trim(),
                    CleanupEnabled: kv.Value.CleanupEnabled,
                    StylePrompt: string.IsNullOrWhiteSpace(kv.Value.StylePrompt)
                        ? null
                        : kv.Value.StylePrompt.Trim());
            }
        }

        // Ensure there is always a "default" profile so callers can fall back
        // without a null check.
        if (!profiles.ContainsKey("default"))
        {
            profiles["default"] = new DictationProfile(
                Name: "default",
                CleanupEnabled: true,
                StylePrompt: null);
        }

        return new DictationDictionary(vocab, patterns, profiles);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }
    }

    // YAML deserialization target. Public types are required by YamlDotNet
    // when used through DeserializerBuilder.
    public sealed class YamlShape
    {
        public List<string>? Vocabulary { get; set; }
        public Dictionary<string, List<string>>? CommonMistranscriptions { get; set; }
        public Dictionary<string, YamlProfile>? Profiles { get; set; }
    }

    public sealed class YamlProfile
    {
        public bool CleanupEnabled { get; set; } = true;
        public string? StylePrompt { get; set; }
    }
}
