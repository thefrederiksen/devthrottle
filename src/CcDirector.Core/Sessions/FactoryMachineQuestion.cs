using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// THE MACHINE QUESTION, answered for THIS machine (Factory Definition, phase 5, design section 6). Given the secret
/// names and repository names of a defined factory's seats, it says for each secret whether a cc-secrets entry of
/// that name exists here and is allowed for <c>run</c>, and for each repository whether it is in this Director's
/// repository registry and where.
///
/// THE SECRETS ARE READ FROM <c>cc-secrets list --json</c>, which prints names, kinds and uses - never a secret. A
/// setting's entry does carry its (non-secret) value in that output; it is never read here, and the output is never
/// logged or returned: only the name, held and allowed flags leave this class. The words for "not allowed for run"
/// are cc-secrets' own, as <c>cc-secrets run</c> says them (tools/cc-secrets/src/store.py).
///
/// A cc-secrets that is missing, fails, does not finish in time or answers in a shape this class cannot read is an
/// answer that SAYS SO (<see cref="FactoryMachineAnswer.SecretsUnavailable"/>), never "not held": a broken tool must
/// not read as an empty store.
///
/// The publish asks it through the <c>factory-machine-question</c> verb; the seat's start (phase 5, pull request P4)
/// asks it before the agent starts; phase 5b adds the Python and uv answers.
/// </summary>
public sealed class FactoryMachineQuestion
{
    /// <summary>The installed tool's name, found the way every cc tool is.</summary>
    public const string CcSecretsToolName = "cc-secrets";

    /// <summary>
    /// How long <c>cc-secrets list --json</c> may take. It reads one local file, so a healthy run takes about a second
    /// (a Python start). The Gateway's wait for the whole answer is longer than this, so this one fires first and the
    /// answer can say it was cc-secrets that did not finish.
    /// </summary>
    public static readonly TimeSpan DefaultListTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs <c>cc-secrets list --json</c>. A seam, so tests never start a real process.</summary>
    public delegate Task<ProcessRunner.Result> ListSecretsAsync(CancellationToken cancellationToken);

    private readonly ListSecretsAsync _listSecrets;
    private readonly TimeSpan _listTimeout;

    /// <param name="listSecrets">What runs <c>cc-secrets list --json</c>.</param>
    /// <param name="listTimeout">The limit on that run.</param>
    public FactoryMachineQuestion(ListSecretsAsync listSecrets, TimeSpan listTimeout)
    {
        _listSecrets = listSecrets ?? throw new ArgumentNullException(nameof(listSecrets));
        _listTimeout = listTimeout;
    }

    /// <summary>The question as this machine answers it: the installed cc-secrets, with <see cref="DefaultListTimeout"/>.</summary>
    public static FactoryMachineQuestion ForThisMachine() => new(RunInstalledCcSecretsListAsync, DefaultListTimeout);

    /// <summary>
    /// Answer for this machine. <paramref name="registered"/> is this Director's repository registry as it stands now.
    /// cc-secrets is run only when at least one secret is asked about.
    /// </summary>
    public async Task<FactoryMachineAnswer> AnswerAsync(string machine, IReadOnlyList<string> secrets,
        IReadOnlyList<string> repositories, IReadOnlyList<RepositoryConfig> registered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(registered);
        FileLog.Write($"[FactoryMachineQuestion] AnswerAsync: machine={machine}, secrets={secrets.Count}, repositories={repositories.Count}");

        var answer = new FactoryMachineAnswer { Machine = machine };
        foreach (var name in repositories)
        {
            var found = registered.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            answer.Repositories.Add(new FactoryRepositoryAnswer { Name = name, Registered = found is not null, Path = found?.Path });
        }

        if (secrets.Count > 0)
        {
            var listed = await ListAsync(machine, cancellationToken);
            if (listed.Unavailable is not null)
            {
                answer.SecretsUnavailable = listed.Unavailable;
            }
            else
            {
                foreach (var name in secrets)
                    answer.Secrets.Add(SecretAnswer(name, listed.Uses!));
            }
        }

        FileLog.Write($"[FactoryMachineQuestion] AnswerAsync: machine={machine}, secretsUnavailable={answer.SecretsUnavailable is not null}, " +
                      $"held={answer.Secrets.Count(s => s.Held)}, allowedForRun={answer.Secrets.Count(s => s.AllowedForRun)}, " +
                      $"registered={answer.Repositories.Count(r => r.Registered)}/{answer.Repositories.Count}");
        return answer;
    }

    private static FactorySecretAnswer SecretAnswer(string name, IReadOnlyDictionary<string, IReadOnlyList<string>> uses)
    {
        if (!uses.TryGetValue(name, out var entryUses))
            return new FactorySecretAnswer { Name = name, Held = false, AllowedForRun = false };
        var allowed = entryUses.Contains("run", StringComparer.Ordinal);
        return new FactorySecretAnswer
        {
            Name = name,
            Held = true,
            AllowedForRun = allowed,
            // cc-secrets' own words, exactly as `cc-secrets run` refuses (tools/cc-secrets/src/store.py).
            NotAllowedReason = allowed ? null : $"Secret '{name}' is not allowed for 'run'. It may be used for: {string.Join(", ", entryUses)}.",
        };
    }

    private readonly record struct Listed(IReadOnlyDictionary<string, IReadOnlyList<string>>? Uses, string? Unavailable);

    // Runs the list and reads ONLY each entry's name and uses. The output is never logged: a setting's value is in it.
    private async Task<Listed> ListAsync(string machine, CancellationToken cancellationToken)
    {
        ProcessRunner.Result result;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(_listTimeout);
            try
            {
                result = await _listSecrets(limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FileLog.Write($"[FactoryMachineQuestion] ListAsync FAILED: cc-secrets list timed out after {Seconds(_listTimeout)}s");
                return new Listed(null, $"cc-secrets on {machine} did not answer within {Seconds(_listTimeout)} seconds.");
            }
        }

        if (!result.Started)
        {
            FileLog.Write("[FactoryMachineQuestion] ListAsync FAILED: cc-secrets could not be started");
            return new Listed(null, $"cc-secrets is not installed on {machine}, or could not be started there.");
        }
        if (result.ExitCode != 0)
        {
            FileLog.Write($"[FactoryMachineQuestion] ListAsync FAILED: cc-secrets list exit={result.ExitCode}");
            return new Listed(null, $"cc-secrets list failed on {machine} (exit {result.ExitCode}).");
        }

        var uses = ReadUses(result.StandardOutput);
        if (uses is null)
        {
            FileLog.Write("[FactoryMachineQuestion] ListAsync FAILED: cc-secrets list answered in a shape that cannot be read");
            return new Listed(null, $"cc-secrets on {machine} answered in a shape the Director does not read; update cc-secrets there.");
        }
        FileLog.Write($"[FactoryMachineQuestion] ListAsync: entries={uses.Count}");
        return new Listed(uses, null);
    }

    /// <summary>
    /// Each entry's name and uses from <c>cc-secrets list --json</c> (<c>{"entries": [{"name", "uses": [...], ...}]}</c>),
    /// or null when the output is not that shape. Nothing else in an entry is read.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadUses(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return null;

            var uses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("uses", out var entryUses) || entryUses.ValueKind != JsonValueKind.Array)
                    return null;
                var list = new List<string>();
                foreach (var use in entryUses.EnumerateArray())
                {
                    if (use.ValueKind != JsonValueKind.String) return null;
                    list.Add(use.GetString()!);
                }
                uses[name.GetString()!] = list;
            }
            return uses;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<ProcessRunner.Result> RunInstalledCcSecretsListAsync(CancellationToken cancellationToken)
    {
        // No explicit-path override, as for the secret transfer: a variable that could point the Director at another
        // program is not honoured for the tool that holds the machine's secrets.
        var exe = CcToolExecutable.Resolve(CcSecretsToolName, explicitPathEnvVar: null);
        if (exe is null)
            return Task.FromResult(new ProcessRunner.Result(-1, "", "", Started: false));
        return ProcessRunner.RunAsync(exe, new[] { "list", "--json" }, workingDirectory: null, cancellationToken);
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
