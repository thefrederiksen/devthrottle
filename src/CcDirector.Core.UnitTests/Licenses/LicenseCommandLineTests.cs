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
        // The Director runs its OWN copy, never whatever PATH resolves. This machine's PATH has a cc-devthrottle (the
        // test proves it first), and an empty tool directory still resolves nothing.
        Assert.NotNull(CcDirector.Core.Utilities.ExecutableResolver.Resolve(CcDevthrottleLicenseCommandLine.ToolName));
        var empty = Path.Combine(Path.GetTempPath(), "license-tool-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Null(CcDevthrottleLicenseCommandLine.ResolveOwnCopy(empty));

            var own = Path.Combine(empty, CcDevthrottleLicenseCommandLine.ToolName + (OperatingSystem.IsWindows() ? ".cmd" : ""));
            File.WriteAllText(own, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal(Path.GetFullPath(own), Path.GetFullPath(CcDevthrottleLicenseCommandLine.ResolveOwnCopy(empty)!), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
