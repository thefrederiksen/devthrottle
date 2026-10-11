using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// The License Switching mission: everything the Director needs from the key store, which lives ONCE, in the
/// Python command line (<c>cc-devthrottle license ... --json</c>). The code that moves a sign-in key exists in one
/// place; the Director never reads, copies or exchanges a key itself - it runs that command and reads its answer.
/// A seam, so every Director test runs against a fake and never against a real sign-in.
/// </summary>
public interface ILicenseCommandLine
{
    /// <summary>Every holding this machine has, with no provider read and no numbers (<c>license list</c>).
    /// Throws <see cref="LicenseCommandLineException"/> when the tool did not give a readable answer.</summary>
    Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads usage from the provider (<c>license usage</c>) for the active holding only, or for every
    /// holding. A read of every holding first renews each spare whose key is due (<c>--refresh</c>): nothing else uses
    /// an unused spare's key, so without it the key expires and the spare stops being a switch target. The active
    /// key is never renewed here - the sessions using it keep it fresh.
    /// A failed read of one holding is that row's <see cref="LicenseHolding.ReadError"/>, not an exception.
    /// Throws <see cref="LicenseCommandLineException"/> when the tool did not give a readable answer.</summary>
    Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken);

    /// <summary>Exchanges the active sign-in with the spare for <paramref name="targetAccountEmail"/>
    /// (<c>license switch</c>), refusing when the active account is not <paramref name="expectedActiveAccountEmail"/>.
    /// Never throws for an answer the tool gave; an answer it could not give is
    /// <see cref="LicenseSwitchReasonCodes.ExchangeFailedUnknown"/>, because the Director cannot know what happened.</summary>
    Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken);
}

/// <summary>What <c>license switch --json</c> answered. Its words are the contract's (section 3.3).</summary>
public sealed class LicenseExchangeAnswer
{
    /// <summary>One of <see cref="LicenseSwitchOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>One of <see cref="LicenseSwitchReasonCodes"/> when <see cref="Outcome"/> is "failed".</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The tool's own plain sentence. It never quotes a key.</summary>
    public string? Message { get; set; }

    public string PreviousActiveAccountEmail { get; set; } = "";

    /// <summary>The active account the tool READ BACK after the exchange.</summary>
    public string ActiveAccountEmailAfter { get; set; } = "";

    public int ExchangeMilliseconds { get; set; }
}

/// <summary>The license command line could not be run, or did not answer with what it promises.</summary>
public sealed class LicenseCommandLineException : Exception
{
    public LicenseCommandLineException(string message) : base(message) { }
}

/// <summary>Where the license tool reads and writes, when it must not be the machine's real ones. Tests and every
/// phase 1 run set both; the tool itself refuses the machine's real default Claude login in phase 1.</summary>
public sealed record LicenseCommandLineOptions(string? DefaultConfigDir, string? StoreDir);

/// <summary>
/// The production <see cref="ILicenseCommandLine"/>: this Director's OWN <c>cc-devthrottle</c>, from the machine's
/// installed Python environment - never whatever PATH resolves. PATH can hold another install's copy (the stale-install
/// fault <see cref="Setup.FleetToolReachability"/> exists to catch), and the program that exchanges a sign-in is not
/// one to take from shared state we do not control. Absent means "not installed": the call fails and says so.
///
/// Nothing the tool prints is ever logged: the log lines name the subcommand, the exit code and the row count.
/// </summary>
public sealed class CcDevthrottleLicenseCommandLine : ILicenseCommandLine
{
    public const string ToolName = "cc-devthrottle";

    /// <summary>How long one run may take. A usage read of every holding is the slowest: one provider call each.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The contract's read errors (section 2.2). A value outside it is never carried: it could be anything
    /// the tool printed, a provider's answer included (rule 6.1).</summary>
    private static readonly HashSet<string> KnownReadErrors = new(StringComparer.Ordinal)
    {
        LicenseReadErrors.Unauthorized, LicenseReadErrors.RefreshFailed, LicenseReadErrors.RateLimited,
        LicenseReadErrors.Network, LicenseReadErrors.UnexpectedAnswer, LicenseReadErrors.StoreUnreadable,
    };

    /// <summary>The contract's switch reason codes (section 3.4), for the same reason.</summary>
    private static readonly HashSet<string> KnownReasonCodes = new(StringComparer.Ordinal)
    {
        LicenseSwitchReasonCodes.NotKeyHolder, LicenseSwitchReasonCodes.SwitchInProgress, LicenseSwitchReasonCodes.Expired,
        LicenseSwitchReasonCodes.ActiveAccountChanged, LicenseSwitchReasonCodes.SpareNotHeld, LicenseSwitchReasonCodes.SpareNotUsable,
        LicenseSwitchReasonCodes.ExchangeFailedRestored, LicenseSwitchReasonCodes.ExchangeFailedUnknown, LicenseSwitchReasonCodes.VerifyFailed,
    };

    private readonly LicenseCommandLineOptions _options;
    private readonly Func<string?> _resolveTool;

    public CcDevthrottleLicenseCommandLine(LicenseCommandLineOptions options)
        : this(options, ResolveOwnCopy)
    {
    }

    /// <param name="resolveTool">Test seam: where the tool is, or null when it is not installed.</param>
    internal CcDevthrottleLicenseCommandLine(LicenseCommandLineOptions options, Func<string?> resolveTool)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolveTool = resolveTool ?? throw new ArgumentNullException(nameof(resolveTool));
    }

    /// <summary>
    /// This Director's own copy: the console script in the installed Python environment
    /// (<c>&lt;machine root&gt;/pyenv/Scripts</c> on Windows, <c>&lt;machine root&gt;/pyenv/bin</c> on macOS and Linux). That
    /// is the program itself on every platform - the Windows <c>bin\cc-devthrottle.cmd</c> shim and the macOS
    /// <c>~/.local/bin/cc-devthrottle</c> symlink both point at it, and on macOS nothing is put in the machine's
    /// <c>bin</c> folder at all. Null when it is not there.
    /// </summary>
    public static string? ResolveOwnCopy() => ResolveOwnCopy(OwnToolDirectory());

    /// <summary>The installed Python environment's script folder, where the installer puts every Python tool.</summary>
    public static string OwnToolDirectory() =>
        Path.Combine(CcStorage.MachineRoot(), "pyenv", OperatingSystem.IsWindows() ? "Scripts" : "bin");

    /// <summary>The tool in <paramref name="toolDirectory"/>, or null - PATH is never searched.</summary>
    internal static string? ResolveOwnCopy(string toolDirectory) => ExecutableResolver.Resolve(Path.Combine(toolDirectory, ToolName));

    public async Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken)
    {
        var output = await RunForJsonAsync(new[] { "license", "list" }, cancellationToken);
        return ReadListedAccounts(output);
    }

    public async Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        var args = new List<string> { "license", "usage" };
        args.Add(activeOnly ? "--active" : "--refresh");
        var output = await RunForJsonAsync(args, cancellationToken);
        return ReadUsageRows(output);
    }

    public async Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken)
    {
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] SwitchAsync: target={targetAccountEmail}, expected={expectedActiveAccountEmail}");
        string output;
        try
        {
            output = await RunForJsonAsync(new[] { "license", "switch", targetAccountEmail, "--expect-active", expectedActiveAccountEmail }, cancellationToken);
        }
        catch (LicenseCommandLineException ex)
        {
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] SwitchAsync FAILED, the exchange's state is unknown: {ex.Message}");
            return Unknown("The license tool gave no readable answer to the switch, so what it did is not known.");
        }

        var answer = ReadSwitchAnswer(output);
        if (answer is null)
        {
            FileLog.Write("[CcDevthrottleLicenseCommandLine] SwitchAsync FAILED: the answer was not a switch result, the exchange's state is unknown");
            return Unknown("The license tool's answer to the switch could not be read, so what it did is not known.");
        }
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] SwitchAsync: outcome={answer.Outcome}, reason={answer.ReasonCode ?? "-"}, after={answer.ActiveAccountEmailAfter}");
        return answer;
    }

    private static LicenseExchangeAnswer Unknown(string message) => new()
    {
        Outcome = LicenseSwitchOutcomes.Failed,
        ReasonCode = LicenseSwitchReasonCodes.ExchangeFailedUnknown,
        Message = message,
    };

    /// <summary>
    /// Runs the tool with <c>--json</c> and the isolation options, and returns standard output. A refusal from the tool
    /// is a JSON answer with a non-zero exit, so the exit code alone decides nothing; the caller reads the JSON.
    /// </summary>
    private async Task<string> RunForJsonAsync(IReadOnlyList<string> subcommand, CancellationToken cancellationToken)
    {
        var exe = _resolveTool()
            ?? throw new LicenseCommandLineException(
                $"{ToolName} is not installed in this machine's Python tool environment ({OwnToolDirectory()}). Reinstall DevThrottle to put it back.");

        var args = new List<string>(subcommand) { "--json" };
        if (!string.IsNullOrWhiteSpace(_options.DefaultConfigDir)) { args.Add("--default-config-dir"); args.Add(_options.DefaultConfigDir); }
        if (!string.IsNullOrWhiteSpace(_options.StoreDir)) { args.Add("--store-dir"); args.Add(_options.StoreDir); }

        ProcessRunner.Result result;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(CallTimeout);
            try
            {
                result = await ProcessRunner.RunAsync(exe, args, workingDirectory: null, standardInput: null, limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LicenseCommandLineException($"{ToolName} license {subcommand[1]} did not finish within {CallTimeout.TotalSeconds:0} seconds.");
            }
        }

        if (!result.Started)
            throw new LicenseCommandLineException($"{ToolName} could not be started (error {result.StartErrorCode}).");

        FileLog.Write($"[CcDevthrottleLicenseCommandLine] license {subcommand[1]}: exit={result.ExitCode}");
        return result.StandardOutput;
    }

    /// <summary>The rows of a <c>usage</c> answer: one JSON object whose <c>accounts</c> array holds rows in the contract's
    /// <see cref="LicenseHolding"/> shape (camel case; extra fields are ignored). Anything else throws, saying which
    /// command it was and never quoting the answer.</summary>
    internal static IReadOnlyList<LicenseHolding> ReadUsageRows(string? output)
    {
        using var document = ParseAccounts(output, "usage", out var rows);
        var holdings = rows.Deserialize<List<LicenseHolding>>(JsonOptions) ?? new List<LicenseHolding>();
        foreach (var h in holdings)
        {
            h.AccountEmail = Normalize(h.AccountEmail, "usage");
            // The tool's own words never travel (rule 6.1): the Director writes every detail itself.
            h.ReadErrorDetail = null;
            if (h.ReadError is not null && !KnownReadErrors.Contains(h.ReadError))
            {
                // The value is not logged either: it is not one of the contract's words, so it could be anything.
                FileLog.Write($"[CcDevthrottleLicenseCommandLine] ReadUsageRows: account={h.AccountEmail} carried a read error outside the contract; read as '{LicenseReadErrors.UnexpectedAnswer}'");
                h.ReadError = LicenseReadErrors.UnexpectedAnswer;
            }
        }
        return holdings;
    }

    /// <summary>The rows of a <c>list</c> answer: one JSON object whose <c>accounts</c> array holds
    /// <c>{email, role}</c> rows, role "active" or "spare". No provider is read, so the rows carry no numbers.</summary>
    internal static IReadOnlyList<LicenseHolding> ReadListedAccounts(string? output)
    {
        using var document = ParseAccounts(output, "list", out var rows);
        var holdings = new List<LicenseHolding>();
        foreach (var row in rows.EnumerateArray())
        {
            var email = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            var role = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            if (role is not ("active" or "spare"))
                throw new LicenseCommandLineException($"{ToolName} license list answered an account whose role is neither active nor spare.");
            holdings.Add(new LicenseHolding { AccountEmail = Normalize(email, "list"), IsActive = role == "active" });
        }
        return holdings;
    }

    private static JsonDocument ParseAccounts(string? output, string subcommand, out JsonElement rows)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new LicenseCommandLineException($"{ToolName} license {subcommand} printed nothing.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            throw new LicenseCommandLineException($"{ToolName} license {subcommand} answered something that is not JSON.");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("accounts", out rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new LicenseCommandLineException($"{ToolName} license {subcommand} did not answer with an accounts list.");
        }
        return document;
    }

    private static string Normalize(string? email, string subcommand)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            throw new LicenseCommandLineException($"{ToolName} license {subcommand} answered an account with no email.");
        return normalized;
    }

    /// <summary>The switch answer, or null when the output is not one JSON object carrying a known outcome.</summary>
    internal static LicenseExchangeAnswer? ReadSwitchAnswer(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            var answer = JsonSerializer.Deserialize<LicenseExchangeAnswer>(output, JsonOptions);
            if (answer is null) return null;
            if (answer.Outcome is not (LicenseSwitchOutcomes.Switched or LicenseSwitchOutcomes.AlreadyActive or LicenseSwitchOutcomes.Failed))
                return null;
            if (answer.ReasonCode is not null && !KnownReasonCodes.Contains(answer.ReasonCode))
            {
                // A failure for a reason the contract has no word for: what the exchange did is not known. The value
                // is not logged or carried - it could be anything the tool printed (rule 6.1).
                FileLog.Write($"[CcDevthrottleLicenseCommandLine] ReadSwitchAnswer: the answer carried a reason outside the contract; read as '{LicenseSwitchReasonCodes.ExchangeFailedUnknown}'");
                answer.Outcome = LicenseSwitchOutcomes.Failed;
                answer.ReasonCode = LicenseSwitchReasonCodes.ExchangeFailedUnknown;
            }
            answer.PreviousActiveAccountEmail = (answer.PreviousActiveAccountEmail ?? "").Trim().ToLowerInvariant();
            answer.ActiveAccountEmailAfter = (answer.ActiveAccountEmailAfter ?? "").Trim().ToLowerInvariant();
            return answer;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
