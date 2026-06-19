using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>
/// Builds the tool catalog: reads the embedded manifest, resolves each tool's runnable binary
/// against the installed layout (a native exe in bin, else the python console-script exe in the
/// sibling pyenv\Scripts directory that the installer's bin\&lt;name&gt;.cmd shims forward to),
/// attaches the universal presence + version checks plus any declared smoke check, and reports
/// which built binaries are NOT in the manifest so coverage gaps are never silent.
///
/// This is pure, side-effect-free read logic - it launches no processes (that is the
/// <see cref="ToolTestRunner"/>'s job). Both the Avalonia UI and the Control API consume it.
/// </summary>
public sealed class ToolCatalogService
{
    private readonly string _binDir;
    private readonly string? _searchPath;
    private readonly string? _pathExt;

    /// <summary>Construct against the real bin directory and the current process PATH/PATHEXT.</summary>
    public ToolCatalogService() : this(CcStorage.Bin()) { }

    /// <summary>
    /// Construct against an explicit bin directory, using the current process PATH/PATHEXT for the
    /// on-PATH availability check (used by tests that only care about bin-dir resolution).
    /// </summary>
    public ToolCatalogService(string binDir)
        : this(binDir,
               Environment.GetEnvironmentVariable("PATH"),
               Environment.GetEnvironmentVariable("PATHEXT"))
    {
    }

    /// <summary>
    /// Construct against an explicit bin directory AND an explicit PATH/PATHEXT search list. Exposed so
    /// the on-PATH availability determination can be exercised deterministically without depending on
    /// (or mutating) the real machine PATH.
    /// </summary>
    public ToolCatalogService(string binDir, string? searchPath, string? pathExt)
    {
        _binDir = binDir ?? throw new ArgumentNullException(nameof(binDir));
        _searchPath = searchPath;
        _pathExt = pathExt;
    }

    /// <summary>
    /// Build the full catalog: one <see cref="ToolDescriptor"/> per manifest entry, ordered by
    /// category then name.
    /// </summary>
    public IReadOnlyList<ToolDescriptor> GetCatalog()
    {
        FileLog.Write($"[ToolCatalogService] GetCatalog: binDir={_binDir}");
        try
        {
            var manifest = ToolManifest.LoadEmbedded();
            var descriptors = new List<ToolDescriptor>(manifest.Tools.Count);

            foreach (var entry in manifest.Tools)
                descriptors.Add(BuildDescriptor(entry));

            descriptors.Sort((a, b) =>
            {
                var byCategory = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                return byCategory != 0 ? byCategory : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            FileLog.Write($"[ToolCatalogService] GetCatalog: {descriptors.Count} tools, {descriptors.Count(d => d.IsBuilt)} built, {descriptors.Count(d => d.IsOnPath)} on PATH, {descriptors.Count(d => d.IsAvailable)} available");
            return descriptors;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolCatalogService] GetCatalog FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Build the descriptor for a single manifest entry.</summary>
    public ToolDescriptor GetTool(string name)
    {
        var entry = ToolManifest.LoadEmbedded().Tools.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Tool not in manifest: {name}");
        return BuildDescriptor(entry);
    }

    /// <summary>
    /// Built binaries present in the bin directory that the manifest does not mention. Reported so
    /// the UI can show "unmanaged" tools instead of pretending the catalog is exhaustive. Build
    /// artifacts (RID-suffixed duplicates like <c>*-win-x64</c> and the Director itself) are
    /// excluded - they are not user-facing cc-* tools.
    /// </summary>
    public IReadOnlyList<string> GetUnmanagedBinaries()
    {
        if (!Directory.Exists(_binDir))
            return Array.Empty<string>();

        var managed = new HashSet<string>(
            ToolManifest.LoadEmbedded().Tools.Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        var unmanaged = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_binDir, "*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("-win-x64", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("cc-director", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith("cc-", StringComparison.OrdinalIgnoreCase)) continue;
            if (managed.Contains(name)) continue;
            unmanaged.Add(name);
        }

        unmanaged.Sort(StringComparer.OrdinalIgnoreCase);
        return unmanaged;
    }

    private ToolDescriptor BuildDescriptor(ToolManifestEntry entry)
    {
        // "Built into this build" = present in the app's bundled bin dir (or its sibling pyenv\Scripts).
        var resolvedInBin = ResolveRunnableBinary(entry.Name);
        var isBuilt = resolvedInBin is not null;

        // "Available on PATH" = the command name resolves on the user's PATH (PATH + PATHEXT), the same
        // resolution rule the session-launch preflight uses. This is independent of the bundled bin dir:
        // a fully-installed machine resolves cc-* on PATH even when this build ships an empty bin dir.
        var resolvedOnPath = Utilities.ExecutableResolver.Resolve(entry.Name, _searchPath, _pathExt);
        var isOnPath = resolvedOnPath is not null;

        // BinaryPath is the runnable path the test runner / Control API will launch: prefer the bundled
        // build, then the PATH-resolved exe, then the expected (non-existent) bin path for display only.
        var binaryPath = resolvedInBin ?? resolvedOnPath ?? Path.Combine(_binDir, ResolveBinaryFileName(entry.Name));

        // "Expected here" = the install is meant to provide the tool: it placed a shim (bin\<name>.cmd),
        // built it into this bundle, or the tool resolves on the user's PATH. A tool with none of these
        // was never installed on this machine (extras tier, a different bundle, or drift), so the home
        // readiness must not nag about it. A shim WITHOUT a runnable binary is the broken half-install
        // case (the shim survives a venv wipe, the pyenv\Scripts exe does not).
        var hasShim = File.Exists(Path.Combine(_binDir, entry.Name + ".cmd"));
        var isExpected = hasShim || isBuilt || isOnPath;

        var tests = new List<ToolTest>
        {
            new(ToolTestKind.OnPath, Array.Empty<string>(), null),
            new(ToolTestKind.Version, new[] { "--version" }, null),
        };

        if (entry.Smoke is { } smoke && smoke.Args.Count > 0)
            tests.Add(new ToolTest(ToolTestKind.Smoke, smoke.Args.ToArray(), smoke.ExpectContains));

        return new ToolDescriptor(
            name: entry.Name,
            category: entry.Category,
            description: entry.Description,
            note: entry.Note,
            binaryPath: binaryPath,
            isBuilt: isBuilt,
            isOnPath: isOnPath,
            isExpected: isExpected,
            tests: tests);
    }

    /// <summary>
    /// Resolve the RUNNABLE binary for a tool against the installed layout, in order:
    /// a native exe in the bin directory (e.g. .NET tools), else - on Windows - the python
    /// console-script exe in the sibling <c>pyenv\Scripts</c> directory, which is exactly where
    /// the installer's <c>bin\&lt;name&gt;.cmd</c> shims forward to (PythonToolsInstaller writes
    /// <c>"%~dp0..\pyenv\Scripts\&lt;name&gt;.exe" %*</c>). Resolving the shim TARGET keeps
    /// execution direct (no cmd.exe hop, no batch argument re-parsing) and makes IsBuilt truthful
    /// on shim-based installs. Returns null when the tool is not built in either location.
    /// </summary>
    private string? ResolveRunnableBinary(string toolName)
    {
        var native = Path.Combine(_binDir, ResolveBinaryFileName(toolName));
        if (File.Exists(native))
            return native;

        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(_binDir));
            if (root is not null)
            {
                var pyenvExe = Path.Combine(root, "pyenv", "Scripts", toolName + ".exe");
                if (File.Exists(pyenvExe))
                    return pyenvExe;
            }
        }

        return null;
    }

    /// <summary>The bin file name for a tool. On Windows tools build to <c>&lt;name&gt;.exe</c>.</summary>
    private static string ResolveBinaryFileName(string toolName)
        => OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
}
