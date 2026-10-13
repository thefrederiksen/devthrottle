using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>Reading <c>cc-devthrottle license ... --json</c>, and running only this Director's own copy.</summary>
public sealed class LicenseCommandLineTests
{
    [Fact]
    public void ReadUsageRows_ContractShapedRows_ReadsThemLowerCased()
    {
        // The shape license usage --json prints: the contract's LicenseHolding rows, plus fields the Director ignores.
        const string json = """
            {"count": 2, "accounts": [
              {"accountEmail": "Account-A@Example.com", "isActive": true, "plan": "max", "rateLimitTier": "default_claude_max_20x",
               "fiveHour": {"utilization": 96.0, "resetsAtUtc": "2026-10-11T05:00:00Z"},
               "sevenDay": {"utilization": 61.0, "resetsAtUtc": "2026-10-15T09:00:00Z"},
               "extraUsage": {"isEnabled": false, "utilization": null},
               "readAtUtc": "2026-10-11T03:11:58Z", "readError": null, "readErrorDetail": null,
               "signedInAtUtc": null, "signInExpiresAtUtc": "2026-11-09T08:00:00Z", "folder": "C:/isolated/default"},
              {"accountEmail": "account-b@example.com", "isActive": false, "readError": "unauthorized", "readErrorDetail": "The sign-in was refused."}
            ]}
            """;

        var rows = CcDevthrottleLicenseCommandLine.ReadUsageRows(json);

        Assert.Equal("account-a@example.com", rows[0].AccountEmail);
        Assert.True(rows[0].IsActive);
        Assert.Equal("default_claude_max_20x", rows[0].RateLimitTier);
        Assert.Equal(96.0, rows[0].FiveHour!.Utilization);
        Assert.Equal(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc), rows[0].FiveHour!.ResetsAtUtc);
        Assert.Null(rows[0].SignedInAtUtc);   // the tool does not know it yet: null, the contract's "not known"
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc), rows[0].SignInExpiresAtUtc);
        Assert.Equal(LicenseReadErrors.Unauthorized, rows[1].ReadError);
        Assert.Null(rows[1].FiveHour);
    }

    [Fact]
    public void ReadUsageRows_ReadErrorOutsideTheContract_ReadsAsUnexpectedAnswerAndCarriesNoToolText()
    {
        // Review of 3868, P1: an unknown readError could be a provider's answer; it never travels or reaches the log.
        var marker = FakeLicenseCommandLine.ToolTextMarker;
        var json = "{\"accounts\": [{\"accountEmail\": \"account-a@example.com\", \"isActive\": true, " +
                   "\"readError\": \"" + marker + "\", \"readErrorDetail\": \"" + marker + "\"}]}";

        var row = CcDevthrottleLicenseCommandLine.ReadUsageRows(json).Single();

        Assert.Equal(LicenseReadErrors.UnexpectedAnswer, row.ReadError);
        Assert.Null(row.ReadErrorDetail);
        Assert.DoesNotContain(marker, LicenseUsageReporter.ReadErrorSentence(marker));
    }

    [Theory]
    [InlineData("switched")]
    [InlineData("failed")]
    public void ReadSwitchAnswer_ReasonOutsideTheContract_ReadsAsFailedStateUnknown(string outcome)
    {
        var answer = CcDevthrottleLicenseCommandLine.ReadSwitchAnswer(
            "{\"outcome\": \"" + outcome + "\", \"reasonCode\": \"" + FakeLicenseCommandLine.ToolTextMarker + "\", " +
            "\"activeAccountEmailAfter\": \"a@example.com\"}");

        Assert.Equal(LicenseSwitchOutcomes.Failed, answer!.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.ExchangeFailedUnknown, answer.ReasonCode);
    }

    [Fact]
    public void ReadListedAccounts_KeyStoreRows_MapsRoleToActive()
    {
        // The shape license list --json prints: email and role, among the key store's own fields.
        const string json = """
            {"count": 2, "accounts": [
              {"email": "Account-A@example.com", "role": "active", "organization": "", "configDir": "C:/isolated/default",
               "accountFile": "C:/isolated/.claude.json", "keyStore": "file", "keyLocation": "C:/isolated/default/.credentials.json"},
              {"email": "account-b@example.com", "role": "spare", "organization": "", "configDir": "C:/isolated/spares/b",
               "accountFile": "C:/isolated/spares/b/.claude.json", "keyStore": "file", "keyLocation": "C:/isolated/spares/b/.credentials.json"}
            ]}
            """;

        var rows = CcDevthrottleLicenseCommandLine.ReadListedAccounts(json);

        Assert.Equal(new[] { ("account-a@example.com", true), ("account-b@example.com", false) },
            rows.Select(r => (r.AccountEmail, r.IsActive)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{\"count\": 0}")]
    [InlineData("[{\"accountEmail\": \"a@example.com\"}]")]
    [InlineData("{\"accounts\": [{\"isActive\": true}]}")]
    public void ReadUsageRows_NotAnAccountsAnswer_ThrowsWithoutQuotingIt(string output)
    {
        var ex = Assert.Throws<LicenseCommandLineException>(() => CcDevthrottleLicenseCommandLine.ReadUsageRows(output));

        Assert.DoesNotContain("Traceback", ex.Message);
    }

    [Theory]
    [InlineData("{\"accounts\": [{\"email\": \"a@example.com\", \"role\": \"owner\"}]}")]
    [InlineData("{\"accounts\": [{\"role\": \"active\"}]}")]
    public void ReadListedAccounts_UnknownRoleOrNoEmail_Throws(string output)
    {
        Assert.Throws<LicenseCommandLineException>(() => CcDevthrottleLicenseCommandLine.ReadListedAccounts(output));
    }

    [Fact]
    public void ReadListedAccounts_EmptyStore_IsAnEmptyListNotAnError()
    {
        Assert.Empty(CcDevthrottleLicenseCommandLine.ReadListedAccounts("{\"count\": 0, \"accounts\": []}"));
    }

    [Fact]
    public void ReadSwitchAnswer_ContractShapedAnswer_ReadsIt()
    {
        const string json = """
            {"outcome": "failed", "reasonCode": "exchange-failed-restored", "message": "Put back.",
             "previousActiveAccountEmail": "Account-A@example.com", "activeAccountEmailAfter": "account-a@example.com",
             "exchangeMilliseconds": 12}
            """;

        var answer = CcDevthrottleLicenseCommandLine.ReadSwitchAnswer(json)!;

        Assert.Equal(LicenseSwitchOutcomes.Failed, answer.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.ExchangeFailedRestored, answer.ReasonCode);
        Assert.Equal("account-a@example.com", answer.PreviousActiveAccountEmail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"outcome\": \"maybe\"}")]
    public void ReadSwitchAnswer_UnreadableOrUnknownOutcome_IsNull(string output)
    {
        Assert.Null(CcDevthrottleLicenseCommandLine.ReadSwitchAnswer(output));
    }

    [Fact]
    public async Task SwitchAsync_ToolNotInstalled_AnswersExchangeFailedUnknown()
    {
        var tool = new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(null, null), () => null);

        var answer = await tool.SwitchAsync("account-b@example.com", "account-a@example.com", CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Failed, answer.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.ExchangeFailedUnknown, answer.ReasonCode);
    }

    [Fact]
    public async Task ListAsync_ToolNotInstalled_ThrowsNamingTheToolDirectory()
    {
        var tool = new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(null, null), () => null);

        var ex = await Assert.ThrowsAsync<LicenseCommandLineException>(() => tool.ListAsync(CancellationToken.None));

        Assert.Contains("not installed", ex.Message);
    }

    [Fact]
    public async Task ReadUsageAsync_EveryHolding_RunsTheToolWithRefreshSoAnUnusedSpareIsRenewed()
    {
        // Whole-phase review of phase 1, finding 1: the reporter's spare read never asked the tool to renew a spare,
        // so an unused spare's key expired and the spare stopped being a switch target. This runs the real adapter
        // against a stand-in tool that records the arguments it was given.
        using var tool = RecordingTool.Create();
        var adapter = new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions("C:/isolated/default", "C:/isolated/store"), () => tool.Path);

        var rows = await adapter.ReadUsageAsync(activeOnly: false, CancellationToken.None);

        Assert.Equal("account-a@example.com", rows.Single().AccountEmail);
        Assert.Equal(new[] { "license", "usage", "--refresh", "--json", "--default-config-dir", "C:/isolated/default", "--store-dir", "C:/isolated/store" },
            tool.Arguments());
    }

    [Fact]
    public async Task ReadUsageAsync_ActiveOnly_RunsTheToolWithoutRefresh()
    {
        // The active key is the sessions' to renew; the active-only read never asks the tool to renew anything.
        using var tool = RecordingTool.Create();
        var adapter = new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(null, null), () => tool.Path);

        await adapter.ReadUsageAsync(activeOnly: true, CancellationToken.None);

        Assert.Equal(new[] { "license", "usage", "--active", "--json" }, tool.Arguments());
    }

    /// <summary>A stand-in for <c>cc-devthrottle</c>: writes each argument it was given on its own line, and answers
    /// one usage row.</summary>
    private sealed class RecordingTool : IDisposable
    {
        private readonly string _folder;
        private readonly string _argumentsFile;

        private RecordingTool(string folder)
        {
            _folder = folder;
            _argumentsFile = System.IO.Path.Combine(folder, "arguments.txt");
            const string answer = "{\"accounts\": [{\"accountEmail\": \"account-a@example.com\", \"isActive\": true}]}";
            if (OperatingSystem.IsWindows())
            {
                Path = System.IO.Path.Combine(folder, "cc-devthrottle.cmd");
                File.WriteAllText(Path,
                    "@echo off\r\n" +
                    ":next\r\n" +
                    "if \"%~1\"==\"\" goto done\r\n" +
                    ">>\"" + _argumentsFile + "\" echo %~1\r\n" +
                    "shift\r\n" +
                    "goto next\r\n" +
                    ":done\r\n" +
                    "echo " + answer + "\r\n");
            }
            else
            {
                Path = System.IO.Path.Combine(folder, "cc-devthrottle");
                File.WriteAllText(Path,
                    "#!/bin/sh\n" +
                    "for a in \"$@\"; do printf '%s\\n' \"$a\" >> '" + _argumentsFile + "'; done\n" +
                    "printf '%s\\n' '" + answer + "'\n");
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public static RecordingTool Create()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "license-recording-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return new RecordingTool(folder);
        }

        public string[] Arguments() => File.ReadAllLines(_argumentsFile).Select(l => l.TrimEnd()).ToArray();

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void OwnToolDirectory_IsTheInstallersPythonScriptFolder()
    {
        // The installer (tools/cc-director-setup-engine InstallLayout.PyenvBinDir) puts every Python tool's console
        // script here, on the machine root, and both the Windows shim and the macOS symlink point at it.
        var expected = Path.Combine(CcDirector.Core.Storage.CcStorage.MachineRoot(), "pyenv", OperatingSystem.IsWindows() ? "Scripts" : "bin");

        Assert.Equal(expected, CcDevthrottleLicenseCommandLine.OwnToolDirectory());
    }

    [Fact]
    public void ResolveOwnCopy_EmptyToolDirectory_IsNullEvenWhenPathHasOne()
    {
        // The Director runs its OWN copy, never whatever the search path resolves. The test builds its own search
        // path holding a cc-devthrottle (and proves the resolver finds it there first), so it does not depend on
        // what this machine has installed; an empty tool directory still resolves nothing.
        var root = Path.Combine(Path.GetTempPath(), "license-tool-dir-" + Guid.NewGuid().ToString("N"));
        var empty = Path.Combine(root, "own");
        var onSearchPath = Path.Combine(root, "on-search-path");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(onSearchPath);
        try
        {
            var other = WriteTool(onSearchPath);
            const string? pathExt = null; // the resolver's own default list, which holds .CMD
            Assert.Equal(other, CcDirector.Core.Utilities.ExecutableResolver.Resolve(CcDevthrottleLicenseCommandLine.ToolName, onSearchPath, pathExt), ignoreCase: true);

            Assert.Null(CcDevthrottleLicenseCommandLine.ResolveOwnCopy(empty, onSearchPath, pathExt));

            var own = WriteTool(empty);
            Assert.Equal(own, CcDevthrottleLicenseCommandLine.ResolveOwnCopy(empty, onSearchPath, pathExt), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteTool(string folder)
    {
        var tool = Path.Combine(folder, CcDevthrottleLicenseCommandLine.ToolName + (OperatingSystem.IsWindows() ? ".cmd" : ""));
        File.WriteAllText(tool, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Path.GetFullPath(tool);
    }
}
