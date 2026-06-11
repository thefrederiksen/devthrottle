using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Launcher;

/// <summary>
/// Launches arbitrary executables with clean process parentage.
///
/// The launcher itself runs outside any ConPty (started by the HKCU Run key or
/// Start Menu), so a child it starts has clean parentage - the rule-0b fix.
///
/// Parentage modes:
///   - GUI apps (UseShellExecute = true): launched with shell association, no ConPty.
///   - Headless/silent apps (UseShellExecute = false, CreateNoWindow = true): no console
///     handle inherited.
///   - .cmd / .bat files: routed through cmd.exe (Windows limitation: shell scripts
///     cannot be launched directly with UseShellExecute = false in all contexts).
///
/// Every launch is FileLog-audited with the resolved path and the caller description.
/// </summary>
public sealed class LaunchService
{
    private readonly ConcurrentDictionary<int, string> _launched = new();

    /// <summary>
    /// Build a ProcessStartInfo for the given launch request. No real spawn - pure,
    /// unit-testable seam.
    /// </summary>
    public ProcessStartInfo BuildStartInfo(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("Path must not be empty.", nameof(request));

        if (!File.Exists(request.Path))
            throw new FileNotFoundException($"Executable not found: {request.Path}", request.Path);

        var ext = Path.GetExtension(request.Path).ToUpperInvariant();
        var isBatchFile = ext is ".CMD" or ".BAT";

        ProcessStartInfo psi;
        if (isBatchFile)
        {
            // Batch files must be launched via cmd.exe; UseShellExecute=false with cmd lets
            // us control the window and keep parentage clean.
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"{request.Path}\"{(request.Args is { Length: > 0 } a ? " " + a : "")}",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else if (request.Headless)
        {
            psi = new ProcessStartInfo
            {
                FileName = request.Path,
                Arguments = request.Args ?? "",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else
        {
            // GUI app: UseShellExecute = true -> shell association, no ConPty inheritance.
            // This is the clean-parentage recipe (rule-0b fix).
            psi = new ProcessStartInfo
            {
                FileName = request.Path,
                Arguments = request.Args ?? "",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = true,
            };
        }

        return psi;
    }

    /// <summary>
    /// Launch the given path with clean parentage. Returns the started process PID.
    /// Throws if the path is missing or the process fails to start.
    /// </summary>
    public int Launch(LaunchRequest request, string caller = "api")
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = BuildStartInfo(request);

        FileLog.Write($"[LaunchService] Launch: path={request.Path} args={request.Args ?? "(none)"} " +
                      $"headless={request.Headless} cwd={psi.WorkingDirectory} caller={caller}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for: {request.Path}");

        _launched[proc.Id] = request.Path;
        FileLog.Write($"[LaunchService] Launched pid={proc.Id} path={request.Path}");
        return proc.Id;
    }

    /// <summary>PIDs of processes launched since this service instance was created.</summary>
    public IReadOnlyList<int> LaunchedPids => _launched.Keys.ToList();
}

/// <summary>A request to launch an executable.</summary>
public sealed class LaunchRequest
{
    /// <summary>Absolute path to the executable (or .cmd/.bat).</summary>
    public required string Path { get; init; }

    /// <summary>Optional command-line arguments.</summary>
    public string? Args { get; init; }

    /// <summary>Optional working directory (defaults to the executable's directory).</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// When true, use UseShellExecute=false + CreateNoWindow=true (headless/hidden mode).
    /// When false (default), use UseShellExecute=true (GUI mode, clean parentage via shell).
    /// </summary>
    public bool Headless { get; init; }
}
