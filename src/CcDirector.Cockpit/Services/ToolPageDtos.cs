using System.Text.Json.Serialization;

namespace CcDirector.Cockpit.Services;

// DTOs for the three Blazor tool pages (issue #183: exes/transcripts/dictionary converted
// from static HTML to Blazor). These mirror the JSON the Gateway already returns for the
// endpoints the static pages fetched same-origin - no endpoint contract changes.

// ===== /exes/list =====

/// <summary>The <c>GET /exes/list</c> payload: local Directors + build-slot status. Mirrors the
/// anonymous JSON shaped in <c>ExesEndpoints</c> on the Gateway.</summary>
public sealed class ExesListDto
{
    public string MachineName { get; set; } = "";

    /// <summary>Repo root when the Gateway runs from inside the cc-director repo; empty string
    /// (treated as null) otherwise, which disables slot management on the page.</summary>
    public string RepoRoot { get; set; } = "";

    public List<ExesDirectorDto> Directors { get; set; } = new();
    public List<ExesSlotDto> Slots { get; set; } = new();
}

public sealed class ExesDirectorDto
{
    public string DirectorId { get; set; } = "";
    public int Pid { get; set; }

    /// <summary>Slot number 1-4 when the exe path resolves to a build slot; null otherwise.</summary>
    public int? Slot { get; set; }

    public string ExePath { get; set; } = "";
    public string ControlEndpoint { get; set; } = "";
    public string? DirectorUrl { get; set; }
    public string? Version { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? Source { get; set; }
    public string? SessionError { get; set; }
    public List<ExesSessionDto> Sessions { get; set; } = new();
}

public sealed class ExesSessionDto
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    public string? Agent { get; set; }
    public string? ActivityState { get; set; }
    public string? StatusColor { get; set; }
    public string? RepoPath { get; set; }
}

public sealed class ExesSlotDto
{
    public int Slot { get; set; }
    public bool Exists { get; set; }
    public string ExePath { get; set; } = "";
    public DateTime? LastBuiltUtc { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Present (non-null) when a local Director's exe resolves to this slot file.</summary>
    public ExesSlotRunningDto? Running { get; set; }
}

public sealed class ExesSlotRunningDto
{
    public int Pid { get; set; }
    public string DirectorId { get; set; } = "";
}

/// <summary>The <c>POST /exes/slots/{n}/build-start</c> success body.</summary>
public sealed class BuildStartResultDto
{
    public bool Built { get; set; }
    public bool Started { get; set; }
    public int Slot { get; set; }
    public int Pid { get; set; }
    public string? BuildTail { get; set; }
}

// ===== /ingest/dictionary =====

/// <summary>The dictation glossary, shape of <c>GET/PUT /ingest/dictionary</c>. Mirrors the
/// Gateway's internal DictionaryDto: vocabulary list, term -&gt; variant map, and named profiles.</summary>
public sealed class DictionaryDto
{
    [JsonPropertyName("vocabulary")]
    public List<string> Vocabulary { get; set; } = new();

    [JsonPropertyName("commonMistranscriptions")]
    public Dictionary<string, List<string>> CommonMistranscriptions { get; set; } = new();

    [JsonPropertyName("profiles")]
    public Dictionary<string, DictionaryProfileDto> Profiles { get; set; } = new();
}

public sealed class DictionaryProfileDto
{
    [JsonPropertyName("cleanupEnabled")]
    public bool CleanupEnabled { get; set; }
}
