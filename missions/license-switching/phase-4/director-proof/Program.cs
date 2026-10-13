using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.LicenseProof;

// The License Switching mission, phase 4, second pull request: the Director's side of a sign-in, joined end to end
// on ONE machine with real processes and no real sign-in.
//
// What is real: the Director's own classes (the sign-in coordinator, the approving side, the daily renewal, the
// usage reporter), `cc-devthrottle license` run from this checkout as a real process (so the conversation over its
// two pipes is the real one), the key store it writes (isolated folders), and `cc-secrets seal` / `open` run from
// this checkout with two separate machine keys.
//
// What is stood in for, and by what:
//   - `claude auth login`        fake_claude.py: prints an authorize URL, reads one line, writes a made-up sign-in
//   - the browser approval        a fixed answer (the real one is proven in ../windows-2026-10-11.txt)
//   - the Gateway relay           ProofGateway below: it passes the request to the approving Director and the
//                                 sealed answer back, and keeps a copy of everything it carried
//   - usage reads                 a listing: nothing here ever contacts a provider
//
// Run it as README.md beside this file says. It prints one PASS or FAIL line per check and exits non-zero on a FAIL.
internal static class Program
{
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";
    private const string AccountC = "account-c@example.com";

    /// <summary>The stand-in for the authorize answer. It must reach the waiting login and nothing else.</summary>
    private static readonly string Answer = "PROOFANSWER" + Guid.NewGuid().ToString("N") + "#PROOFSTATE" + Guid.NewGuid().ToString("N");

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly List<(string From, string To)> Redactions = new();
    private static int _failed;
    private static TimeSpan _clockOffset = TimeSpan.Zero;

    private static DateTime Now() => DateTime.UtcNow + _clockOffset;

    private static async Task<int> Main(string[] args)
    {
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
            ?? throw new ArgumentException($"{name} is required: --root <new folder> --python <python.exe> --repo <checkout>");
        var root = Path.GetFullPath(Arg("--root"));
        var python = Arg("--python");
        var repo = Path.GetFullPath(Arg("--repo"));
        if (Directory.Exists(root)) throw new ArgumentException("--root must be a folder that does not exist yet; the proof creates it and works only inside it");
        Redactions.Add((root, "<proof root>"));
        Redactions.Add((root.Replace("\\", "\\\\"), "<proof root>"));
        Redactions.Add((repo, "<checkout>"));

        // ---- an isolated machine: its own Director root, its own Claude config, its own key store ----
        var directorRoot = Path.Combine(root, "director-root");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", directorRoot);
        Environment.SetEnvironmentVariable("CC_SESSION_ID", null);
        Environment.SetEnvironmentVariable("CC_GATEWAY_URL", null);
        Environment.SetEnvironmentVariable("CC_GATEWAY_SESSION_KEY", null);
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_ANSWER", Answer);
        // A tool that ends in a failure files an error report (cc_shared/tool_errors.py). This proof makes failures on
        // purpose, so its reports are pointed at a closed local address: they stay in the proof root and go nowhere.
        Environment.SetEnvironmentVariable("DEVTHROTTLE_HOSTED_GATEWAY_URL", "http://127.0.0.1:9");
        var defaultDir = Path.Combine(root, "target", "default");
        var storeDir = Path.Combine(root, "target", "store");
        var fakeBin = Path.Combine(root, "fakebin");
        var toolDir = Path.Combine(directorRoot, "pyenv", "Scripts");
        foreach (var d in new[] { defaultDir, storeDir, fakeBin, toolDir }) Directory.CreateDirectory(d);
        var proofDir = Path.Combine(repo, "missions", "license-switching", "phase-4", "director-proof");
        File.WriteAllText(Path.Combine(fakeBin, "claude.cmd"), $"@\"{python}\" \"{Path.Combine(proofDir, "fake_claude.py")}\" %*\r\n");
        File.WriteAllText(Path.Combine(toolDir, "cc-devthrottle.cmd"),
            "@echo off\r\n" +
            $"set \"PYTHONPATH={Path.Combine(repo, "tools")}\"\r\n" +
            $"set \"PATH={fakeBin};%PATH%\"\r\n" +
            $"cd /d \"{Path.Combine(repo, "tools", "cc-devthrottle")}\"\r\n" +
            $"\"{python}\" -m src.cli %*\r\n");
        WriteSignIn(defaultDir, AccountA, days: 20);
        FileLog.UseLogDirectory(Path.Combine(directorRoot, "logs"), "proof");
        FileLog.Start();

        var secrets = new ProofSecrets(python, Path.Combine(repo, "tools"));
        var targetHome = Path.Combine(root, "secrets-target");
        var approverHome = Path.Combine(root, "secrets-approver");
        var targetPublic = await secrets.MachineKeyAsync(targetHome);

        Say("The License Switching Director proof: two Directors on one machine, a stand-in claude, no real sign-in.");
        Say($"started {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

        var targetTool = new TargetTool(new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(defaultDir, storeDir)));
        var approverTool = new ApproverTool();
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var target = new LicenseSwitching(targetTool, LicenseSwitching.DirectorFolder(storeDir), Now, "the-target-machine", "owner", enabled: true,
            signInDelay: (wait, ct) => Task.WhenAny(Task.Delay(wait, ct), deadline.Task));
        using var approver = new LicenseSwitching(approverTool, Path.Combine(root, "approver", "director"), Now, "the-approving-machine", "owner", enabled: true);
        var gateway = new ProofGateway(target, approver, targetPublic);
        var targetReports = new Reports();
        var approverReports = new Reports();
        target.Reporter.AttachSink(targetReports);
        approver.Reporter.AttachSink(approverReports);
        target.SignIns.AttachRelay(gateway);
        target.SignIns.AttachSeal(secrets.For(targetHome));
        approver.Approver.AttachSeal(secrets.For(approverHome));

        // ---- 1 ----
        Say("\n--- 1. both Directors take their key holder lock and report; the approving machine holds nothing");
        var first = await target.Reporter.TickAsync(CancellationToken.None);
        var approverFirst = await approver.Reporter.TickAsync(CancellationToken.None);
        Check("target-reports", first is { Trigger: LicenseReportTriggers.Connected } && Holdings(first) == $"{AccountA}:active",
            $"trigger={first?.Trigger}, holdings={Holdings(first)}");
        Check("approver-reports-with-no-holdings", approverFirst is { Trigger: LicenseReportTriggers.Connected, Holdings.Count: 0 },
            $"trigger={approverFirst?.Trigger}, holdings={approverFirst?.Holdings.Count} (contract 2.4 as amended: this report is how the Gateway finds the approving Director)");

        // ---- 2 ----
        Say("\n--- 2. license-sign-in for account B: no browser profile here, so it is approved on the other machine");
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_DAYS", "2");   // so the daily renewal below finds it due
        var signInId = Guid.NewGuid().ToString("N");
        var added = await target.SignIns.SignInAsync(SignIn(signInId, AccountB), CancellationToken.None);
        Check("added", added is { Outcome: LicenseSignInOutcomes.Added, ReasonCode: null }, $"outcome={added.Outcome}, reason={added.ReasonCode ?? "-"}, signedInAtUtc={added.SignedInAtUtc:O}, signInExpiresAtUtc={added.SignInExpiresAtUtc:O}");
        Check("held-as-spare", await HeldAsync(targetTool) == $"{AccountA}:active {AccountB}:spare", await HeldAsync(targetTool));
        Check("the-url-named-the-account", gateway.Requests.Count == 1 && gateway.Requests[0].AuthorizeUrl.Contains("login_hint=account-b%40example.com"),
            $"requests={gateway.Requests.Count}; the tool on the approving machine refuses a URL that names another account");
        Check("gateway-carried-only-the-sealed-answer", gateway.Carried.Count == 2 && gateway.Carried.All(c => !c.Contains(Answer)) && gateway.Carried[1].Contains("sealedApproval"),
            $"carried {gateway.Carried.Count} messages; the answer itself is in none of them");
        var keyB = KeyPrint(storeDir, AccountB);
        var afterAdd = await target.Reporter.TickAsync(CancellationToken.None);
        Check("changed-report", afterAdd is { Trigger: LicenseReportTriggers.Changed } && Holdings(afterAdd) == $"{AccountA}:active {AccountB}:spare" && Holding(afterAdd, AccountB)?.RenewalError is null,
            $"trigger={afterAdd?.Trigger}, holdings={Holdings(afterAdd)}");

        // ---- 3 ----
        Say("\n--- 3. the same SignInId again is answered from the stored result and runs nothing");
        var again = await target.SignIns.SignInAsync(SignIn(signInId, AccountB), CancellationToken.None);
        Check("replayed", again is { Replayed: true, Outcome: LicenseSignInOutcomes.Added } && gateway.Requests.Count == 1 && KeyPrint(storeDir, AccountB) == keyB,
            $"replayed={again.Replayed}, approval requests still {gateway.Requests.Count}, the held sign-in unchanged ({keyB})");

        // ---- 4 ----
        Say("\n--- 4. the daily renewal while NO Gateway relays an approval (every Director today): account B is due and has no profile here");
        gateway.CanRequest = false;
        var startedBefore = targetTool.SignInsStarted;
        var noRelayRun = await target.Renewer.TickAsync(CancellationToken.None);
        Check("no-approver", Rows(noRelayRun) == $"{AccountA}:not-due {AccountB}:failed:{LicenseRenewalErrors.NoApprover}", Rows(noRelayRun));
        Check("no-sign-in-started", targetTool.SignInsStarted == startedBefore && gateway.Requests.Count == 1,
            $"the tool's sign-in was started {targetTool.SignInsStarted - startedBefore} time(s) for it and no approval was asked for: the check's own row said no profile is set here");
        Check("held-sign-in-untouched-by-it", KeyPrint(storeDir, AccountB) == keyB, $"the sign-in held for account B is the one it had ({keyB})");
        var noRelayReport = await target.Reporter.TickAsync(CancellationToken.None);
        Check("no-approver-travels-in-the-report", noRelayReport is { Trigger: LicenseReportTriggers.Changed } && Holding(noRelayReport, AccountB) is { RenewalError: LicenseRenewalErrors.NoApprover },
            $"trigger={noRelayReport?.Trigger}, renewalError={Holding(noRelayReport, AccountB)?.RenewalError}");
        var asked = await target.SignIns.SignInAsync(SignIn(Guid.NewGuid().ToString("N"), AccountC), CancellationToken.None);
        Check("asked-sign-in-is-no-approver-too", asked is { Outcome: LicenseSignInOutcomes.Failed, ReasonCode: LicenseRenewalErrors.NoApprover } && targetTool.SignInsStarted == startedBefore,
            $"a license-sign-in for account C: outcome={asked.Outcome}, reason={asked.ReasonCode}; the tool was asked whether a profile is set (license get-browser) and no sign-in was started");
        gateway.CanRequest = true;

        Say("\n--- 4b. a day later a Gateway relays: account B is due, the other machine REFUSES (its profile is signed in as someone else)");
        _clockOffset = LicenseContract.RenewalRunInterval;
        approverTool.Refuse = LicenseRenewalErrors.WrongAccount;
        var failedRun = await target.Renewer.TickAsync(CancellationToken.None);
        Check("renewal-failed", Rows(failedRun) == $"{AccountA}:not-due {AccountB}:failed:{LicenseRenewalErrors.WrongAccount}", Rows(failedRun));
        Check("held-sign-in-untouched", KeyPrint(storeDir, AccountB) == keyB, $"the sign-in held for account B is the one it had ({keyB}); a failed renewal writes nothing");
        var failedReport = await target.Reporter.TickAsync(CancellationToken.None);
        var failedHolding = Holding(failedReport, AccountB);
        Check("failure-travels-in-the-report", failedReport is { Trigger: LicenseReportTriggers.Changed } && failedHolding is { RenewalError: LicenseRenewalErrors.WrongAccount, RenewalFailedAtUtc: not null },
            $"trigger={failedReport?.Trigger}, renewalError={failedHolding?.RenewalError}, renewalErrorDetail=\"{failedHolding?.RenewalErrorDetail}\"");

        // ---- 5 ----
        Say("\n--- 5. a day later the renewal runs again, the other machine approves, and the sign-in is renewed IN PLACE");
        approverTool.Refuse = null;
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_DAYS", "29");
        Check("not-run-twice-a-day", await target.Renewer.TickAsync(CancellationToken.None) is null, "a second check inside the day runs nothing");
        _clockOffset = LicenseContract.RenewalRunInterval * 2;
        var renewedRun = await target.Renewer.TickAsync(CancellationToken.None);
        var renewedKey = KeyPrint(storeDir, AccountB);
        Check("renewed", Rows(renewedRun) == $"{AccountA}:not-due {AccountB}:renewed", Rows(renewedRun));
        Check("renewed-in-place", renewedKey != keyB && await HeldAsync(targetTool) == $"{AccountA}:active {AccountB}:spare" && SpareFolders(storeDir) == 1,
            $"same folder, a new sign-in ({keyB} -> {renewedKey}); {SpareFolders(storeDir)} spare folder; {await HeldAsync(targetTool)}");
        var renewedReport = await target.Reporter.TickAsync(CancellationToken.None);
        Check("failure-cleared-in-the-report", renewedReport is { Trigger: LicenseReportTriggers.Changed } && Holding(renewedReport, AccountB) is { RenewalError: null, RenewalFailedAtUtc: null },
            $"trigger={renewedReport?.Trigger}, renewalError={Holding(renewedReport, AccountB)?.RenewalError ?? "none"}");

        // ---- 6 ----
        Say("\n--- 6. a sign-in for account C that nobody answers: at its deadline the login is ended and nothing is left");
        gateway.Silent = true;
        var waitingFor = target.SignIns.SignInAsync(SignIn(Guid.NewGuid().ToString("N"), AccountC), CancellationToken.None);
        await gateway.Asked.Task.WaitAsync(TimeSpan.FromMinutes(2));
        deadline.SetResult();
        var expired = await waitingFor;
        Check("expired", expired is { Outcome: LicenseSignInOutcomes.Failed, ReasonCode: LicenseRenewalErrors.Expired }, $"outcome={expired.Outcome}, reason={expired.ReasonCode}");
        Check("nothing-left-behind", SpareFolders(storeDir) == 1 && await HeldAsync(targetTool) == $"{AccountA}:active {AccountB}:spare" && KeyPrint(storeDir, AccountB) == renewedKey,
            $"{SpareFolders(storeDir)} spare folder (no folder for account C); {await HeldAsync(targetTool)}; account B's sign-in unchanged");
        var late = target.Approvals.Deliver(new LicenseApprovalResult { SignInId = gateway.Requests[^1].SignInId, Outcome = LicenseApprovalOutcomes.Refused, ReasonCode = LicenseRenewalErrors.ApproverOffline });
        Check("late-answer-dropped", !late, "an answer that arrives after the deadline reaches nobody");

        // ---- 7 ----
        Say("\n--- 7. the answer was typed into the waiting login and is nowhere else");
        FileLog.Stop();
        var logs = string.Concat(Directory.EnumerateFiles(Path.Combine(directorRoot, "logs"), "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Check("log-has-the-run", logs.Contains("[LicenseSignInCoordinator]") && logs.Contains("[LicenseApprover]") && logs.Contains("[LicenseRenewer]"), "the Director log of this run was found and read");
        Check("answer-not-in-the-log", !logs.Contains(Answer), "not in the Director log");
        Check("url-not-in-the-log", !logs.Contains("oauth/authorize"), "nor is any authorize URL");
        var stored = string.Concat(Directory.EnumerateFiles(LicenseSwitching.DirectorFolder(storeDir), "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Check("answer-not-in-a-stored-result", stored.Contains(signInId) && !stored.Contains(Answer), "not in the stored sign-in results");

        // ---- 8 ----
        Say("\n--- 8. no call in this run filed a tool error report - counted, and the count is shown to see one");
        var outbox = Path.Combine(directorRoot, "logs", "error-outbox", "tool");
        var kept = ReportsKept(outbox);
        Check("no-error-report-kept", kept == 0, $"{kept} report file(s) in the tool's error outbox after steps 1 to 7 (a refused renewal, a no-approver renewal and a sign-in called off at its deadline among them)");
        // The control: one sign-in that really fails, started on purpose with no profile and no leave to ask. If the
        // count above could not see a report, it could not see this one either.
        var control = await targetTool.StartASignInThatFailsAsync(AccountC);
        var keptAfter = ReportsKept(outbox);
        Check("the-count-sees-a-report", control.Finished is { ReasonCode: LicenseRenewalErrors.NoApprover } && keptAfter == 1,
            $"control: a sign-in the Director never makes (no profile, told not to ask) ended {control.Finished?.ReasonCode} and left {keptAfter} report file in the same outbox");

        Say($"\n{(_failed == 0 ? "PASSED" : "FAILED")}: {_failed} check(s) failed. The proof root can be deleted; it holds only made-up sign-ins.");
        return _failed == 0 ? 0 : 1;
    }

    private static LicenseSignInRequest SignIn(string signInId, string email) => new()
    {
        SignInId = signInId, AccountEmail = email, Reason = LicenseSignInReasons.Manual, NotAfterUtc = Now() + LicenseContract.SignInNotAfter,
    };

    private static string Holdings(LicenseUsageReport? report) =>
        report is null ? "(no report)" : string.Join(" ", report.Holdings.Select(h => $"{h.AccountEmail}:{(h.IsActive ? "active" : "spare")}"));

    /// <summary>The report files the tool's error reporting keeps on disk before it sends them (cc_shared/tool_errors.py).</summary>
    private static int ReportsKept(string outbox) =>
        Directory.Exists(outbox) ? Directory.EnumerateFiles(outbox, "report-*.json").Count() : 0;

    private static LicenseHolding? Holding(LicenseUsageReport? report, string email) => report?.Holdings.FirstOrDefault(h => h.AccountEmail == email);

    private static string Rows(IReadOnlyList<LicenseRenewalRow>? rows) =>
        rows is null ? "(no run)" : string.Join(" ", rows.OrderBy(r => r.AccountEmail).Select(r => $"{r.AccountEmail}:{r.Result}{(r.Error is null ? "" : ":" + r.Error)}"));

    private static async Task<string> HeldAsync(ILicenseCommandLine tool) =>
        string.Join(" ", (await tool.ListAsync(CancellationToken.None)).OrderBy(h => h.AccountEmail).Select(h => $"{h.AccountEmail}:{(h.IsActive ? "active" : "spare")}"));

    private static int SpareFolders(string storeDir) =>
        Directory.Exists(Path.Combine(storeDir, "spares")) ? Directory.GetDirectories(Path.Combine(storeDir, "spares")).Length : 0;

    /// <summary>The first 8 hexadecimal digits of the SHA-256 of the held key file: enough to see it change, never the key.</summary>
    private static string KeyPrint(string storeDir, string email)
    {
        var file = Path.Combine(storeDir, "spares", email.Replace("@", "-"), ".credentials.json");
        return File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..8].ToLowerInvariant() : "(none)";
    }

    private static void WriteSignIn(string folder, string email, double days)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(folder, ".claude.json"), JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = email } }));
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = "fake-access-" + Guid.NewGuid().ToString("N"), refreshToken = "fake-refresh-" + Guid.NewGuid().ToString("N"),
                expiresAt = nowMs + 8 * 3600 * 1000, refreshTokenExpiresAt = nowMs + (long)(days * 86400 * 1000),
                scopes = new[] { "user:inference" }, subscriptionType = "max",
            },
        }));
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failed++;
        Say($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}");
    }

    private static void Say(string text)
    {
        foreach (var (from, to) in Redactions) text = text.Replace(from, to);
        Console.WriteLine(text);
    }

    /// <summary>The target machine's license tool: the real one, except that a usage read is answered from a listing,
    /// so nothing in this proof contacts a provider with a made-up key.</summary>
    private sealed class TargetTool(CcDevthrottleLicenseCommandLine real) : ILicenseCommandLine
    {
        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken ct) => real.ListAsync(ct);
        public async Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken ct) =>
            (await real.ListAsync(ct)).Where(h => !activeOnly || h.IsActive).ToList();
        public Task<LicenseExchangeAnswer> SwitchAsync(string target, string expected, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken ct) => real.CheckRenewalsAsync(ct);
        public Task<bool?> HasBrowserProfileAsync(string email, CancellationToken ct) => real.HasBrowserProfileAsync(email, ct);
        public Task<LicenseSignInStart> StartSignInAsync(string email, bool ask, CancellationToken ct)
        {
            SignInsStarted++;
            return real.StartSignInAsync(email, ask, ct);
        }

        /// <summary>How many times this Director started the tool's sign-in.</summary>
        public int SignInsStarted { get; private set; }

        /// <summary>The control for the error report count: a sign-in that really fails, started on purpose.</summary>
        public Task<LicenseSignInStart> StartASignInThatFailsAsync(string email) => real.StartSignInAsync(email, false, CancellationToken.None);
        public Task<LicenseApproveAnswer> ApproveAsync(string url, string email, string profile, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The approving machine's license tool: it holds nothing, and its browser approval is a fixed answer.</summary>
    private sealed class ApproverTool : ILicenseCommandLine
    {
        public string? Refuse { get; set; }

        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LicenseHolding>>(Array.Empty<LicenseHolding>());
        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken ct) => ListAsync(ct);
        public Task<LicenseExchangeAnswer> SwitchAsync(string target, string expected, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool?> HasBrowserProfileAsync(string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<LicenseSignInStart> StartSignInAsync(string email, bool ask, CancellationToken ct) => throw new NotSupportedException();
        public Task<LicenseApproveAnswer> ApproveAsync(string url, string email, string profile, CancellationToken ct) =>
            Task.FromResult(Refuse is { } reason ? LicenseApproveAnswer.Refused(reason) : new LicenseApproveAnswer { Approved = true, Approval = Answer });
    }

    /// <summary>Stands in for the Gateway relay of contract 8.3: takes the request and returns at once; sends
    /// license-approve to the approving Director; sends its answer back as license-approval-result. It keeps the JSON
    /// of everything it carried, so the proof can show the answer was in none of it.</summary>
    private sealed class ProofGateway(LicenseSwitching target, LicenseSwitching approver, string targetPublic) : ILicenseApprovalRelay
    {
        public bool CanRequest { get; set; } = true;
        public bool Silent { get; set; }
        public List<LicenseApprovalRequest> Requests { get; } = new();
        public List<string> Carried { get; } = new();
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestAsync(LicenseApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Silent)
            {
                Asked.TrySetResult();
                return Task.CompletedTask;
            }
            Carried.Add(JsonSerializer.Serialize(request, Web));
            _ = Task.Run(async () =>
            {
                var result = await approver.Approver.ApproveAsync(new LicenseApproveRequest
                {
                    SignInId = request.SignInId, AccountEmail = request.AccountEmail, AuthorizeUrl = request.AuthorizeUrl,
                    BrowserProfile = "the proof profile", SealToRecipient = targetPublic, NotAfterUtc = request.NotAfterUtc,
                }, CancellationToken.None);
                Carried.Add(JsonSerializer.Serialize(result, Web));
                target.Approvals.Deliver(result);
            }, CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    private sealed class Reports : ILicenseReportSink
    {
        public bool CanSend => true;
        public Task SendAsync(LicenseUsageReport report, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary><c>cc-secrets</c> run from this checkout, with one private folder per machine.</summary>
    private sealed class ProofSecrets(string python, string toolsDir)
    {
        public ILicenseApprovalSeal For(string home) => new Seal(this, home);

        public async Task<string> MachineKeyAsync(string home)
        {
            var printed = await RunAsync(home, new[] { "machine-key", "--json" }, null)
                ?? throw new InvalidOperationException("cc-secrets machine-key failed");
            return JsonDocument.Parse(printed).RootElement.GetProperty("publicKey").GetString()!;
        }

        private async Task<string?> RunAsync(string home, IReadOnlyList<string> args, string? standardInput)
        {
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
                CreateNoWindow = true, WorkingDirectory = Path.Combine(toolsDir, "cc-secrets"),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["CC_SECRETS_HOME"] = home;
            psi.Environment["PYTHONPATH"] = toolsDir;
            foreach (var a in new[] { "-m", "src.cli" }.Concat(args)) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (standardInput is not null) await process.StandardInput.WriteLineAsync(standardInput);
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            await errors;
            return process.ExitCode == 0 ? (await output).Trim() : null;
        }

        private sealed class Seal(ProofSecrets secrets, string home) : ILicenseApprovalSeal
        {
            public Task<string?> SealAsync(string approval, string recipient, string signInId, CancellationToken ct) =>
                secrets.RunAsync(home, new[] { "seal", "--to", recipient, "--purpose", LicenseContract.ApprovalSealPurpose, "--id", signInId }, approval);

            public Task<string?> OpenAsync(string sealedApproval, string signInId, CancellationToken ct) =>
                secrets.RunAsync(home, new[] { "open", "--purpose", LicenseContract.ApprovalSealPurpose, "--id", signInId }, sealedApproval);
        }
    }
}
