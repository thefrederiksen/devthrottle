using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The editable, versioned store of the wingman's instructions (issue #537). The wingman uses the
/// ACTIVE instructions: the user's active custom version when set, otherwise the DEPLOYED DEFAULT
/// (<see cref="WingmanTranslator.FidelityPrompt"/>, shipped by the DevThrottle dev team and carrying
/// <see cref="WingmanTranslator.DefaultInstructionsVersion"/>).
///
/// Managed-default behavior:
/// - A user with NO customization always tracks the latest deployed default automatically.
/// - When the user HAS customized and a new release ships a CHANGED default, <see cref="UpdateAvailable"/>
///   turns true and the page can show the diff of the dev team's changes (the acknowledged default
///   content vs the new default) and offer a one-click switch - never silently overwriting the user's
///   prompt. Acknowledging (switch, or customizing afresh) snapshots the current default.
///
/// Persisted as one JSON file under the gateway storage root. Thread-safe; best-effort on disk errors.
/// </summary>
public sealed class WingmanInstructionsStore
{
    /// <summary>One saved version of the instructions.</summary>
    public sealed class InstructionVersion
    {
        public string Id { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string? Label { get; set; }
        public string Source { get; set; } = "user";   // "user" | "default"
        public string Hash { get; set; } = "";
    }

    private sealed class StateFile
    {
        public string? ActiveVersionId { get; set; }
        public string AckDefaultVersion { get; set; } = "";
        public string AckDefaultContent { get; set; } = "";
        public List<InstructionVersion> Versions { get; set; } = new();
    }

    /// <summary>Hard cap so a pasted prompt cannot bloat the brain call / file.</summary>
    public const int MaxContentChars = 20_000;

    private readonly string _path;
    private readonly string _defaultContent;
    private readonly string _defaultVersion;
    private readonly object _lock = new();
    private StateFile _state = new();
    private int _seq;

    public WingmanInstructionsStore(string? defaultContent = null, string? defaultVersion = null, string? path = null)
    {
        _defaultContent = defaultContent ?? WingmanTranslator.FidelityPrompt;
        _defaultVersion = defaultVersion ?? WingmanTranslator.DefaultInstructionsVersion;
        _path = path ?? Path.Combine(CcStorage.Root(), "wingman-instructions.json");
        Load();
    }

    public string DefaultContent => _defaultContent;
    public string DefaultVersion => _defaultVersion;
    public string DefaultHash => Hash(_defaultContent);

    /// <summary>Short stable content fingerprint (the real identity of a set of instructions).</summary>
    public static string Hash(string? s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var s = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path));
                    if (s is not null) _state = s;
                }
            }
            catch (Exception ex) { FileLog.Write($"[WingmanInstructionsStore] load FAILED: {ex.Message}"); _state = new(); }

            // First run, or a user who has never customized: they ride the latest deployed default,
            // so acknowledge it (no stale "update available" banner).
            if (!IsCustomizedNoLock() && Hash(_state.AckDefaultContent) != DefaultHash)
                AcknowledgeDefaultNoLock();
            else if (string.IsNullOrEmpty(_state.AckDefaultContent))
                AcknowledgeDefaultNoLock();
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { FileLog.Write($"[WingmanInstructionsStore] save FAILED: {ex.Message}"); }
    }

    private void AcknowledgeDefaultNoLock()
    {
        _state.AckDefaultVersion = _defaultVersion;
        _state.AckDefaultContent = _defaultContent;
        Save();
    }

    private bool IsCustomizedNoLock()
        => _state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is not null;

    private InstructionVersion? FindNoLock(string? id)
        => id is null ? null : _state.Versions.FirstOrDefault(v => v.Id == id);

    private string NextId()
    {
        // Monotonic per-process plus a content-independent suffix; Date.Now is fine here (not a workflow).
        _seq++;
        return $"v{DateTime.UtcNow:yyyyMMddHHmmss}-{_seq}";
    }

    /// <summary>True when the user has an active custom version (vs. riding the deployed default).</summary>
    public bool IsCustomized { get { lock (_lock) return IsCustomizedNoLock(); } }

    /// <summary>The instructions the wingman uses right now: the active custom version, else the default.</summary>
    public string ActiveContent
    {
        get
        {
            lock (_lock)
            {
                if (_state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is { } v) return v.Content;
                return _defaultContent;
            }
        }
    }

    /// <summary>The deployed default the user has customized has been superseded by a newer dev-team
    /// default. Only true while customized - a non-customized user always rides the latest default.</summary>
    public bool UpdateAvailable
    {
        get { lock (_lock) return IsCustomizedNoLock() && Hash(_state.AckDefaultContent) != DefaultHash; }
    }

    /// <summary>The active version (a custom one, or a synthesized record for the deployed default).</summary>
    public InstructionVersion Active()
    {
        lock (_lock)
        {
            if (_state.ActiveVersionId is not null && FindNoLock(_state.ActiveVersionId) is { } v) return v;
            return DefaultAsVersionNoLock();
        }
    }

    private InstructionVersion DefaultAsVersionNoLock() => new()
    {
        Id = "default",
        Content = _defaultContent,
        CreatedAtUtc = DateTime.UtcNow,
        Label = $"DevThrottle default (v{_defaultVersion})",
        Source = "default",
        Hash = DefaultHash,
    };

    /// <summary>Deployed default as a version record (for display / diff against a custom version).</summary>
    public InstructionVersion DefaultAsVersion() { lock (_lock) return DefaultAsVersionNoLock(); }

    /// <summary>The acknowledged (based-on) default content - the left side of the "our changes" diff.</summary>
    public (string version, string content) AcknowledgedDefault()
    {
        lock (_lock) return (_state.AckDefaultVersion, _state.AckDefaultContent);
    }

    /// <summary>Version history, newest first.</summary>
    public IReadOnlyList<InstructionVersion> Versions()
    {
        lock (_lock) return _state.Versions.OrderByDescending(v => v.CreatedAtUtc).ToList();
    }

    public InstructionVersion? Get(string id)
    {
        lock (_lock) return FindNoLock(id);
    }

    /// <summary>Save a new custom version from edited content and make it the active instructions.
    /// Acknowledges the current default (the user is editing against it). Throws on empty/oversized.</summary>
    public InstructionVersion Save(string content, string? label)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Instructions content is required.", nameof(content));
        if (content.Length > MaxContentChars)
            throw new ArgumentException($"Instructions exceed the {MaxContentChars}-character limit.", nameof(content));

        lock (_lock)
        {
            var v = new InstructionVersion
            {
                Id = NextId(),
                Content = content,
                CreatedAtUtc = DateTime.UtcNow,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                Source = "user",
                Hash = Hash(content),
            };
            _state.Versions.Add(v);
            _state.ActiveVersionId = v.Id;
            // Editing is done against the current default; acknowledge it so the banner only fires on a
            // LATER dev-team change.
            _state.AckDefaultVersion = _defaultVersion;
            _state.AckDefaultContent = _defaultContent;
            Save();
            FileLog.Write($"[WingmanInstructionsStore] saved version {v.Id} (len={content.Length}, hash={v.Hash})");
            return v;
        }
    }

    /// <summary>Make an existing version active again. Returns false if the id is unknown.</summary>
    public bool Revert(string id)
    {
        lock (_lock)
        {
            if (FindNoLock(id) is null) return false;
            _state.ActiveVersionId = id;
            Save();
            FileLog.Write($"[WingmanInstructionsStore] reverted active to {id}");
            return true;
        }
    }

    /// <summary>Adopt the deployed default: drop the active custom version and acknowledge the current
    /// default (clears <see cref="UpdateAvailable"/>).</summary>
    public void SwitchToDefault()
    {
        lock (_lock)
        {
            _state.ActiveVersionId = null;
            AcknowledgeDefaultNoLock();
            FileLog.Write($"[WingmanInstructionsStore] switched to deployed default v{_defaultVersion}");
        }
    }
}
