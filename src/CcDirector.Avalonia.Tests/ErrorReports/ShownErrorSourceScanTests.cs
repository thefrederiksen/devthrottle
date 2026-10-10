using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Avalonia.Tests.ErrorReports;

/// <summary>
/// Issue #3675, step 4a: every error the desktop shows reaches the Gateway's error store. That is only true if
/// every place that shows one goes through the reporting helper, so this test DERIVES those places from the
/// source (see <see cref="ShownErrorScanner"/> for exactly what it reads), prints how many it read, and fails on
/// any that neither goes through the helper nor carries a written exemption.
///
/// The second half proves the scanner itself, on small sources written here: a broken site of every kind is
/// caught, an exemption without a reason is caught, and a run that reads nothing fails. A scan nobody has seen
/// fail certifies nothing.
/// </summary>
public sealed class ShownErrorSourceScanTests
{
    // The projects whose processes run an ErrorReporter and show a user interface: the Director and the launcher,
    // and the libraries that draw their screens.
    private static readonly string[] ScannedProjects =
    {
        "src/CcDirector.Avalonia",
        "src/CcDirector.Launcher",
        "src/CcDirector.Terminal.Avalonia",
        "src/CcDirector.TrayUi",
    };

    // The helper itself: the one place that is allowed to open a message box for an error directly.
    private static readonly string[] HelperFiles = { "src/CcDirector.Avalonia/ShownErrorBox.cs" };

    private readonly ITestOutputHelper _output;

    public ShownErrorSourceScanTests(ITestOutputHelper output) => _output = output;

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"could not find the repository root above {AppContext.BaseDirectory}; this test reads the source tree");
    }

    private static Dictionary<string, string> ReadSources()
    {
        var root = RepositoryRoot();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project);
            Assert.True(Directory.Exists(dir), $"scanned project not found: {dir}");
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (HelperFiles.Contains(relative, StringComparer.Ordinal)) continue;
                sources[relative] = File.ReadAllText(file);
            }
        }
        return sources;
    }

    [Fact]
    public void EveryErrorShownOnTheDesktop_GoesThroughTheReportingHelper()
    {
        var result = new ShownErrorScanner().Scan(ReadSources());

        var onHelper = result.Count("on the helper");
        var userInput = result.Count($"exempt: {ShownErrorScanner.UserInputKind}");
        var notAnError = result.Count($"exempt: {ShownErrorScanner.NotAnErrorKind}");
        var reportedAbove = result.Count($"exempt: {ShownErrorScanner.ReportedAboveKind}");
        var violations = result.Violations.ToList();

        _output.WriteLine($"files read: {result.FilesRead}");
        _output.WriteLine($"display sites read: {result.Sites.Count} (message boxes {result.CountKind("message box")}, " +
                          $"control text {result.CountKind("control text")}, error calls {result.CountKind("error call")}, " +
                          $"layout text {result.CountKind("layout text")}, other helper calls {result.CountKind("helper call")})");
        _output.WriteLine($"on the helper: {onHelper}; exempt as user input: {userInput}; exempt as not an error: {notAnError}; second display of a failure reported above: {reportedAbove}; not reported: {violations.Count}");
        foreach (var site in result.Sites.Where(s => s.Status.StartsWith("exempt", StringComparison.Ordinal)))
            _output.WriteLine($"  {site.Status}  {site.File}:{site.Line}  - {site.Reason}");
        foreach (var finding in result.Findings)
            _output.WriteLine($"  FINDING  {finding}");
        foreach (var v in violations)
            _output.WriteLine($"  NOT REPORTED  {v.File}:{v.Line} [{v.Kind}] {v.Text}");

        // A run that reads nothing passes everything (a check whose pass condition is an absence). Each kind of
        // site must have been read, so a rule that silently stopped matching is a red run, not a green one.
        Assert.True(result.FilesRead > 100, $"only {result.FilesRead} files read - the scan is not reading the source");
        Assert.True(result.CountKind("message box") > 0, "no message box read - the message box rule is broken");
        Assert.True(result.CountKind("control text") > 0, "no control text read - the control text rule is broken");
        Assert.True(result.CountKind("error call") > 0, "no error call read - the error call rule is broken");
        Assert.True(result.CountKind("layout text") > 0, "no layout text read - the axaml rule is broken");
        Assert.True(result.Findings.Count == 0, string.Join("\n", result.Findings));
        Assert.True(violations.Count == 0,
            $"{violations.Count} place(s) show the user an error that is never reported. Show it through " +
            "ShownError.Report / ShownErrorBox, or - only for the user's own input, or text that is not an error - " +
            $"put \"// {ShownErrorScanner.ExemptPrefix} (<kind>): <reason>\" on the line above:\n" +
            string.Join("\n", violations.Select(v => $"{v.File}:{v.Line} [{v.Kind}] {v.Text}")));
    }

    private static ShownErrorScanner.Result ScanOne(string code, string? axaml = null)
    {
        var sources = new Dictionary<string, string> { ["src/Fake/FakeView.axaml.cs"] = code };
        if (axaml is not null) sources["src/Fake/FakeView.axaml"] = axaml;
        return new ShownErrorScanner().Scan(sources);
    }

    private const string Head = "using System; class FakeView { ";

    [Fact]
    public void Scan_MessageBoxWithoutTheHelper_IsNotReported()
    {
        var r = ScanOne(Head + "async void M(){ await MessageBox.ShowAsync(this, \"Error\", \"Could not save.\"); } }");
        Assert.Single(r.Violations);
        Assert.Equal("message box", r.Violations.Single().Kind);
    }

    [Fact]
    public void Scan_MessageDialogWithoutTheHelper_IsNotReported()
    {
        var r = ScanOne(Head + "async void M(Exception ex){ await new MessageDialog(\"Cannot open\", ex.Message).ShowDialog<bool?>(this); } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_ErrorTextOnAControl_IsNotReported()
    {
        var r = ScanOne(Head + "void M(Exception ex){ StatusText.Text = $\"Save failed: {ex.Message}\"; } }");
        Assert.Equal("control text", r.Violations.Single().Kind);
    }

    [Fact]
    public void Scan_AnyTextOnAnErrorNamedControl_IsNotReported_ButClearingItIsNoSite()
    {
        var r = ScanOne(Head + "void M(string m){ ErrorText.Text = m; } void C(){ ErrorText.Text = \"\"; } }");
        Assert.Single(r.Sites);
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_ErrorTextSplitOverLinesInAShowCall_IsNotReported()
    {
        var r = ScanOne(Head + "void M(Exception ex){ ShowNotification(\n  \"The log \" +\n  $\"could not be sent: {ex.Message}\"); } }");
        Assert.Equal("error call", r.Violations.Single().Kind);
    }

    [Fact]
    public void Scan_ResultSetters_LogPanes_AndNotifications_AreSites_ButFailureCallbacksAreNot()
    {
        var r = ScanOne(Head + "void M(Exception ex){ SetDetectResult($\"Detection failed: {ex.Message}\", success: false);" +
            " AppendLog($\"ERROR: could not start: {ex.Message}\"); Notified?.Invoke(this, $\"Could not read: {ex.Message}\");" +
            " onPlaybackFailed?.Invoke(\"x\"); SetDetectResult(\"Detecting...\", success: false); } }");
        // Three sites: the result setter, the log pane, the notification. onPlaybackFailed is a callback, not a screen.
        Assert.Equal(3, r.Violations.Count());
    }

    [Fact]
    public void Scan_ACallToAnErrorNamedMethod_IsASiteWhateverItsText_AndItsBodyIsNotCountedAgain()
    {
        var r = ScanOne(Head + "void A(){ ShowError(\"Enter a name.\"); } void ShowError(string m){ ErrorText.Text = m; } }");
        Assert.Single(r.Sites);
        Assert.Equal("error call", r.Violations.Single().Kind);
    }

    [Fact]
    public void Scan_AShowMethodThatShowsTheTextItIsGiven_IsCountedAtItsCallers()
    {
        var r = ScanOne(Head + "void ShowNotice(string text){ NativeNotice.Show(text, \"t\", K.Error); }" +
            " void A(Exception ex){ ShowNotice($\"Could not start: {ex.Message}\"); } }");
        Assert.Single(r.Sites);
        Assert.StartsWith("ShowNotice(", r.Violations.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_StatusWithErrorTrue_IsASite()
    {
        var r = ScanOne(Head + "void A(){ ShowFirewallStatus(\"Approval was declined.\", error: true); } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_TheHelperInsideTheSite_IsOnTheHelper()
    {
        var r = ScanOne(Head + "void M(Exception ex){ StatusText.Text = ShownError.Report(\"settings\", \"save\", $\"Save failed: {ex.Message}\", ex); } }");
        Assert.Equal("on the helper", r.Sites.Single().Status);
        Assert.Empty(r.Violations);
    }

    [Fact]
    public void Scan_AWrapperThatReports_CoversItsCallers_AndCallbacksGivenToIt()
    {
        var r = ScanOne(Head +
            "void ShowFailure(string action, string m, Exception ex){ ErrorText.Text = ShownError.Report(\"s\", action, m, ex); }" +
            "void A(Exception ex){ ShowStatus(\"x\"); ShowFailure(\"save\", $\"Save failed: {ex.Message}\", ex); }" +
            "void B(){ Run(m => ShowStatus($\"stop failed: {m}\")); } void Run(Action<string> a){ ShownError.Report(\"s\",\"a\",\"t\"); } }");
        Assert.Empty(r.Violations);
        Assert.Contains(r.Sites, s => s.Text.StartsWith("ShowFailure(", StringComparison.Ordinal) && s.Status == "on the helper");
        Assert.Contains(r.Sites, s => s.Text.StartsWith("ShowStatus($\"stop failed", StringComparison.Ordinal) && s.Status == "on the helper");
    }

    [Fact]
    public void Scan_ShowingALocalTheHelperReturned_IsOnTheHelper_ButAnyOtherLocalIsNot()
    {
        var reported = ScanOne(Head + "void M(Exception ex){ var notice = ShownError.Report(\"s\", \"start\", \"Failed to start\", ex); Flush(); NativeNotice.Show(notice, \"t\", K.Error); } }");
        Assert.Empty(reported.Violations);
        var plain = ScanOne(Head + "void M(Exception ex){ var notice = $\"Failed: {ex.Message}\"; NativeNotice.Show(notice, \"t\", K.Error); } }");
        Assert.Single(plain.Violations);
    }

    [Fact]
    public void Scan_AFieldThatOnlyEverHoldsReportedText_IsOnTheHelper_ButOneUnreportedWriteSpoilsIt()
    {
        const string reported = "string? _err; void A(Exception ex){ _err = ShownError.Report(\"s\", \"a\", ex.Message, ex); } void C(){ _err = null; }";
        var good = ScanOne(Head + reported + " void D(){ ErrorText.Text = _err ?? \"\"; } }");
        Assert.Empty(good.Violations);
        var bad = ScanOne(Head + reported + " void B(Exception ex){ _err = ex.Message; } void D(){ ErrorText.Text = _err ?? \"\"; } }");
        Assert.Single(bad.Violations);
    }

    // ----- the shapes review finding F1 named: each hid a real error the user saw -----

    [Fact]
    public void Scan_ACallNamedForAFailure_WithoutAShowPrefix_IsASite()
    {
        // ColourLegendDialog: Fail("...") put its argument on the status line in amber.
        var r = ScanOne(Head + "void L(){ Fail(\"The Gateway could not be asked.\"); } void Fail(string m){ StatusText.Text = m; } }");
        Assert.Equal("error call", Assert.Single(r.Violations).Kind);
    }

    [Fact]
    public void Scan_AFailureFactoryOnAType_OrACallWithNothingToShow_IsNotASite()
    {
        var r = ScanOne(Head + "object M(){ ClearGatewayFailure(); item.SetFailed(); return OperationResult<int>.Fail(\"could not join\"); } }");
        Assert.Empty(r.Sites);
    }

    [Fact]
    public void Scan_ANamedFailureMember_CarriesErrorText()
    {
        // DirectorTeamPanel: ShowStatus(moved.ErrorMessage ?? "The move did not happen.", red).
        var r = ScanOne(Head + "void M(){ ShowStatus(moved.ErrorMessage ?? \"The move did not happen.\", \"#F14C4C\"); } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_AnEnumValueOrAFlag_NamedForAFailure_IsNotErrorText()
    {
        var r = ScanOne(Head + "bool _gatewayError; void M(){ ShowStatus(StatusLevel.Error); SetStatus(_gatewayError); } }");
        Assert.Empty(r.Sites);
    }

    [Fact]
    public void Scan_ALocalGivenErrorText_ThenShown_IsASite_UnlessWhatItWasGivenWasReported()
    {
        // WorktreesView: parts.Add("Could NOT fully delete ..."), then ShowBanner(string.Join(.., parts)).
        const string bad = "void M(){ var parts = new List<string>(); parts.Add(\"Could NOT fully delete 2 folders\"); ShowBanner(string.Join(\" \", parts)); } }";
        Assert.Single(ScanOne(Head + bad).Violations);
        const string good = "void M(){ var parts = new List<string>(); parts.Add(ShownError.Report(\"s\", \"a\", \"Could NOT fully delete 2 folders\")); ShowBanner(string.Join(\" \", parts)); } }";
        Assert.Empty(ScanOne(Head + good).Violations);
    }

    [Fact]
    public void Scan_AFieldGivenErrorText_ShownElsewhereInTheClass_IsASite()
    {
        // SaveWorkspaceDialog: _existingIdsProblem = "Could not read ..." in a catch, shown later on TxtWarning.
        var r = ScanOne(Head + "string? _existing; void L(Exception ex){ _existing = \"Could not read the workspaces\"; } void S(){ TxtWarning.Text = _existing; } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_AValueTheMethodLogsAsFailed_IsErrorText()
    {
        // BrowserSettingsView: StatusText.Text = result.Message beside FileLog.Write($"... FAILED: {result.Message}").
        var r = ScanOne(Head + "void M(){ StatusText.Text = result.Message; if (!result.Success) FileLog.Write($\"[V] M FAILED: {result.Message}\"); } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_TextInsideAThrowOrAPattern_IsNotShownText()
    {
        var r = ScanOne(Head + "void M(string s){ var exe = s ?? throw new Exception(\"Cannot find the program\"); Label.Text = exe;" +
                        " var colour = s switch { \"Failed\" => \"red\", _ => \"grey\" }; Dot.Text = colour; } }");
        Assert.Empty(r.Sites);
    }

    [Fact]
    public void Scan_AnExemptionNotOnTheLineDirectlyAbove_DoesNotExempt()
    {
        var r = ScanOne(Head + "void M(){\n // shown-error-exempt (user input): the name the user typed is empty\n\n // something else\n NameError.Text = \"Name cannot be empty\"; } }");
        Assert.Single(r.Violations);
        Assert.Contains(r.Findings, f => f.Contains("no display site uses"));
    }

    [Fact]
    public void Scan_AWrapperOnAnotherInstance_IsNotTrustedToReport()
    {
        var r = ScanOne(Head + "void M(Exception ex){ viewer.ShowLoadError(ex.Message); } }");
        Assert.Single(r.Violations);
    }

    [Fact]
    public void Scan_ExemptionWithAReason_IsExempt()
    {
        var r = ScanOne(Head + "void M(){\n// shown-error-exempt (user input): the name box is empty, the user's own entry\nShowError(\"Enter a name.\"); } }");
        Assert.Equal("exempt: user input", r.Sites.Single().Status);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Scan_ExemptionWithoutAReason_OrOfAnUnknownKind_IsAFinding()
    {
        var noReason = ScanOne(Head + "void M(){\n// shown-error-exempt (user input): empty\nShowError(\"Enter a name.\"); } }");
        Assert.Single(noReason.Findings);
        var badKind = ScanOne(Head + "void M(){\n// shown-error-exempt (too noisy): it fires all the time, nobody cares\nShowError(\"x\"); } }");
        Assert.Single(badKind.Findings);
    }

    [Fact]
    public void Scan_ReportedAbove_IsCheckedAgainstTheBlock()
    {
        var good = ScanOne(Head + "void M(Exception ex){ A.Text = ShownError.Report(\"s\", \"a\", \"Could not resume\", ex);\n" +
            "// shown-error-exempt (reported above): the advice under the label the line before reported\nB.Text = \"Could not resume - try again\"; } }");
        Assert.Empty(good.Findings);
        Assert.Equal("exempt: reported above", good.Sites.Single(x => x.Text.StartsWith("B.Text", StringComparison.Ordinal)).Status);

        var bad = ScanOne(Head + "void M(){\n// shown-error-exempt (reported above): the advice under the label the line before reported\nB.Text = \"Could not resume - try again\"; } }");
        Assert.Single(bad.Findings);
    }

    [Fact]
    public void Scan_AnExemptionNoSiteUses_IsAFinding()
    {
        var r = ScanOne(Head + "void M(){\n// shown-error-exempt (user input): left behind after the site moved away\nvar x = 1; } }");
        Assert.Single(r.Findings);
    }

    [Fact]
    public void Scan_LayoutErrorText_MustNameAReportingMethod()
    {
        const string code = Head + "void ShowFailure(string a, string m){ T.Text = ShownError.Report(\"s\", a, m); } void Other(){} }";
        var unmarked = ScanOne(code, "<StackPanel>\n  <TextBlock Text=\"Could not connect\" />\n</StackPanel>");
        Assert.Single(unmarked.Violations);

        var marked = ScanOne(code, "<StackPanel>\n  <!-- shown-error-reported-by: ShowFailure -->\n  <TextBlock Text=\"Could not connect\" />\n</StackPanel>");
        Assert.Empty(marked.Violations);

        var wrongMethod = ScanOne(code, "<StackPanel>\n  <!-- shown-error-reported-by: Other -->\n  <TextBlock Text=\"Could not connect\" />\n</StackPanel>");
        Assert.Single(wrongMethod.Violations);
    }

    [Fact]
    public void Scan_TextBoundInLayout_IsReadThroughItsProperty()
    {
        var r = ScanOne(Head + "string StatusMessage {get;set;} void M(){ StatusMessage = \"Failed to approve item\"; } }",
            "<TextBlock Text=\"{Binding StatusMessage}\" />");
        Assert.Equal("control text", r.Violations.Single().Kind);
    }

    [Fact]
    public void Scan_OrdinaryTextAndLogLines_AreNotSites()
    {
        var r = ScanOne(Head + "void M(Exception ex){ StatusText.Text = \"Saved.\"; FileLog.Write($\"[X] M FAILED: {ex}\"); ShowStatus(\"Connected.\"); } }");
        Assert.Empty(r.Sites);
    }
}
