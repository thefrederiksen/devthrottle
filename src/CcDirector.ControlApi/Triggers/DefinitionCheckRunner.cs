using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Factory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Triggers;

/// <summary>
/// RUNS ONE CHECK FROM A FACTORY'S DEFINITION (Factory Definition, phase 5b, design section 2). A command trigger is
/// a command line in a repository folder; a check from a definition is a Python file in a PUBLISHED version of the
/// factory's definition. This runner:
///
///   1. Downloads that version's <c>checks/</c> and <c>files/</c> once, into
///      <c>&lt;storage&gt;/factory-checks/&lt;factory&gt;/&lt;version&gt;/</c> - written whole into a temporary folder
///      and then renamed, so a half-written folder is never run - and runs the check in that folder. Never inside a
///      checkout. <see cref="Prune"/> deletes the versions no assignment names any more.
///   2. Chooses the interpreter from the check's script header: <c>uv run --script</c> when it declares packages,
///      else plain Python found on PATH (never the Windows Store alias). No other interpreter is tried.
///   3. With declared secrets, reads <c>cc-secrets list --json</c> first: an entry this machine does not hold, or one
///      not allowed for <c>run</c>, is a start error naming it and the check never runs. Each secret arrives in its own
///      cc-secrets variable, and <c>CC_CHECK_SECRET_VARS</c> tells the check which.
///   4. Gives the check the Director's environment minus what a session is never handed
///      (<see cref="InheritedSessionEnvironment"/>) and minus EVERY variable whose name begins <c>CC_</c> - a Director
///      started from inside a session carries that session's Gateway key, id and address, which a session is stamped
///      over and a check is not - plus its own <c>CC_CHECK_STATE_FILE</c>, <c>CC_CHECK_SECRET_VARS</c>,
///      <c>CC_CHECK_FACTORY</c> and <c>CC_CHECK_SEAT</c>.
///   5. ALWAYS creates the state file before the run, holding the stored state (empty on a first run), in a per-run
///      folder under the Director's storage; reads it back after the run, at most 64 KB, and deletes the folder.
///
/// It decides NOTHING from the result, exactly like a command trigger: the Gateway reads the report under the check
/// contract. Anything that stops the check from running - a refusal, a missing program, or this machine's disk
/// failing under the runner - is a <see cref="TriggerCheckReport.StartError"/>, never a skip, so the start turns red
/// on the Gateway with the reason.
/// </summary>
public sealed class DefinitionCheckRunner
{
    public const string StateFileVariable = "CC_CHECK_STATE_FILE";
    public const string SecretVarsVariable = "CC_CHECK_SECRET_VARS";
    public const string FactoryVariable = "CC_CHECK_FACTORY";
    public const string SeatVariable = "CC_CHECK_SEAT";

    /// <summary>No variable of the Director's or of a session's whose name begins with this reaches a check: a check
    /// needs none of them, and a rule that names the prefix cannot miss the next one someone adds.</summary>
    public const string DirectorVariablePrefix = "CC_";

    /// <summary>How long <c>cc-secrets list --json</c> may take.</summary>
    public const int SecretsListTimeoutSeconds = 30;

    private const string ChecksFolder = "factory-checks";
    private const string RunsFolder = "factory-check-runs";
    private const string PartialPrefix = ".partial-";

    private readonly string _directorId;
    private readonly string _storageRoot;
    private readonly Func<IFactoryCheckSource?> _source;
    private readonly ICheckProcesses _processes;
    private readonly Func<Dictionary<string, string>> _baseEnvironment;
    private readonly Func<DateTime> _nowUtc;
    private readonly bool _isWindows;

    // One download of a version at a time; two checks of one version wait for the first to write the folder.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new(StringComparer.OrdinalIgnoreCase);
    // The version folders and per-run folders a running check is using now; Prune never deletes one of them.
    private readonly object _inUseGate = new();
    private readonly Dictionary<string, int> _inUse = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="storageRoot">The Director's own storage folder (<c>CcStorage.Root()</c>).</param>
    /// <param name="source">Read on every download, so a replaced Gateway client is picked up. Null: no Gateway.</param>
    /// <param name="baseEnvironment">The Director's environment as a session would inherit it: production is
    /// <see cref="InheritedSessionEnvironment.Inherited"/>. A fresh dictionary on every call.</param>
    public DefinitionCheckRunner(string directorId, string storageRoot, Func<IFactoryCheckSource?> source,
        ICheckProcesses processes, Func<Dictionary<string, string>> baseEnvironment, Func<DateTime> nowUtc, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(directorId)) throw new ArgumentException("a Director id is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(storageRoot)) throw new ArgumentException("a storage folder is required", nameof(storageRoot));
        _directorId = directorId;
        _storageRoot = storageRoot;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _baseEnvironment = baseEnvironment ?? throw new ArgumentNullException(nameof(baseEnvironment));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _isWindows = isWindows;
    }

    /// <summary>The production base environment: the Director's own, minus what a session never inherits.</summary>
    public static Dictionary<string, string> SessionInheritedEnvironment()
        => InheritedSessionEnvironment.Inherited(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Where one version of one factory's checks is written.</summary>
    public string VersionFolder(string factory, int version)
        => Path.Combine(_storageRoot, ChecksFolder, factory, version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The name of the Python this machine runs a check with: <c>python.exe</c> on Windows, else <c>python3</c>.</summary>
    public string PythonName => PythonNameFor(_isWindows);

    /// <summary>The name of the Python a check runs with on Windows or elsewhere.</summary>
    public static string PythonNameFor(bool isWindows) => isWindows ? "python.exe" : "python3";

    /// <summary>The name of the uv a check that declares packages runs under on Windows or elsewhere.</summary>
    public static string UvNameFor(bool isWindows) => isWindows ? "uv.exe" : "uv";

    /// <summary>
    /// The Python a check runs with, found the one way the runner finds it: <see cref="PythonNameFor"/> on PATH, never
    /// the Windows Store alias. The publish's machine question asks with this same code, so its answer and the run agree.
    /// </summary>
    public static string? FindPython(ICheckProcesses processes, bool isWindows)
        => (processes ?? throw new ArgumentNullException(nameof(processes))).FindOnPath(PythonNameFor(isWindows));

    /// <summary>The uv a check that declares packages runs under, found the one way the runner finds it.</summary>
    public static string? FindUv(ICheckProcesses processes, bool isWindows)
        => (processes ?? throw new ArgumentNullException(nameof(processes))).FindOnPath(UvNameFor(isWindows));

    /// <summary>
    /// The variable a cc-secrets entry arrives in: its own <c>envName</c> when it has one, else the entry name
    /// upper-cased with dashes and dots turned to underscores (the Delivery Lead's ruling, mission 5.5a).
    /// </summary>
    public static string VariableFor(string entry, string? envName)
        => !string.IsNullOrWhiteSpace(envName) ? envName : entry.ToUpperInvariant().Replace('-', '_').Replace('.', '_');

    /// <summary>Run one check from a definition and say what it produced. Never throws for anything the check or this
    /// machine did; only a cancellation ends it with an exception.</summary>
    public async Task<TriggerCheckReport> RunAsync(TriggerAssignmentDto trigger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        FileLog.Write($"[DefinitionCheckRunner] RunAsync: trigger={trigger.Name}, factory={trigger.Factory}, version={trigger.DefinitionVersion}, check={trigger.CheckPath}, secrets={trigger.Secrets?.Count ?? 0}, due={trigger.DueRunId ?? "-"}");
        try
        {
            return await RunCheckedAsync(trigger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This runner is the check's entry point: a disk that fails under it is a reason on the Gateway, not a
            // report that never arrives.
            FileLog.Write($"[DefinitionCheckRunner] RunAsync FAILED: trigger={trigger.Name}, {ex.Message}");
            return Failed($"the Director could not use its storage for the check: {ex.Message}");
        }
    }

    private async Task<TriggerCheckReport> RunCheckedAsync(TriggerAssignmentDto trigger, CancellationToken ct)
    {
        if (WhyNotRunnable(trigger) is { } bad) return Failed(bad);
        var factory = trigger.Factory!;
        var version = trigger.DefinitionVersion!.Value;
        var folder = VersionFolder(factory, version);

        Hold(folder);
        string? runFolder = null;
        try
        {
            if (await EnsureVersionFolderAsync(factory, version, folder, ct).ConfigureAwait(false) is { } downloadError)
                return Failed(downloadError);

            var checkFile = Path.Combine(folder, trigger.CheckPath!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(checkFile))
                return Failed($"the check {trigger.CheckPath} is not in version {version} of the definition of {factory}");

            var header = CheckScriptHeader.Read(await File.ReadAllTextAsync(checkFile, ct).ConfigureAwait(false));
            if (header.Error is not null) return Failed($"{trigger.CheckPath}: {header.Error}");

            var (checkLine, interpreterError) = CheckLine(header.DeclaresPackages, checkFile);
            if (interpreterError is not null) return Failed(interpreterError);

            var secrets = (trigger.Secrets ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal).ToList();
            var (commandLine, secretVars, secretError) = await WithSecretsAsync(secrets, checkLine!, ct).ConfigureAwait(false);
            if (secretError is not null) return Failed(secretError);

            runFolder = Path.Combine(_storageRoot, RunsFolder, Guid.NewGuid().ToString("N"));
            Hold(runFolder);
            Directory.CreateDirectory(runFolder);
            var stateFile = Path.Combine(runFolder, "state");
            await File.WriteAllTextAsync(stateFile, trigger.State ?? "", new UTF8Encoding(false), ct).ConfigureAwait(false);

            var environment = CheckEnvironment();
            environment[StateFileVariable] = stateFile;
            environment[SecretVarsVariable] = JsonSerializer.Serialize(secretVars);
            environment[FactoryVariable] = factory;
            environment[SeatVariable] = trigger.Seat ?? "";

            var result = await _processes.RunAsync(new CheckProcessStart(commandLine![0], commandLine.Skip(1).ToList(),
                folder, environment, trigger.TimeoutSeconds), ct).ConfigureAwait(false);
            if (result.StartError is not null) return Failed(result.StartError);

            var (state, oversize) = ReadState(stateFile);
            FileLog.Write($"[DefinitionCheckRunner] RunAsync: trigger={trigger.Name}, exit={result.ExitCode?.ToString() ?? "none"}, timedOut={result.TimedOut}, state={(oversize ? "oversize" : state is null ? "gone" : $"{state.Length} chars")}");
            return new TriggerCheckReport
            {
                CheckedAtUtc = _nowUtc(),
                ExitCode = result.TimedOut ? null : result.ExitCode,
                TimedOut = result.TimedOut,
                Output = Cap(result.Output),
                ErrorOutput = Cap(result.ErrorOutput),
                State = state,
                StateOversize = oversize,
            };
        }
        finally
        {
            if (runFolder is not null)
            {
                DeleteFolder(runFolder);
                Release(runFolder);
            }
            Release(folder);
        }
    }

    /// <summary>
    /// Delete every downloaded version that none of <paramref name="assigned"/> names, every half-written download,
    /// and every per-run folder a crash left behind - never one a running check is using now. Called after each
    /// fetch of this Director's triggers.
    /// </summary>
    public void Prune(IReadOnlyList<TriggerAssignmentDto> assigned)
    {
        ArgumentNullException.ThrowIfNull(assigned);
        try
        {
            PruneFolders(assigned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed now is pruned on a later fetch; the checks of this fetch still run.
            FileLog.Write($"[DefinitionCheckRunner] Prune FAILED: {ex.Message}");
        }
    }

    private void PruneFolders(IReadOnlyList<TriggerAssignmentDto> assigned)
    {
        var keep = new HashSet<string>(
            assigned.Where(t => t.IsDefinitionCheck && WhyNotRunnable(t) is null)
                .Select(t => Path.GetFullPath(VersionFolder(t.Factory!, t.DefinitionVersion!.Value))),
            StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        var checks = Path.Combine(_storageRoot, ChecksFolder);
        if (Directory.Exists(checks))
        {
            foreach (var factoryDir in Directory.GetDirectories(checks))
            {
                foreach (var versionDir in Directory.GetDirectories(factoryDir))
                {
                    var full = Path.GetFullPath(versionDir);
                    if (keep.Contains(full) || IsHeld(full) || IsHeld(PartialOwner(full))) continue;
                    DeleteFolder(full);
                    removed++;
                }
                if (Directory.GetFileSystemEntries(factoryDir).Length == 0) DeleteFolder(factoryDir);
            }
        }

        var runs = Path.Combine(_storageRoot, RunsFolder);
        if (Directory.Exists(runs))
        {
            foreach (var runDir in Directory.GetDirectories(runs))
            {
                if (IsHeld(Path.GetFullPath(runDir))) continue;
                DeleteFolder(runDir);
                removed++;
            }
        }
        if (removed > 0) FileLog.Write($"[DefinitionCheckRunner] Prune: removed {removed} folder(s), kept {keep.Count} version(s)");
    }

    // ---------- 2.2: the version folder ----------

    private async Task<string?> EnsureVersionFolderAsync(string factory, int version, string folder, CancellationToken ct)
    {
        var gate = _downloadLocks.GetOrAdd(Path.GetFullPath(folder), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(folder)) return null;

            var source = _source();
            if (source is null) return $"version {version} of the checks of {factory} could not be downloaded: no Gateway is configured";
            var fetch = await source.FetchFactoryChecksAsync(_directorId, factory, version, ct).ConfigureAwait(false);
            if (fetch.Files is null)
                return $"version {version} of the checks of {factory} could not be downloaded: {fetch.Refusal}";

            var partial = Path.Combine(Path.GetDirectoryName(folder)!, $"{PartialPrefix}{version}-{Guid.NewGuid():N}");
            try
            {
                foreach (var (path, text) in fetch.Files)
                {
                    if (WhyNotAFilePath(path) is { } why)
                        return $"version {version} of the checks of {factory} was not written: the Gateway sent the path '{path}', which {why}";
                    var target = Path.Combine(partial, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
                }
                Directory.CreateDirectory(partial);
                Directory.Move(partial, folder);
                partial = null;
                FileLog.Write($"[DefinitionCheckRunner] downloaded: factory={factory}, version={version}, files={fetch.Files.Count}, folder={folder}");
                return null;
            }
            finally
            {
                if (partial is not null) DeleteFolder(partial);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Why a path the Gateway sent may not be written, or null. Only <c>checks/</c> and <c>files/</c>, plain
    /// relative segments, nothing that climbs out.</summary>
    public static string? WhyNotAFilePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "is empty";
        if (!path.StartsWith("checks/", StringComparison.Ordinal) && !path.StartsWith("files/", StringComparison.Ordinal))
            return "is outside checks/ and files/";
        if (path.Contains('\\') || path.Contains(':')) return "is not a plain relative path";
        foreach (var segment in path.Split('/'))
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "is not a plain relative path";
        return null;
    }

    // ---------- 2.3: Python or uv ----------

    private (IReadOnlyList<string>? Line, string? Error) CheckLine(bool declaresPackages, string checkFile)
    {
        if (declaresPackages)
        {
            var uv = FindUv(_processes, _isWindows);
            return uv is null
                ? (null, "the check declares packages and uv is not on this machine")
                : (new[] { uv, "run", "--script", checkFile }, null);
        }
        var python = FindPython(_processes, _isWindows);
        return python is null
            ? (null, $"no Python on this machine (looked for {PythonName} on PATH)")
            : (new[] { python, checkFile }, null);
    }

    // ---------- 2.4: secrets ----------

    private async Task<(IReadOnlyList<string>? Line, Dictionary<string, string> Vars, string? Error)> WithSecretsAsync(
        IReadOnlyList<string> secrets, IReadOnlyList<string> checkLine, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (secrets.Count == 0) return (checkLine, vars, null);

        var tool = _processes.FindCcSecrets();
        if (tool is null) return (null, vars, "the check declares secrets and cc-secrets is not on this machine");

        var listed = await _processes.RunAsync(new CheckProcessStart(tool, new[] { "list", "--json" }, null, null,
            SecretsListTimeoutSeconds), ct).ConfigureAwait(false);
        if (listed.StartError is not null) return (null, vars, $"cc-secrets list --json could not run: {listed.StartError}");
        if (listed.TimedOut) return (null, vars, $"cc-secrets list --json did not answer within {SecretsListTimeoutSeconds} seconds");
        if (listed.ExitCode != 0)
            return (null, vars, $"cc-secrets list --json failed (exit {listed.ExitCode}): {FirstLine(listed.ErrorOutput, listed.Output)}");
        if (ReadEntries(listed.Output) is not { } entries)
            return (null, vars, "cc-secrets list --json answered something that is not its list of entries");

        foreach (var name in secrets)
        {
            if (!entries.TryGetValue(name, out var entry))
                return (null, vars, $"No secret named '{name}' is on this machine.");
            if (!entry.Uses.Contains("run", StringComparer.OrdinalIgnoreCase))
                return (null, vars, $"The secret '{name}' on this machine is not allowed for run (it is allowed for: {(entry.Uses.Count == 0 ? "nothing" : string.Join(", ", entry.Uses))}).");
            var variable = VariableFor(name, entry.EnvName);
            if (vars.FirstOrDefault(v => string.Equals(v.Value, variable, StringComparison.OrdinalIgnoreCase)) is { Key: not null } clash)
                return (null, vars, $"the secrets '{clash.Key}' and '{name}' would both arrive in {variable}, so the check could not tell them apart");
            vars[name] = variable;
        }

        var line = new List<string> { tool, "run", secrets[0] };
        if (secrets.Count == 1)
            line.AddRange(new[] { "--via", "env", "--env-name", vars[secrets[0]] });
        else
            foreach (var other in secrets.Skip(1)) line.AddRange(new[] { "--with", other });
        line.Add("--");
        line.AddRange(checkLine);
        return (line, vars, null);
    }

    private sealed record ListedEntry(string? EnvName, IReadOnlyList<string> Uses);

    private static Dictionary<string, ListedEntry>? ReadEntries(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var entries = new Dictionary<string, ListedEntry>(StringComparer.Ordinal);
            foreach (var e in list.EnumerateArray())
            {
                if (!e.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
                var envName = e.TryGetProperty("envName", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var uses = e.TryGetProperty("uses", out var u) && u.ValueKind == JsonValueKind.Array
                    ? u.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : new List<string>();
                entries[name.GetString()!] = new ListedEntry(envName, uses);
            }
            return entries;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[DefinitionCheckRunner] ReadEntries FAILED: cc-secrets list --json was not JSON: {ex.Message}");
            return null;
        }
    }

    // ---------- 2.5: the state file ----------

    private static (string? State, bool Oversize) ReadState(string stateFile)
    {
        var info = new FileInfo(stateFile);
        if (!info.Exists) return (null, false);
        if (info.Length > TriggerCheckLimits.StateCapBytes) return (null, true);
        return (File.ReadAllText(stateFile, Encoding.UTF8), false);
    }

    // ---------- helpers ----------

    /// <summary>The base environment with every <see cref="DirectorVariablePrefix"/> variable removed.</summary>
    private Dictionary<string, string> CheckEnvironment()
    {
        var environment = _baseEnvironment();
        var comparison = _isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var name in environment.Keys.Where(k => k.StartsWith(DirectorVariablePrefix, comparison)).ToList())
            environment.Remove(name);
        return environment;
    }

    private string? WhyNotRunnable(TriggerAssignmentDto trigger)
    {
        if (!IsPlainName(trigger.Factory)) return $"the Gateway handed a check with the factory '{trigger.Factory}', which is not a factory id";
        if (trigger.DefinitionVersion is not > 0) return $"the Gateway handed a check of {trigger.Factory} with no published version";
        if (string.IsNullOrEmpty(trigger.CheckPath) || !trigger.CheckPath.StartsWith("checks/", StringComparison.Ordinal)
            || WhyNotAFilePath(trigger.CheckPath) is not null)
            return $"the Gateway handed a check of {trigger.Factory} with the path '{trigger.CheckPath}', which is not a file in checks/";
        return null;
    }

    private static bool IsPlainName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && !name.StartsWith(PartialPrefix, StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\');

    private TriggerCheckReport Failed(string reason)
    {
        FileLog.Write($"[DefinitionCheckRunner] check could not start: {reason}");
        return new TriggerCheckReport { CheckedAtUtc = _nowUtc(), StartError = reason };
    }

    private static string Cap(string? text)
    {
        var value = text ?? "";
        return value.Length <= TriggerCheckLimits.OutputCap ? value : value[..TriggerCheckLimits.OutputCap];
    }

    private static string FirstLine(params string[] texts)
    {
        var line = texts.SelectMany(t => (t ?? "").Split('\n')).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? "(it printed nothing)" : line.Length > 300 ? line[..300] : line;
    }

    /// <summary>A half-written download beside a version folder belongs to that version while its download runs.</summary>
    private static string PartialOwner(string dir)
    {
        var name = Path.GetFileName(dir);
        if (!name.StartsWith(PartialPrefix, StringComparison.Ordinal)) return dir;
        var version = name[PartialPrefix.Length..].Split('-')[0];
        return Path.Combine(Path.GetDirectoryName(dir)!, version);
    }

    private void Hold(string folder)
    {
        var key = Path.GetFullPath(folder);
        lock (_inUseGate) _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
    }

    private void Release(string folder)
    {
        var key = Path.GetFullPath(folder);
        lock (_inUseGate)
        {
            if (_inUse.GetValueOrDefault(key) <= 1) _inUse.Remove(key);
            else _inUse[key]--;
        }
    }

    private bool IsHeld(string folder)
    {
        var key = Path.GetFullPath(folder);
        lock (_inUseGate) return _inUse.ContainsKey(key);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder some other program holds open is left for the next prune, which tries again.
            FileLog.Write($"[DefinitionCheckRunner] DeleteFolder FAILED: could not delete {folder}: {ex.Message}");
        }
    }
}
