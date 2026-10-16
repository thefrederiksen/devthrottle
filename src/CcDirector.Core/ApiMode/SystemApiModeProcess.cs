using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// The real API-mode process: <see cref="Process"/> with standard input, output and error piped, on Windows,
/// Linux and macOS. No shell is involved; the arguments are passed one by one.
/// </summary>
public sealed class SystemApiModeProcess : IApiModeProcess
{
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private StreamReader? _error;

    public int ProcessId { get; private set; }

    public bool HasExited => _process is { } process && process.HasExited;

    public void Start(ApiModeProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        FileLog.Write($"[SystemApiModeProcess] Start: {startInfo.Describe()}");
        if (_process is not null)
            throw new InvalidOperationException("This process was already started.");

        if (!Directory.Exists(startInfo.WorkingDirectory))
            throw new ApiModeStartException(startInfo.Executable,
                $"Cannot start '{startInfo.Executable}': the working folder '{startInfo.WorkingDirectory}' does not exist.");

        var resolved = ApiModeExecutableResolver.Resolve(
            startInfo.Executable,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATHEXT") : null,
            OperatingSystem.IsWindows(),
            File.Exists);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in startInfo.Arguments)
            processStartInfo.ArgumentList.Add(argument);

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = ApiModeEnvironment.Build(InheritedSessionEnvironment.Inherited(comparer), startInfo, comparer);
        processStartInfo.Environment.Clear();
        foreach (var (name, value) in environment)
            processStartInfo.Environment[name] = value;

        var process = new Process { StartInfo = processStartInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            FileLog.Write($"[SystemApiModeProcess] Start FAILED: {resolved}: {ex.Message}");
            throw new ApiModeStartException(startInfo.Executable,
                $"Cannot start '{startInfo.Executable}' (resolved to '{resolved}'): {ex.Message}", ex);
        }

        _process = process;
        _input = process.StandardInput.BaseStream;
        if (OperatingSystem.IsWindows())
        {
            // Windows gives a child synchronous pipes, whose reads cannot be cancelled; read them on threads of their
            // own so the transport can always stop reading (see DedicatedThreadReadStream).
            _output = new DedicatedThreadReadStream(process.StandardOutput.BaseStream, $"process {process.Id} standard output");
            _error = new StreamReader(
                new DedicatedThreadReadStream(process.StandardError.BaseStream, $"process {process.Id} standard error"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        else
        {
            _output = process.StandardOutput.BaseStream;
            _error = process.StandardError;
        }
        ProcessId = process.Id;
        FileLog.Write($"[SystemApiModeProcess] Start: process {ProcessId} started from {resolved}");
    }

    public async Task WriteLineAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        var input = _input ?? throw new InvalidOperationException("The process has not been started.");
        await input.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var output = _output ?? throw new InvalidOperationException("The process has not been started.");
        return output.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken)
    {
        var error = _error ?? throw new InvalidOperationException("The process has not been started.");
        return error.ReadLineAsync(cancellationToken);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("The process has not been started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[SystemApiModeProcess] WaitForExitAsync: process {ProcessId} exited with code {process.ExitCode}");
        return process.ExitCode;
    }

    public void CloseInput()
    {
        FileLog.Write($"[SystemApiModeProcess] CloseInput: process {ProcessId}");
        var process = _process ?? throw new InvalidOperationException("The process has not been started.");
        process.StandardInput.Close();
    }

    public void Kill()
    {
        FileLog.Write($"[SystemApiModeProcess] Kill: process {ProcessId}");
        var process = _process ?? throw new InvalidOperationException("The process has not been started.");
        if (process.HasExited)
        {
            FileLog.Write($"[SystemApiModeProcess] Kill: process {ProcessId} had already exited");
            return;
        }

        // The agent's launcher can be a script that starts the real runtime (an npm shim starting node), so the
        // whole tree goes, or the agent would outlive the kill.
        process.Kill(entireProcessTree: true);
    }

    public void Dispose()
    {
        FileLog.Write($"[SystemApiModeProcess] Dispose: process {ProcessId}");
        // The Windows read streams cancel their blocked reads on disposal; the process's own streams close with it.
        if (OperatingSystem.IsWindows())
        {
            _output?.Dispose();
            _error?.Dispose();
        }
        _process?.Dispose();
    }
}

/// <summary>Finds the file an executable name refers to, without starting anything.</summary>
public static class ApiModeExecutableResolver
{
    /// <summary>
    /// Resolve <paramref name="executable"/> to a file. A name with a folder in it must exist as given; a bare name
    /// is looked up in <paramref name="searchPath"/> (on Windows, also with each <paramref name="windowsExtensions"/>
    /// ending, so <c>pi</c> finds <c>pi.cmd</c>). Throws <see cref="ApiModeStartException"/> naming the executable
    /// when no file is found - never a guess.
    /// </summary>
    public static string Resolve(string executable, string? searchPath, string? windowsExtensions, bool isWindows, Func<string, bool> fileExists)
    {
        FileLog.Write($"[ApiModeExecutableResolver] Resolve: executable={executable}");
        if (string.IsNullOrWhiteSpace(executable))
            throw new ApiModeStartException(executable, "Cannot start an API-mode agent: no executable was given.");

        // Windows runs only a file with a program ending, and npm puts an extensionless shell script beside
        // pi.cmd: a bare 'pi' must find pi.cmd, never the script Windows cannot run.
        string[] endings = isWindows && !Path.HasExtension(executable)
            ? (windowsExtensions ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];
        var hasFolder = executable.Contains('/') || (isWindows && executable.Contains('\\'));
        var separator = isWindows ? ';' : ':';
        var folders = hasFolder
            ? [""]
            : (searchPath ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var folder in folders)
        {
            var stem = folder.Length == 0 ? executable : Path.Combine(folder, executable);
            foreach (var ending in endings)
            {
                var candidate = stem + ending;
                if (fileExists(candidate))
                {
                    FileLog.Write($"[ApiModeExecutableResolver] Resolve: {executable} -> {candidate}");
                    return candidate;
                }
            }
        }

        var where = hasFolder ? "that file does not exist" : "it was not found on the search path (PATH)";
        FileLog.Write($"[ApiModeExecutableResolver] Resolve FAILED: {executable}: {where}");
        throw new ApiModeStartException(executable, $"Cannot start '{executable}': {where}.");
    }
}

/// <summary>Builds an API-mode process's environment from a base and the caller's removals and settings.</summary>
public static class ApiModeEnvironment
{
    /// <summary>
    /// <paramref name="inherited"/> minus <see cref="ApiModeProcessStartInfo.RemoveVariables"/> and every name starting
    /// with one of <see cref="ApiModeProcessStartInfo.RemoveVariablePrefixes"/>, then with
    /// <see cref="ApiModeProcessStartInfo.SetVariables"/> applied. Names compare with <paramref name="comparer"/>
    /// (case-insensitive on Windows).
    /// </summary>
    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> inherited, ApiModeProcessStartInfo startInfo, StringComparer comparer)
    {
        var prefixComparison = ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var removed = new HashSet<string>(startInfo.RemoveVariables, comparer);
        var environment = new Dictionary<string, string>(comparer);
        foreach (var (name, value) in inherited)
        {
            if (removed.Contains(name))
                continue;
            if (startInfo.RemoveVariablePrefixes.Any(prefix => name.StartsWith(prefix, prefixComparison)))
                continue;
            environment[name] = value;
        }

        foreach (var (name, value) in startInfo.SetVariables)
            environment[name] = value;

        FileLog.Write($"[ApiModeEnvironment] Build: {inherited.Count} inherited, {environment.Count} given, {startInfo.SetVariables.Count} set");
        return environment;
    }
}
