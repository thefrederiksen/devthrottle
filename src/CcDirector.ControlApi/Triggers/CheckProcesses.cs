using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi.Triggers;

/// <summary>One external process a check from a definition needs: Python, uv, or cc-secrets.</summary>
/// <param name="Environment">The child's WHOLE environment, or null for the Director's own, unchanged.</param>
public sealed record CheckProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    int TimeoutSeconds);

/// <summary>What one external process came to. <see cref="StartError"/> is set, and nothing else, when it could not
/// start; <see cref="ExitCode"/> is null when it was killed at its timeout.</summary>
public sealed record CheckProcessResult(string? StartError, int? ExitCode, bool TimedOut, string Output, string ErrorOutput)
{
    public static CheckProcessResult CouldNotStart(string reason) => new(reason, null, false, "", "");
}

/// <summary>
/// THE SEAM FOR EVERY PROCESS A DEFINITION CHECK STARTS (Factory Definition, phase 5b): finding Python, uv and
/// cc-secrets on this machine, and running one of them. Production is <see cref="CheckProcesses"/>; the tests pass a
/// fake, so no test of the runner starts a real process.
/// </summary>
public interface ICheckProcesses
{
    /// <summary>The full path of <paramref name="name"/> on PATH, never the Windows Store alias, or null.</summary>
    string? FindOnPath(string name);

    /// <summary>The full path of the installed cc-secrets, found the way every cc tool is, or null.</summary>
    string? FindCcSecrets();

    /// <summary>Run one process to its end or its timeout. Cancelling kills it and throws.</summary>
    Task<CheckProcessResult> RunAsync(CheckProcessStart start, CancellationToken ct);
}

/// <summary>The production <see cref="ICheckProcesses"/>: real processes on this machine.</summary>
public sealed class CheckProcesses : ICheckProcesses
{
    /// <summary>How long a killed process may take to go before its output is read anyway.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public string? FindOnPath(string name)
    {
        var found = FindOnPath(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));
        FileLog.Write($"[CheckProcesses] FindOnPath: name={name}, found={found ?? "(none)"}");
        return found;
    }

    /// <summary>
    /// The pure form of <see cref="FindOnPath(string)"/>. A PATH folder that is the Windows Store's app alias folder
    /// (<c>Microsoft\WindowsApps</c>) is skipped: its <c>python.exe</c> is a stub that opens the Store instead of
    /// running anything, so "Python is on PATH" would be a lie there.
    /// </summary>
    public static string? FindOnPath(string name, string? searchPath, string? pathExt)
    {
        if (string.IsNullOrEmpty(searchPath)) return null;
        var kept = searchPath.Split(Path.PathSeparator)
            .Where(dir => !IsWindowsStoreAliasFolder(dir));
        return ExecutableResolver.Resolve(name, string.Join(Path.PathSeparator, kept), pathExt);
    }

    /// <summary>True for the folder holding the Windows Store's app execution aliases.</summary>
    public static bool IsWindowsStoreAliasFolder(string dir)
    {
        var normal = dir.Trim().Replace('/', '\\').TrimEnd('\\');
        return normal.EndsWith(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase);
    }

    public string? FindCcSecrets()
    {
        // No explicit-path override, as for the secret transfer: a variable that could point the Director at another
        // program is not something the tool that hands a check its secrets should honour.
        var found = CcToolExecutable.Resolve(CcSecretsRunner.ToolName, explicitPathEnvVar: null);
        FileLog.Write($"[CheckProcesses] FindCcSecrets: found={found ?? "(none)"}");
        return found;
    }

    public async Task<CheckProcessResult> RunAsync(CheckProcessStart start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        FileLog.Write($"[CheckProcesses] RunAsync: file={start.FileName}, args={start.Arguments.Count}, timeout={start.TimeoutSeconds}s");
        var psi = new ProcessStartInfo
        {
            FileName = start.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Standard input is closed at once, so a check that reads it gets end of input instead of hanging.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(start.WorkingDirectory)) psi.WorkingDirectory = start.WorkingDirectory;
        foreach (var a in start.Arguments) psi.ArgumentList.Add(a);
        if (start.Environment is not null)
        {
            psi.Environment.Clear();
            foreach (var (name, value) in start.Environment) psi.Environment[name] = value;
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return CheckProcessResult.CouldNotStart($"{start.FileName} could not start");
        }
        catch (Win32Exception ex)
        {
            FileLog.Write($"[CheckProcesses] RunAsync FAILED: {start.FileName} could not start: {ex.Message}");
            return CheckProcessResult.CouldNotStart($"{start.FileName} could not start: {ex.Message}");
        }
        proc.StandardInput.Close();

        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(start.TimeoutSeconds));
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            KillTree(proc);
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            throw;
        }

        var output = await outTask.ConfigureAwait(false);
        var error = await errTask.ConfigureAwait(false);
        FileLog.Write($"[CheckProcesses] RunAsync: file={start.FileName}, exit={(timedOut ? "none" : proc.ExitCode.ToString())}, timedOut={timedOut}");
        return new CheckProcessResult(null, timedOut ? null : proc.ExitCode, timedOut, output, error);
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            proc.WaitForExit(KillWait);
        }
        catch (InvalidOperationException)
        {
            // not-an-error: it exited between the check and the kill
        }
        catch (Win32Exception ex)
        {
            FileLog.Write($"[CheckProcesses] kill FAILED: {ex.Message}");
        }
    }
}
