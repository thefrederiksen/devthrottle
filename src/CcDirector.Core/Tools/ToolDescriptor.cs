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
        bool isOnPath,
        bool isExpected,
        IReadOnlyList<ToolTest> tests)
    {
        Name = name;
        Category = category;
        Description = description;
        Note = note;
        BinaryPath = binaryPath;
        IsBuilt = isBuilt;
        IsOnPath = isOnPath;
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

    /// <summary>
    /// Absolute path to the runnable binary. When the tool is built in the app's bundled bin
    /// directory (or its sibling python scripts directory) this points there; otherwise, when the
    /// tool only resolves on the user's PATH, it points at the PATH-resolved executable; otherwise it
    /// is the expected (non-existent) bin path, kept for display so the user can see where it was sought.
    /// </summary>
    public string BinaryPath { get; }

    /// <summary>
    /// True when the binary is present in the app's bundled bin directory (the "built into this build"
    /// diagnostic). This is deliberately separate from <see cref="IsAvailable"/>: a tool can be usable
    /// (resolvable on the user's PATH) without being bundled in this particular build.
    /// </summary>
    public bool IsBuilt { get; }

    /// <summary>
    /// True when the tool's command name resolves on the user's PATH (PATH + PATHEXT), using the same
    /// resolution rule the session-launch preflight uses (<see cref="Utilities.ExecutableResolver"/>).
    /// </summary>
    public bool IsOnPath { get; }

    /// <summary>
    /// The user-facing AVAILABILITY signal: the tool can actually be run on this machine because it is
    /// either bundled in this build (<see cref="IsBuilt"/>) or resolves on the user's PATH
    /// (<see cref="IsOnPath"/>). Unavailable only when it is on neither. This is what the Home readiness
    /// row and the Tools dashboard report, per issue #448.
    /// </summary>
    public bool IsAvailable => IsBuilt || IsOnPath;

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
