namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of GET /facts on a Director (issue #330, plan 1B): the machine facts the fleet
/// brain needs that were not previously visible to the Gateway - the cc-* tool inventory
/// with versions and the launcher presence/port fact. Served deterministically (a Fact in
/// the CONTRACT_AUDIT taxonomy); the Gateway pulls it on demand through the proxy leg
/// (GET /directors/{id}/facts).
/// </summary>
public sealed class DirectorFactsDto
{
    /// <summary>The Director that served these facts.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The machine the Director runs on.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The Director's own version.</summary>
    public string Version { get; set; } = "";

    /// <summary>The cc-* tool inventory (names + versions) from the embedded catalog manifest.</summary>
    public List<ToolInventoryItemDto> Tools { get; set; } = new();

    /// <summary>The launcher presence/port fact. Never null - "not installed" is a valid fact.</summary>
    public LauncherFactDto Launcher { get; set; } = new();
}

/// <summary>One catalog tool's inventory facts (issue #330).</summary>
public sealed class ToolInventoryItemDto
{
    /// <summary>The tool's command name, e.g. <c>cc-vault</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Catalog category (Documents, Email, ...).</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Installed version: the setup manifest's recorded per-tool version when present
    /// (installed.json), else the binary's own file-version resource, else - for tools the
    /// installer laid down as wheel-bundle console scripts (pyenv\Scripts) - the recorded
    /// "python-tools" bundle version, else null - an honestly-unknown version (e.g. a
    /// dev-built exe with no version resource), never an invented one.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>True when the tool's binary is present on this machine.</summary>
    public bool IsBuilt { get; set; }
}

/// <summary>
/// The launcher presence/port fact (issue #330): read from the launcher discovery file
/// <c>%LOCALAPPDATA%/cc-director/config/launcher/launcher.json</c> at request time.
/// Absent file = launcher not installed - a valid fact, not an error (cc-launcher has
/// not shipped yet, issue #250).
/// </summary>
public sealed class LauncherFactDto
{
    /// <summary>True when the launcher discovery file exists.</summary>
    public bool Installed { get; set; }

    /// <summary>The launcher's loopback REST port, when the discovery file declares one.</summary>
    public int? Port { get; set; }

    /// <summary>Why the port could not be read from a PRESENT discovery file (corrupt JSON,
    /// no port field). Null when absent (not installed) or read cleanly.</summary>
    public string? Error { get; set; }
}
