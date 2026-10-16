namespace CcDirector.Gateway.Contracts;

/// <summary>One file of a kit: its path inside the kit folder, with forward slashes, and its whole text.</summary>
public sealed class FactoryKitFileDto
{
    /// <summary>The path inside the kit folder, such as <c>brief.md</c> or <c>files/send.py</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>The file's whole text, exactly as it is to be written.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// The answer to <c>GET /factory-definitions/{factory}/versions/{version}/kit</c>: the kit the Gateway composed for one
/// seat from one published version. The Director writes every file as it stands; it composes nothing itself.
/// </summary>
public sealed class FactoryKitResponse
{
    /// <summary>The factory, in its one spelling.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat the kit is for.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The published definition version the kit was built from.</summary>
    public int Version { get; set; }

    /// <summary><see cref="FactoryKits.Seat"/> or <see cref="FactoryKits.RulesOnly"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Every file of the kit, in ordinal order of their paths.</summary>
    public List<FactoryKitFileDto> Files { get; set; } = new();
}

/// <summary>
/// WHAT A CHECK FOUND, carried on the session start the check caused (Factory Definition mission, phase 5b, design 3.4).
/// The Director writes it, whole, as <c>check.json</c> in the seat's kit before the agent starts, so the agent reads
/// what the check reported rather than running it again.
/// </summary>
public sealed class FactoryCheckDto
{
    /// <summary>The check's path inside the definition, such as <c>checks/new_mail.py</c>.</summary>
    public string CheckPath { get; set; } = "";

    /// <summary>How many things the check found to do. Always above 0 on a start: a count of 0 starts nothing.</summary>
    public int Count { get; set; }

    /// <summary>The check's own words about what it found (at most 8 KB), or null when it gave none.</summary>
    public string? Context { get; set; }

    /// <summary>When the check ran, in UTC.</summary>
    public DateTime CheckedUtc { get; set; }

    /// <summary>The published definition version the check was run from.</summary>
    public int DefinitionVersion { get; set; }

    /// <summary>The seat the check started.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The start's number: its place among the seat's starts, from 1.</summary>
    public int Start { get; set; }
}
