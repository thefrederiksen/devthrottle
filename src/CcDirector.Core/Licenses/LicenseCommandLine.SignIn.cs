using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>One held account's sign-in in a renewal run: first what <c>license renew --check</c> said of it, then -
/// for one that was due - what the Director's sign-in did.</summary>
public sealed class LicenseRenewalRow
{
    public string AccountEmail { get; set; } = "";

    public bool IsActive { get; set; }

    /// <summary>One of <see cref="LicenseRenewalResults"/>.</summary>
    public string Result { get; set; } = "";

    /// <summary>A <see cref="LicenseRenewalErrors"/> code when <see cref="Result"/> is "failed", else null.</summary>
    public string? Error { get; set; }

    /// <summary>For a row that is due: whether a browser profile is set for the account on this machine. Never the
    /// profile's name, which is the owner's and stays in the tool.</summary>
    public bool HasBrowserProfileHere { get; set; }

    public DateTime? SignedInAtUtc { get; set; }

    public DateTime? SignInExpiresAtUtc { get; set; }
}

/// <summary>What a renewal did to one account, in the Director's words.</summary>
public static class LicenseRenewalResults
{
    /// <summary>The sign-in ends within five days; the Director signs the account in again.</summary>
    public const string Due = "due";

    /// <summary>It was due and the Director's sign-in renewed it.</summary>
    public const string Renewed = "renewed";

    public const string NotDue = "not-due";

    /// <summary>The sign-in carries no end date, so the tool cannot tell whether it is due.</summary>
    public const string UnknownExpiry = "unknown-expiry";

    public const string Failed = "failed";
}

/// <summary>What <c>license sign-in --json</c> answered.</summary>
public sealed class LicenseSignInAnswer
{
    /// <summary>One of <see cref="LicenseSignInOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>A <see cref="LicenseRenewalErrors"/> code when <see cref="Outcome"/> is "failed", else null.</summary>
    public string? ReasonCode { get; set; }

    public DateTime? SignedInAtUtc { get; set; }

    public DateTime? SignInExpiresAtUtc { get; set; }

    /// <summary>A sentence this Director wrote for this one answer, said instead of the reason's own; null for the
    /// reason's own. Never the tool's words.</summary>
    public string? Sentence { get; init; }

    public bool Succeeded => Outcome is LicenseSignInOutcomes.Added or LicenseSignInOutcomes.Renewed;

    public static LicenseSignInAnswer Failed(string reasonCode) => new() { Outcome = LicenseSignInOutcomes.Failed, ReasonCode = reasonCode };
}

/// <summary>What <c>license check-approver --json</c> answered. Only its words: the tool's own sentence is never
/// carried, because it can quote what a browser step printed.</summary>
public sealed class LicenseApproverToolAnswer
{
    /// <summary>One of <see cref="LicenseApproverStates.Reportable"/>.</summary>
    public string State { get; set; } = LicenseApproverStates.CheckFailed;

    /// <summary>The account checked; null when the profile showed none.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Who the page said is signed in; null when it named nobody.</summary>
    public string? ShownAccount { get; set; }

    /// <summary>False when the tool could not be run or gave no readable answer, as opposed to answering
    /// <see cref="LicenseApproverStates.CheckFailed"/> itself.</summary>
    public bool ToolAnswered { get; set; }

    public static LicenseApproverToolAnswer NoAnswer(string? accountEmail) =>
        new() { State = LicenseApproverStates.CheckFailed, AccountEmail = accountEmail, ToolAnswered = false };
}

/// <summary>What <c>license approve --json</c> answered. <see cref="Approval"/> is the authorize answer the waiting
/// login needs: it is never logged, and it leaves this process only sealed to the waiting machine.</summary>
public sealed class LicenseApproveAnswer
{
    public bool Approved { get; set; }

    /// <summary>A <see cref="LicenseRenewalErrors"/> code when refused, else null.</summary>
    public string? ReasonCode { get; set; }

    public string? Approval { get; set; }

    public static LicenseApproveAnswer Refused(string reasonCode) => new() { Approved = false, ReasonCode = reasonCode };
}

/// <summary>
/// A sign-in that is waiting for its approval from another machine: <c>license sign-in --remote-approval</c> has
/// printed its authorize URL and is reading its standard input. Exactly one of <see cref="CompleteAsync"/> and
/// <see cref="AbandonAsync"/> ends it; disposing one that was never ended abandons it.
/// </summary>
public interface ILicenseRemoteSignIn : IAsyncDisposable
{
    /// <summary>What the sign-in printed. It names the account and carries a challenge, never the verifier.</summary>
    string AuthorizeUrl { get; }

    /// <summary>Types the approval into the waiting login and answers what the tool then did.</summary>
    Task<LicenseSignInAnswer> CompleteAsync(string approval, CancellationToken cancellationToken);

    /// <summary>Ends the waiting login with no approval: the tool removes a new spare's empty folder and never
    /// touches a folder that already holds a sign-in.</summary>
    Task AbandonAsync(CancellationToken cancellationToken);
}

/// <summary>The start of a sign-in: a login waiting for another machine's approval, or one that already finished.</summary>
public sealed class LicenseSignInStart
{
    /// <summary>Set when the tool printed an authorize URL and is waiting for the approval on its input.</summary>
    public ILicenseRemoteSignIn? Waiting { get; init; }

    /// <summary>Set when the tool ended without waiting: it approved in a browser profile here and signed in, or it failed.</summary>
    public LicenseSignInAnswer? Finished { get; init; }
}

public sealed partial class CcDevthrottleLicenseCommandLine
{
    /// <summary>How long one approval in a local browser profile may take - and so how long a sign-in may take to
    /// either print its authorize URL or finish by approving here.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(4);

    /// <summary>How long the tool may take to finish once its input has ended - the approval typed in, or nothing.</summary>
    public static readonly TimeSpan FinishTimeout = LicenseContract.SignInFinishAllowance;

    internal const string AuthorizeUrlPrefix = "authorize-url: ";

    private const string FailedPrefix = "failed-";

    public async Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken)
    {
        FileLog.Write("[CcDevthrottleLicenseCommandLine] CheckRenewalsAsync");
        var output = await RunForJsonAsync(new[] { "license", "renew", "--check" }, cancellationToken);
        var rows = ReadRenewalRows(output);
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] CheckRenewalsAsync: rows={rows.Count}, due={rows.Count(r => r.Result == LicenseRenewalResults.Due)}, failed={rows.Count(r => r.Result == LicenseRenewalResults.Failed)}");
        return rows;
    }

    public async Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken)
    {
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] HasBrowserProfileAsync: account={accountEmail}");
        try
        {
            var output = await RunForJsonAsync(new[] { "license", "get-browser", accountEmail }, cancellationToken);
            var answer = ReadHasBrowserProfile(output);
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] HasBrowserProfileAsync: account={accountEmail}, profile set here={answer}");
            return answer;
        }
        catch (LicenseCommandLineException ex)
        {
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] HasBrowserProfileAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>The <c>get-browser --json</c> answer: <c>{accountEmail, browser}</c>, where browser is a name or null.</summary>
    internal static bool ReadHasBrowserProfile(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new LicenseCommandLineException($"{ToolName} license get-browser printed nothing.");
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("browser", out var browser))
                throw new LicenseCommandLineException($"{ToolName} license get-browser answered no \"browser\" field.");
            return browser.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(browser.GetString());
        }
        catch (JsonException)
        {
            throw new LicenseCommandLineException($"{ToolName} license get-browser did not answer JSON.");
        }
    }

    public async Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken)
    {
        // The URL is not logged: it names the account in its query and is of no use to a reader of the log.
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] ApproveAsync: account={accountEmail}, browser profile given={!string.IsNullOrWhiteSpace(browserProfile)}");
        string output;
        try
        {
            output = await RunForJsonAsync(
                new[] { "license", "approve", authorizeUrl, "--email", accountEmail, "--browser", browserProfile },
                cancellationToken, LicenseContract.ApproveToolTimeout, isolated: false);
        }
        catch (LicenseCommandLineException ex)
        {
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] ApproveAsync FAILED: {ex.Message}");
            return LicenseApproveAnswer.Refused(LicenseRenewalErrors.ApprovalFailed);
        }
        var answer = ReadApproveAnswer(output);
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] ApproveAsync: account={accountEmail}, approved={answer.Approved}, reason={answer.ReasonCode ?? "-"}");
        return answer;
    }

    public async Task<LicenseApproverToolAnswer> CheckApproverAsync(string browserProfile, string? accountEmail, CancellationToken cancellationToken)
    {
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] CheckApproverAsync: profile={browserProfile}, account={accountEmail ?? "(the one signed in)"}");
        var args = new List<string> { "license", "check-approver", "--browser", browserProfile };
        if (accountEmail is not null) { args.Add("--email"); args.Add(accountEmail); }
        string output;
        try
        {
            // check-approver touches no key store, so it takes no folder options.
            output = await RunForJsonAsync(args, cancellationToken, ApproverCheckToolTimeout, isolated: false);
        }
        catch (LicenseCommandLineException ex)
        {
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] CheckApproverAsync FAILED: {ex.Message}");
            return LicenseApproverToolAnswer.NoAnswer(accountEmail);
        }
        var answer = ReadApproverCheck(output, accountEmail);
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] CheckApproverAsync: profile={browserProfile}, account={answer.AccountEmail ?? "-"}, state={answer.State}, answered={answer.ToolAnswered}");
        return answer;
    }

    /// <summary>How long one <c>license check-approver</c> may take: reading the signed-in account, starting the
    /// throwaway login, and reading the approval page, each with its own wait in the tool (about six and a half
    /// minutes in all), plus room to clean up.</summary>
    public static readonly TimeSpan ApproverCheckToolTimeout = TimeSpan.FromMinutes(8);

    /// <summary>The <c>check-approver --json</c> answer: <c>{browserProfile, browserProfileName, accountEmail,
    /// shownAccount, state, detail}</c>. A state outside the contract, or a "can approve" that names no account or
    /// another one, is no answer: the check failed.</summary>
    internal static LicenseApproverToolAnswer ReadApproverCheck(string? output, string? askedFor)
    {
        using var document = FirstObject(output);
        if (document is null) return LicenseApproverToolAnswer.NoAnswer(askedFor);
        var root = document.RootElement;
        var state = Text(root, "state");
        var account = Text(root, "accountEmail")?.Trim().ToLowerInvariant();
        var shown = Text(root, "shownAccount")?.Trim().ToLowerInvariant();
        if (state is null || !LicenseApproverStates.Reportable.Contains(state))
        {
            FileLog.Write("[CcDevthrottleLicenseCommandLine] license check-approver answered a state outside the contract; read as no answer");
            return LicenseApproverToolAnswer.NoAnswer(askedFor);
        }
        if (askedFor is not null && account is not null && account != askedFor.Trim().ToLowerInvariant())
        {
            FileLog.Write("[CcDevthrottleLicenseCommandLine] license check-approver answered for another account than it was asked; read as no answer");
            return LicenseApproverToolAnswer.NoAnswer(askedFor);
        }
        if (state == LicenseApproverStates.CanApprove && (account is null || (shown is not null && shown != account)))
        {
            FileLog.Write("[CcDevthrottleLicenseCommandLine] license check-approver said can-approve without naming that account; read as no answer");
            return LicenseApproverToolAnswer.NoAnswer(askedFor);
        }
        return new LicenseApproverToolAnswer { State = state, AccountEmail = account ?? askedFor, ShownAccount = shown, ToolAnswered = true };
    }

    public async Task<LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken)
    {
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] StartSignInAsync: account={accountEmail}, askAnotherMachineWhenNoBrowser={askAnotherMachineWhenNoBrowser}");
        var exe = _resolveTool();
        if (exe is null)
        {
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] StartSignInAsync FAILED: {ToolName} is not installed in {OwnToolDirectory()}");
            return new LicenseSignInStart { Finished = LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed) };
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        var args = new List<string> { "license", "sign-in", accountEmail };
        if (askAnotherMachineWhenNoBrowser) args.Add("--ask-when-no-browser");
        args.Add("--json");
        foreach (var a in WithIsolation(args))
            psi.ArgumentList.Add(a);
        foreach (var (name, value) in _options.ToolEnvironment())
            psi.Environment[name] = value;

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            FileLog.Write($"[CcDevthrottleLicenseCommandLine] StartSignInAsync FAILED: {ToolName} could not be started (error {ex.NativeErrorCode})");
            return new LicenseSignInStart { Finished = LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed) };
        }

        // Standard error carries the sentence for a person; it is drained so the tool never blocks on it, and dropped.
        var drained = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var conversation = new LicenseRemoteSignInConversation(
            process.StandardOutput, process.StandardInput,
            waitForExit: async ct => { await process.WaitForExitAsync(ct); await drained; },
            kill: () => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } },
            release: process.Dispose,
            SignInTimeout, FinishTimeout);
        var start = await conversation.StartAsync(cancellationToken);
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] StartSignInAsync: account={accountEmail}, {(start.Waiting is not null ? "waiting for another machine's approval" : $"finished: outcome={start.Finished?.Outcome}, reason={start.Finished?.ReasonCode ?? "-"}")}");
        return start;
    }

    /// <summary>The rows of a <c>renew</c> or <c>renew --check</c> answer: one JSON object whose <c>accounts</c> array holds
    /// <c>{accountEmail, role, result, signedInAtUtc, signInExpiresAtUtc}</c> rows. The tool's own <c>detail</c> sentence
    /// is never read (rule 6.1), and a failure word outside the contract reads as <c>login-failed</c>.</summary>
    internal static IReadOnlyList<LicenseRenewalRow> ReadRenewalRows(string? output)
    {
        using var document = ParseAccounts(output, "renew", out var rows);
        var answer = new List<LicenseRenewalRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new LicenseCommandLineException($"{ToolName} license renew answered a row that is not an object.");
            var role = Text(row, "role");
            if (role is not ("active" or "spare"))
                throw new LicenseCommandLineException($"{ToolName} license renew answered an account whose role is neither active nor spare.");
            var result = Text(row, "result") ?? "";
            var renewal = new LicenseRenewalRow
            {
                AccountEmail = Normalize(Text(row, "accountEmail"), "renew"),
                IsActive = role == "active",
                HasBrowserProfileHere = !string.IsNullOrWhiteSpace(Text(row, "browser")),
                SignedInAtUtc = Time(row, "signedInAtUtc"),
                SignInExpiresAtUtc = Time(row, "signInExpiresAtUtc"),
            };
            if (result is LicenseRenewalResults.Due or LicenseRenewalResults.Renewed or LicenseRenewalResults.NotDue or LicenseRenewalResults.UnknownExpiry)
            {
                renewal.Result = result;
            }
            else if (result.StartsWith(FailedPrefix, StringComparison.Ordinal))
            {
                renewal.Result = LicenseRenewalResults.Failed;
                renewal.Error = KnownRenewalError(result[FailedPrefix.Length..], "renew");
            }
            else
            {
                throw new LicenseCommandLineException($"{ToolName} license renew answered an account with a result this Director does not know.");
            }
            answer.Add(renewal);
        }
        return answer;
    }

    /// <summary>The <c>sign-in --json</c> answer: <c>{result: added|renewed, signedInAtUtc, signInExpiresAtUtc}</c>, or
    /// <c>{result: failed, reason}</c>. Anything else - nothing printed, text that is not JSON - is a failed login.</summary>
    internal static LicenseSignInAnswer ReadSignInAnswer(string? output)
    {
        using var document = FirstObject(output);
        if (document is null) return LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed);
        var root = document.RootElement;
        return Text(root, "result") switch
        {
            LicenseSignInOutcomes.Added => Succeeded(LicenseSignInOutcomes.Added, root),
            LicenseSignInOutcomes.Renewed => Succeeded(LicenseSignInOutcomes.Renewed, root),
            LicenseSignInOutcomes.Failed => LicenseSignInAnswer.Failed(KnownRenewalError(Text(root, "reason"), "sign-in")),
            _ => LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed),
        };
    }

    private static LicenseSignInAnswer Succeeded(string outcome, JsonElement root) => new()
    {
        Outcome = outcome,
        SignedInAtUtc = Time(root, "signedInAtUtc"),
        SignInExpiresAtUtc = Time(root, "signInExpiresAtUtc"),
    };

    /// <summary>The <c>approve --json</c> answer: <c>{result: approved, code}</c> or <c>{result: refused, reason}</c>.</summary>
    internal static LicenseApproveAnswer ReadApproveAnswer(string? output)
    {
        using var document = FirstObject(output);
        if (document is null) return LicenseApproveAnswer.Refused(LicenseRenewalErrors.ApprovalFailed);
        var root = document.RootElement;
        switch (Text(root, "result"))
        {
            case LicenseApprovalOutcomes.Approved:
                var approval = Text(root, "code");
                return string.IsNullOrWhiteSpace(approval)
                    ? LicenseApproveAnswer.Refused(LicenseRenewalErrors.ApprovalFailed)
                    : new LicenseApproveAnswer { Approved = true, Approval = approval.Trim() };
            case LicenseApprovalOutcomes.Refused:
                return LicenseApproveAnswer.Refused(KnownRenewalError(Text(root, "reason"), "approve"));
            default:
                return LicenseApproveAnswer.Refused(LicenseRenewalErrors.ApprovalFailed);
        }
    }

    /// <summary>The word when it is one of the contract's (section 8.1); otherwise <c>login-failed</c> for a sign-in and
    /// <c>approval-failed</c> for an approval. The unknown value is not logged or carried: it could be anything.</summary>
    private static string KnownRenewalError(string? word, string subcommand)
    {
        if (word is not null && LicenseRenewalErrors.All.Contains(word)) return word;
        var fallback = subcommand == "approve" ? LicenseRenewalErrors.ApprovalFailed : LicenseRenewalErrors.LoginFailed;
        FileLog.Write($"[CcDevthrottleLicenseCommandLine] license {subcommand} carried a reason outside the contract; read as '{fallback}'");
        return fallback;
    }

    /// <summary>The first JSON object in the output, or null. A sign-in started with <c>--remote-approval</c> prints its
    /// authorize URL line first, so the object is found, not assumed to be the whole output.</summary>
    private static JsonDocument? FirstObject(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var start = output.IndexOf('{');
        if (start < 0) return null;
        try
        {
            var document = JsonDocument.Parse(output[start..]);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Time(JsonElement row, string name)
    {
        var text = Text(row, name);
        return text is not null && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }
}

/// <summary>
/// The conversation with a running <c>license sign-in --json</c>: read lines until it prints
/// <c>authorize-url: ...</c> - or ends, having approved in a browser profile here or failed - and, when it is
/// waiting, later write the approval as one line and close its input, then read its JSON answer.
/// Closing the input with nothing written is how it is abandoned - the tool then ends the login itself, removing a
/// new spare's empty folder - and the process is killed only when it does not end in time.
///
/// It talks to streams, not to a process, so the whole exchange is tested without one.
/// </summary>
internal sealed class LicenseRemoteSignInConversation : ILicenseRemoteSignIn
{
    private readonly TextReader _output;
    private readonly TextWriter _input;
    private readonly Func<CancellationToken, Task> _waitForExit;
    private readonly Action _kill;
    private readonly Action _release;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _finishTimeout;
    private int _ended;

    /// <param name="startTimeout">How long the tool may take to print its authorize URL or to end.</param>
    /// <param name="finishTimeout">How long it may take to end once its input has ended.</param>
    public LicenseRemoteSignInConversation(TextReader output, TextWriter input, Func<CancellationToken, Task> waitForExit,
        Action kill, Action release, TimeSpan startTimeout, TimeSpan finishTimeout)
    {
        _output = output;
        _input = input;
        _waitForExit = waitForExit;
        _kill = kill;
        _release = release;
        _startTimeout = startTimeout;
        _finishTimeout = finishTimeout;
    }

    public string AuthorizeUrl { get; private set; } = "";

    /// <summary>Reads until the authorize URL. A tool that ends first is answered from what it printed: the sign-in
    /// it made by approving in a browser profile here, or its failure.</summary>
    public async Task<LicenseSignInStart> StartAsync(CancellationToken cancellationToken)
    {
        var printed = new StringBuilder();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_startTimeout);
        try
        {
            while (await _output.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.StartsWith(CcDevthrottleLicenseCommandLine.AuthorizeUrlPrefix, StringComparison.Ordinal))
                {
                    AuthorizeUrl = line[CcDevthrottleLicenseCommandLine.AuthorizeUrlPrefix.Length..].Trim();
                    if (AuthorizeUrl.Length > 0)
                        return new LicenseSignInStart { Waiting = this };
                }
                printed.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[LicenseRemoteSignInConversation] StartAsync FAILED: the sign-in neither asked for an approval nor ended in time; it is ended");
            End(kill: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new LicenseSignInStart { Finished = LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed) };
        }

        // The tool ended without asking: what it printed is its answer - signed in here, or why not.
        var answer = CcDevthrottleLicenseCommandLine.ReadSignInAnswer(printed.ToString());
        End(kill: false);
        return new LicenseSignInStart { Finished = answer };
    }

    public async Task<LicenseSignInAnswer> CompleteAsync(string approval, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approval)) throw new ArgumentException("An approval is required", nameof(approval));
        if (Interlocked.Exchange(ref _ended, 1) == 1)
            throw new InvalidOperationException("this sign-in has already been completed or abandoned");
        var printed = await FinishAsync(approval.Trim(), cancellationToken);
        return printed is null
            ? LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed)
            : CcDevthrottleLicenseCommandLine.ReadSignInAnswer(printed);
    }

    public async Task AbandonAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        await FinishAsync(null, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await AbandonAsync(CancellationToken.None);

    /// <summary>Writes the line (or nothing), closes the tool's input, and reads what it prints until it ends. Null
    /// when the tool did not end in time and was killed.</summary>
    private async Task<string?> FinishAsync(string? line, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_finishTimeout);
        try
        {
            try
            {
                if (line is not null)
                {
                    await _input.WriteLineAsync(line.AsMemory(), limit.Token);
                    await _input.FlushAsync(limit.Token);
                }
            }
            catch (IOException)
            {
                // The tool closed its end first. What it printed says what happened.
            }
            finally
            {
                try { _input.Close(); }
                catch (IOException) { /* the pipe is already broken; nothing is left to close */ }
            }

            var printed = await _output.ReadToEndAsync(limit.Token);
            await _waitForExit(limit.Token);
            _release();
            return printed;
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[LicenseRemoteSignInConversation] FinishAsync FAILED: the sign-in did not end in time and was killed");
            _kill();
            _release();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private void End(bool kill)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        try { _input.Close(); }
        catch (IOException) { /* already broken */ }
        if (kill) _kill();
        _release();
    }
}
