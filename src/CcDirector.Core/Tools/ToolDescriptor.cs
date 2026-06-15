namespace CcDirector.Core.Tools;

/// <summary>
/// Roll-up health of a tool, computed from its individual check results.
/// </summary>
public enum ToolStatus
{
    /// <summary>The binary is not present in the bin directory; nothing was run.</summary>
    NotBuilt,

    /// <summary>The tool's tests have not been run yet this session.</summary>
    Untested,

    /// <summary>Every declared check passed.</summary>
    Pass,

    /// <summary>At least one declared check failed.</summary>
    Fail,
}

/// <summary>
/// One tool in the catalog: its manifest metadata plus its resolved binary location and the
/// set of health checks declared for it. Pure data - the <see cref="ToolCatalogService"/>
/// builds these and the <see cref="ToolTestRunner"/> executes their tests.
/// </summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(
        string name,
        string category,
        string description,
        string? note,
        string binaryPath,
        bool isBuilt,
        bool isExpected,
        IReadOnlyList<ToolTest> tests)
    {
        Name = name;
        Category = category;
        Description = description;
        Note = note;
        BinaryPath = binaryPath;
        IsBuilt = isBuilt;
        IsExpected = isExpected;
        Tests = tests;
    }

    /// <summary>The tool's command name, e.g. <c>cc-vault</c>.</summary>
    public string Name { get; }

    /// <summary>Grouping shown in the UI (Documents, Email, Social, ...).</summary>
    public string Category { get; }

    /// <summary>One-line description of what the tool does.</summary>
    public string Description { get; }

    /// <summary>
    /// Optional honesty note, e.g. "Auth-gated; presence + version only". Surfaced in the UI so a
    /// tool with no smoke check explains itself rather than looking under-tested.
    /// </summary>
    public string? Note { get; }

    /// <summary>Absolute path to where the binary should live in the bin directory.</summary>
    public string BinaryPath { get; }

    /// <summary>True when <see cref="BinaryPath"/> exists on disk.</summary>
    public bool IsBuilt { get; }

    /// <summary>
    /// True when this machine's install is expected to provide the tool: it has an installer shim
    /// (<c>bin\&lt;name&gt;.cmd</c>) or a built binary. Distinguishes "installed but broken" (expected
    /// and not built) from "never installed here" (e.g. the extras tier, or tools not in this bundle).
    /// The home readiness only flags expected-but-broken tools, so optional tools never raise a warning.
    /// </summary>
    public bool IsExpected { get; }

    /// <summary>The declared health checks (always OnPath + Version; Smoke when the manifest has one).</summary>
    public IReadOnlyList<ToolTest> Tests { get; }
}
