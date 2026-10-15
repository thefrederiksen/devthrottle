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
