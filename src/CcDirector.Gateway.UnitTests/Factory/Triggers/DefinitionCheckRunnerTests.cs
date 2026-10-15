using System.Text;
using System.Text.Json;
using CcDirector.ControlApi.Triggers;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE DIRECTOR RUNS A CHECK FROM A DEFINITION (Factory Definition, phase 5b, design 2.2 to 2.5): the version folder
/// written whole once, Python or uv chosen from the script header, cc-secrets asked first and then used with the flags
/// phase 0 proved, the environment, and the state file. Every external process - Python, uv, cc-secrets - is a fake
/// (<see cref="FakeProcesses"/>) that records what it was asked to run, so no test here starts a real process. The
/// fake "check" is a function that reads and writes the state file the runner named, as a real check would.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class DefinitionCheckRunnerTests : IDisposable
{
    private const string Director = "dir-north-1";
    private const string Factory = "devthrottle";
    private const string CheckPath = "checks/new_mail.py";
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dcr-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource _source = new();
    private readonly FakeProcesses _processes = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private DefinitionCheckRunner Runner(bool isWindows = true) => new(Director, _root, () => _source, _processes,
        () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "the-path", ["HOME"] = "home" },
        () => Now, isWindows);

    private static TriggerAssignmentDto Check(int version = 3, string? state = "", params string[] secrets) => new()
    {
        Id = "t1", Name = "outreach-new-mail", Kind = TriggerKinds.Definition, Factory = Factory, Seat = "outreach",
        DefinitionVersion = version, CheckPath = CheckPath, Secrets = secrets.ToList(), State = state,
        IntervalSeconds = 600, TimeoutSeconds = 120,
    };

    private static string ListJson(params (string Name, string EnvName, string[] Uses)[] entries)
        => JsonSerializer.Serialize(new
        {
            entries = entries.Select(e => new { name = e.Name, kind = "secret", envName = e.EnvName, uses = e.Uses }),
        });

    private static readonly string[] Run = { "run" };

    // ---------- fakes ----------

    private sealed class FakeSource : IFactoryCheckSource
    {
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal)
        {
            [CheckPath] = "import json\nprint(json.dumps({'count': 0}))\n",
            ["files/mail-rules.json"] = "{}",
        };
        public string? Refusal { get; set; }
        public List<(string Director, string Factory, int Version)> Fetches { get; } = new();

        public Task<FactoryCheckFetch> FetchFactoryChecksAsync(string directorId, string factory, int version, CancellationToken ct)
        {
            Fetches.Add((directorId, factory, version));
            return Task.FromResult(Refusal is null ? new FactoryCheckFetch(Files, null) : FactoryCheckFetch.Refused(Refusal));
        }
    }

    private sealed class FakeProcesses : ICheckProcesses
    {
        public Dictionary<string, string> OnPath { get; } = new(StringComparer.Ordinal)
        {
            ["python.exe"] = @"C:\Python\python.exe", ["python3"] = "/usr/bin/python3",
            ["uv.exe"] = @"C:\tools\uv.exe", ["uv"] = "/usr/bin/uv",
        };
        public string? CcSecrets { get; set; } = @"C:\tools\cc-secrets.exe";
        public CheckProcessResult List { get; set; } = new(null, 0, false, ListJson(), "");
        public Func<CheckProcessStart, CheckProcessResult> Check { get; set; } =
            _ => new CheckProcessResult(null, 0, false, "{\"count\": 0}", "");
        public List<CheckProcessStart> Runs { get; } = new();
        public List<string> Looked { get; } = new();

        public string? FindOnPath(string name)
        {
            Looked.Add(name);
            return OnPath.GetValueOrDefault(name);
        }

        public string? FindCcSecrets() => CcSecrets;

        public Task<CheckProcessResult> RunAsync(CheckProcessStart start, CancellationToken ct)
        {
            Runs.Add(start);
            return Task.FromResult(start.Arguments.SequenceEqual(new[] { "list", "--json" }) ? List : Check(start));
        }

        public CheckProcessStart TheCheck => Assert.Single(Runs, r => !r.Arguments.SequenceEqual(new[] { "list", "--json" }));
        public int Listings => Runs.Count(r => r.Arguments.SequenceEqual(new[] { "list", "--json" }));
    }

    private string VersionFolder(int version = 3) => Path.Combine(_root, "factory-checks", Factory, version.ToString());
    private string CheckFile(int version = 3) => Path.Combine(VersionFolder(version), "checks", "new_mail.py");

    // ---------- 2.2: the version folder ----------

    [Fact]
    public async Task RunAsync_NoSecretsNoPackages_RunsPythonOnTheCheckInTheVersionFolder()
    {
        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Null(report.StartError);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("{\"count\": 0}", report.Output);
        var run = _processes.TheCheck;
        Assert.Equal(@"C:\Python\python.exe", run.FileName);
        Assert.Equal(new[] { CheckFile() }, run.Arguments);
        Assert.Equal(VersionFolder(), run.WorkingDirectory);
        Assert.Equal(120, run.TimeoutSeconds);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(VersionFolder(), "files", "mail-rules.json")));
        Assert.Equal(0, _processes.Listings);
    }

    [Fact]
    public async Task RunAsync_TheSameVersionTwice_IsDownloadedOnce()
    {
        var runner = Runner();
        await runner.RunAsync(Check(), CancellationToken.None);
        await runner.RunAsync(Check(), CancellationToken.None);

        Assert.Equal((Director, Factory, 3), Assert.Single(_source.Fetches));
    }

    [Fact]
    public async Task RunAsync_TheGatewayRefusesTheDownload_StartErrorQuotesItAndNothingRuns()
    {
        _source.Refusal = "HTTP 404: version 3 of the definition of devthrottle is a draft, not published";

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal("version 3 of the checks of devthrottle could not be downloaded: HTTP 404: version 3 of the " +
                     "definition of devthrottle is a draft, not published", report.StartError);
        Assert.Empty(_processes.Runs);
        Assert.False(Directory.Exists(VersionFolder()));
    }

    [Theory]
    [InlineData("seats/boss.yaml")]
    [InlineData("checks/../../escape.py")]
    [InlineData("files/a\\b.txt")]
    public async Task RunAsync_TheGatewaySendsAPathOutsideChecksAndFiles_NothingIsWrittenAndNothingRuns(string path)
    {
        _source.Files[path] = "x";

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.StartsWith("version 3 of the checks of devthrottle was not written: the Gateway sent the path", report.StartError);
        Assert.Empty(_processes.Runs);
        Assert.False(Directory.Exists(VersionFolder()));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "factory-checks", Factory)));
    }

    [Fact]
    public async Task RunAsync_TheCheckIsNotInTheVersion_StartError()
    {
        _source.Files.Remove(CheckPath);

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal("the check checks/new_mail.py is not in version 3 of the definition of devthrottle", report.StartError);
        Assert.Empty(_processes.Runs);
    }

    [Fact]
    public async Task Prune_AVersionNoAssignmentNames_IsDeleted_AndTheAssignedOneIsKept()
    {
        var runner = Runner();
        await runner.RunAsync(Check(version: 2), CancellationToken.None);
        await runner.RunAsync(Check(version: 3), CancellationToken.None);

        runner.Prune(new[] { Check(version: 3), new TriggerAssignmentDto { Id = "cmd", CheckCommand = "x", RepoPath = "." } });

        Assert.False(Directory.Exists(VersionFolder(2)));
        Assert.True(Directory.Exists(VersionFolder(3)));
    }

    [Fact]
    public async Task Prune_WhileACheckRuns_ItsFolderAndItsStateAreKept()
    {
        var runner = Runner();
        _processes.Check = start =>
        {
            runner.Prune(Array.Empty<TriggerAssignmentDto>());
            Assert.True(Directory.Exists(VersionFolder()));
            Assert.True(File.Exists(start.Environment![DefinitionCheckRunner.StateFileVariable]));
            return new CheckProcessResult(null, 0, false, "{\"count\": 0}", "");
        };

        var report = await runner.RunAsync(Check(), CancellationToken.None);

        Assert.Null(report.StartError);
        runner.Prune(Array.Empty<TriggerAssignmentDto>());
        Assert.False(Directory.Exists(VersionFolder()));
    }

    // ---------- 2.3: Python or uv ----------

    [Fact]
    public async Task RunAsync_TheHeaderDeclaresPackages_RunsUnderUvRunScript()
    {
        _source.Files[CheckPath] = "# /// script\n# dependencies = [\"httpx\"]\n# ///\nprint('{\"count\": 0}')\n";

        await Runner().RunAsync(Check(), CancellationToken.None);

        var run = _processes.TheCheck;
        Assert.Equal(@"C:\tools\uv.exe", run.FileName);
        Assert.Equal(new[] { "run", "--script", CheckFile() }, run.Arguments);
    }

    [Fact]
    public async Task RunAsync_PackagesDeclaredAndNoUv_StartErrorAndNoOtherInterpreterIsTried()
    {
        _source.Files[CheckPath] = "# /// script\n# dependencies = [\"httpx\"]\n# ///\n";
        _processes.OnPath.Remove("uv.exe");

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal("the check declares packages and uv is not on this machine", report.StartError);
        Assert.Empty(_processes.Runs);
        Assert.Equal(new[] { "uv.exe" }, _processes.Looked);
    }

    [Theory]
    [InlineData(true, "python.exe")]
    [InlineData(false, "python3")]
    public async Task RunAsync_NoPython_StartErrorNamesWhatItLookedFor(bool isWindows, string name)
    {
        _processes.OnPath.Remove(name);

        var report = await Runner(isWindows).RunAsync(Check(), CancellationToken.None);

        Assert.Equal($"no Python on this machine (looked for {name} on PATH)", report.StartError);
        Assert.Empty(_processes.Runs);
    }

    [Fact]
    public async Task RunAsync_AHeaderThatNeverCloses_StartError()
    {
        _source.Files[CheckPath] = "# /// script\n# dependencies = []\nprint(1)\n";

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.StartsWith("checks/new_mail.py: the check's script header opened with '# /// script' on line 1", report.StartError);
        Assert.Empty(_processes.Runs);
    }

    // ---------- 2.4: secrets ----------

    [Fact]
    public async Task RunAsync_OneSecret_RunsUnderCcSecretsViaEnvInTheEntrysOwnVariable()
    {
        _processes.List = new(null, 0, false,
            ListJson(("laptop-2-rustdesk", "LAPTOP2_RUSTDESK_PASSWORD", Run), ("unrelated", "UNRELATED", Run)), "");

        var report = await Runner().RunAsync(Check(secrets: "laptop-2-rustdesk"), CancellationToken.None);

        Assert.Null(report.StartError);
        var run = _processes.TheCheck;
        Assert.Equal(@"C:\tools\cc-secrets.exe", run.FileName);
        Assert.Equal(new[] { "run", "laptop-2-rustdesk", "--via", "env", "--env-name", "LAPTOP2_RUSTDESK_PASSWORD", "--",
            @"C:\Python\python.exe", CheckFile() }, run.Arguments);
        Assert.Equal("{\"laptop-2-rustdesk\":\"LAPTOP2_RUSTDESK_PASSWORD\"}", run.Environment![DefinitionCheckRunner.SecretVarsVariable]);
        Assert.Equal(1, _processes.Listings);
    }

    [Fact]
    public async Task RunAsync_OneSecretWithNoVariableOfItsOwn_ArrivesInTheRuleName()
    {
        _processes.List = new(null, 0, false, ListJson(("cloudflare-account-id", "", Run)), "");

        await Runner().RunAsync(Check(secrets: "cloudflare-account-id"), CancellationToken.None);

        Assert.Equal(new[] { "run", "cloudflare-account-id", "--via", "env", "--env-name", "CLOUDFLARE_ACCOUNT_ID", "--",
            @"C:\Python\python.exe", CheckFile() }, _processes.TheCheck.Arguments);
    }

    [Fact]
    public async Task RunAsync_SeveralSecrets_UseWith_AndEachArrivesInItsOwnVariable()
    {
        _processes.List = new(null, 0, false, ListJson(
            ("outreach-mail-client-secret", "OUTREACH_MAIL_CLIENT_SECRET", Run),
            ("laptop-2-rustdesk", "LAPTOP2_RUSTDESK_PASSWORD", Run),
            ("dot.name-entry", "", Run),
            ("not-declared", "NOT_DECLARED", Run)), "");

        await Runner().RunAsync(Check(secrets: new[] { "outreach-mail-client-secret", "laptop-2-rustdesk", "dot.name-entry" }),
            CancellationToken.None);

        var run = _processes.TheCheck;
        Assert.Equal(new[] { "run", "outreach-mail-client-secret", "--with", "laptop-2-rustdesk", "--with", "dot.name-entry",
            "--", @"C:\Python\python.exe", CheckFile() }, run.Arguments);
        Assert.DoesNotContain("not-declared", run.Arguments);
        var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(run.Environment![DefinitionCheckRunner.SecretVarsVariable])!;
        Assert.Equal(new Dictionary<string, string>
        {
            ["outreach-mail-client-secret"] = "OUTREACH_MAIL_CLIENT_SECRET",
            ["laptop-2-rustdesk"] = "LAPTOP2_RUSTDESK_PASSWORD",
            ["dot.name-entry"] = "DOT_NAME_ENTRY",
        }, vars);
        Assert.Equal(1, _processes.Listings);
    }

    [Fact]
    public async Task RunAsync_ASecretThisMachineDoesNotHold_StartErrorNamesItAndTheCheckNeverRuns()
    {
        _processes.List = new(null, 0, false, ListJson(("posthog-api-host", "POSTHOG_API_HOST", Run)), "");

        var report = await Runner().RunAsync(Check(secrets: new[] { "posthog-api-host", "outreach-mail-client-secret" }),
            CancellationToken.None);

        Assert.Equal("No secret named 'outreach-mail-client-secret' is on this machine.", report.StartError);
        Assert.Equal(1, _processes.Listings);
        Assert.Single(_processes.Runs);
    }

    [Fact]
    public async Task RunAsync_ASecretAllowedForLoginOnly_StartErrorNamesItAndTheCheckNeverRuns()
    {
        _processes.List = new(null, 0, false, ListJson(("website-login", "WEBSITE_LOGIN", new[] { "login" })), "");

        var report = await Runner().RunAsync(Check(secrets: "website-login"), CancellationToken.None);

        Assert.Equal("The secret 'website-login' on this machine is not allowed for run (it is allowed for: login).", report.StartError);
        Assert.Single(_processes.Runs);
    }

    [Fact]
    public async Task RunAsync_TwoSecretsThatWouldArriveInOneVariable_StartError()
    {
        _processes.List = new(null, 0, false, ListJson(("a-key", "SHARED", Run), ("b-key", "SHARED", Run)), "");

        var report = await Runner().RunAsync(Check(secrets: new[] { "a-key", "b-key" }), CancellationToken.None);

        Assert.Equal("the secrets 'a-key' and 'b-key' would both arrive in SHARED, so the check could not tell them apart", report.StartError);
    }

    [Fact]
    public async Task RunAsync_SecretsDeclaredAndNoCcSecrets_StartError()
    {
        _processes.CcSecrets = null;

        var report = await Runner().RunAsync(Check(secrets: "a-key"), CancellationToken.None);

        Assert.Equal("the check declares secrets and cc-secrets is not on this machine", report.StartError);
        Assert.Empty(_processes.Runs);
    }

    [Fact]
    public async Task RunAsync_CcSecretsListFails_StartErrorInItsOwnWords()
    {
        _processes.List = new(null, 1, false, "", "the secrets store is locked\n");

        var report = await Runner().RunAsync(Check(secrets: "a-key"), CancellationToken.None);

        Assert.Equal("cc-secrets list --json failed (exit 1): the secrets store is locked", report.StartError);
    }

    [Fact]
    public async Task RunAsync_CcSecretsListAnswersSomethingElse_StartError()
    {
        _processes.List = new(null, 0, false, "not json", "");

        var report = await Runner().RunAsync(Check(secrets: "a-key"), CancellationToken.None);

        Assert.Equal("cc-secrets list --json answered something that is not its list of entries", report.StartError);
    }

    [Theory]
    [InlineData("laptop-2-rustdesk", "LAPTOP2_RUSTDESK_PASSWORD", "LAPTOP2_RUSTDESK_PASSWORD")]
    [InlineData("cloudflare-account-id", "", "CLOUDFLARE_ACCOUNT_ID")]
    [InlineData("a.b-c", null, "A_B_C")]
    public void VariableFor_OwnVariableElseTheRule(string entry, string? envName, string expected)
        => Assert.Equal(expected, DefinitionCheckRunner.VariableFor(entry, envName));

    // ---------- 2.5: the environment and the state file ----------

    [Fact]
    public async Task RunAsync_TheEnvironment_IsTheBaseAndTheFourCheckVariables()
    {
        await Runner().RunAsync(Check(), CancellationToken.None);

        var env = _processes.TheCheck.Environment!;
        Assert.Equal(new[] { "CC_CHECK_FACTORY", "CC_CHECK_SEAT", "CC_CHECK_SECRET_VARS", "CC_CHECK_STATE_FILE", "HOME", "PATH" },
            env.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(Factory, env["CC_CHECK_FACTORY"]);
        Assert.Equal("outreach", env["CC_CHECK_SEAT"]);
        Assert.Equal("{}", env["CC_CHECK_SECRET_VARS"]);
    }

    [Fact]
    public void SessionInheritedEnvironment_HoldsNothingASessionIsNeverHanded()
        => Assert.DoesNotContain(DefinitionCheckRunner.SessionInheritedEnvironment().Keys, InheritedSessionEnvironment.IsStripped);

    [Fact]
    public async Task RunAsync_FirstRun_TheStateFileExistsAndIsEmpty_AndIsNotInTheCheckFolder()
    {
        string? seen = null;
        _processes.Check = start =>
        {
            var file = start.Environment![DefinitionCheckRunner.StateFileVariable];
            seen = File.ReadAllText(file);
            Assert.False(file.StartsWith(VersionFolder(), StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith(_root, file);
            return new CheckProcessResult(null, 0, false, "{\"count\": 0}", "");
        };

        var report = await Runner().RunAsync(Check(state: null), CancellationToken.None);

        Assert.Equal("", seen);
        Assert.Equal("", report.State);
        Assert.False(report.StateOversize);
    }

    [Fact]
    public async Task RunAsync_StoredState_IsInTheFileBeforeTheRun_AndTheNewStateIsReported_AndTheRunFolderIsGone()
    {
        string? seen = null, file = null;
        _processes.Check = start =>
        {
            file = start.Environment![DefinitionCheckRunner.StateFileVariable];
            seen = File.ReadAllText(file);
            File.WriteAllText(file, "{\"newest\":\"2026-10-15T08:00:00Z\"}");
            return new CheckProcessResult(null, 0, false, "{\"count\": 1}", "");
        };

        var report = await Runner().RunAsync(Check(state: "{\"newest\":\"2026-10-14T08:00:00Z\"}"), CancellationToken.None);

        Assert.Equal("{\"newest\":\"2026-10-14T08:00:00Z\"}", seen);
        Assert.Equal("{\"newest\":\"2026-10-15T08:00:00Z\"}", report.State);
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task RunAsync_TheCheckDeletesItsStateFile_StateIsNull()
    {
        _processes.Check = start =>
        {
            File.Delete(start.Environment![DefinitionCheckRunner.StateFileVariable]);
            return new CheckProcessResult(null, 0, false, "{\"count\": 0}", "");
        };

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Null(report.State);
        Assert.False(report.StateOversize);
    }

    [Theory]
    [InlineData(TriggerCheckLimits.StateCapBytes, false)]
    [InlineData(TriggerCheckLimits.StateCapBytes + 1, true)]
    public async Task RunAsync_StateAbove64KB_IsOversizeAndNotSent(int bytes, bool oversize)
    {
        _processes.Check = start =>
        {
            File.WriteAllText(start.Environment![DefinitionCheckRunner.StateFileVariable], new string('s', bytes));
            return new CheckProcessResult(null, 0, false, "{\"count\": 0}", "");
        };

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal(oversize, report.StateOversize);
        Assert.Equal(oversize ? null : bytes, report.State?.Length);
    }

    [Fact]
    public async Task RunAsync_OutputLongerThanTheCap_IsCutAt16384Characters()
    {
        _processes.Check = _ => new CheckProcessResult(null, 0, false, new string('o', 20_000), new string('e', 20_000));

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal(16_384, report.Output!.Length);
        Assert.Equal(16_384, report.ErrorOutput!.Length);
    }

    [Fact]
    public async Task RunAsync_TheCheckTimesOut_ReportsTheTimeoutAndNoExitCode()
    {
        _processes.Check = _ => new CheckProcessResult(null, null, true, "", "");

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.True(report.TimedOut);
        Assert.Null(report.ExitCode);
        Assert.Null(report.StartError);
    }

    [Fact]
    public async Task RunAsync_ThePythonFoundCannotStart_StartError()
    {
        _processes.Check = _ => CheckProcessResult.CouldNotStart(@"C:\Python\python.exe could not start: access is denied");

        var report = await Runner().RunAsync(Check(), CancellationToken.None);

        Assert.Equal(@"C:\Python\python.exe could not start: access is denied", report.StartError);
    }

    // ---------- the script header ----------

    [Theory]
    [InlineData("print(1)\n", false)]
    [InlineData("# /// script\n# requires-python = \">=3.11\"\n# ///\n", false)]
    [InlineData("# /// script\n# dependencies = []\n# ///\n", false)]
    [InlineData("# /// script\n# dependencies = [\"httpx\"]\n# ///\n", true)]
    [InlineData("# /// script\n# dependencies = [\n#   \"httpx>=0.27\",\n#   \"rich\",\n# ]\n# ///\n", true)]
    [InlineData("# /// script\r\n# dependencies = ['httpx']\r\n# ///\r\n", true)]
    [InlineData("# /// script\n# dependencies = [\n#   # none yet\n# ]\n# ///\n", false)]
    public void CheckScriptHeader_Read_SaysWhetherPackagesAreDeclared(string text, bool declares)
    {
        var reading = CheckScriptHeader.Read(text);
        Assert.Null(reading.Error);
        Assert.Equal(declares, reading.DeclaresPackages);
    }

    // ---------- finding a program on PATH ----------

    [Fact]
    public void FindOnPath_TheWindowsStoreAliasFolder_IsNeverUsed()
    {
        var store = Path.Combine(_root, "Users", "x", "AppData", "Local", "Microsoft", "WindowsApps");
        var real = Path.Combine(_root, "Python");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(store, "python.exe"), "");

        Assert.Null(CheckProcesses.FindOnPath("python.exe", store, ".EXE"));

        File.WriteAllText(Path.Combine(real, "python.exe"), "");
        Assert.Equal(Path.Combine(real, "python.exe"),
            CheckProcesses.FindOnPath("python.exe", store + Path.PathSeparator + real, ".EXE"));
    }
}
