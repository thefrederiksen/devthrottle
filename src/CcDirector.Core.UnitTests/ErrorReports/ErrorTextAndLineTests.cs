using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3311: what may leave a user's machine in an error report, and which log lines are errors.
/// </summary>
public sealed class ErrorTextAndLineTests
{
    [Theory]
    [InlineData("/Users/robert/Library/Application Support/cc-director/logs", "~/Library/Application Support/cc-director/logs")]
    [InlineData("/home/soren/.local/share/cc-director", "~/.local/share/cc-director")]
    [InlineData(@"C:\Users\soren\AppData\Local\cc-director", @"~\AppData\Local\cc-director")]
    public void Scrub_HomeFolder_BecomesTilde(string input, string expected)
        => Assert.Equal(expected, ErrorTextScrubber.Scrub(input));

    [Theory]
    [InlineData("Authorization: Bearer dt_live_abcdef0123456789")]
    [InlineData("request failed token=abc123secretvalue&x=1")]
    [InlineData("password: hunter2")]
    [InlineData("key AKIAabcdEFGHijklMNOPqrstUVWX0123456 leaked")]
    public void Scrub_CredentialShapedValue_IsRedacted(string input)
    {
        var scrubbed = ErrorTextScrubber.Scrub(input);

        Assert.Contains(ErrorTextScrubber.Redacted, scrubbed);
        Assert.DoesNotContain("dt_live_abcdef", scrubbed);
        Assert.DoesNotContain("abc123secretvalue", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("AKIAabcdEFGHijklMNOPqrstUVWX0123456", scrubbed);
    }

    [Fact]
    public void Scrub_Guid_IsKept()
    {
        // Session and Director ids are what make an error traceable; a GUID has hyphens and is not a key.
        const string text = "session 74ac7657-0181-4153-b5f7-bdea8da223ad not found";

        Assert.Equal(text, ErrorTextScrubber.Scrub(text));
    }

    [Fact]
    public void Scrub_EveryKeyThisProductMints_IsRedacted()
    {
        // The product's own key shape: 43 characters of URL-safe base64, most with a hyphen in them - which a
        // rule that stopped at the first hyphen let through three times in four.
        // Ten thousand keys, so a rule that misses one key in a few thousand fails here every run rather than
        // one run in four (review round 2 measured the first rule at one miss in 1,513).
        for (var i = 0; i < 10_000; i++)
        {
            var key = CcDirector.Core.Security.GatewaySessionKey.Mint();

            var scrubbed = ErrorTextScrubber.Scrub($"connect FAILED with {key} at the far end");

            Assert.DoesNotContain(key, scrubbed);
            Assert.Contains(ErrorTextScrubber.Redacted, scrubbed);
        }
    }

    [Theory]
    [InlineData(@"D:\ReposFred\devthrottle-dev-reports-p2-gateway\src")]
    [InlineData("branch feat/centralized-error-logging-3311-and-more-words")]
    [InlineData("fleet-3fe6711b-da67-4315-925e-105a7992bdd7 is gone")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01 is the commit")]
    // A long method name in a STACK FRAME is kept: the runtime writes frames from our own names, never data.
    [InlineData("boom\n   at CcDirector.Core.InitializeServicesAndShowTheMainWindowAsync()")]
    public void Scrub_LongFolderBranchAndIdNames_AreKept(string text)
        => Assert.Equal(text, ErrorTextScrubber.Scrub(text));

    [Fact]
    public void Scrub_ALongMixedCaseRunInAMessage_IsRedacted()
    {
        // The accepted cost of the key rule, pinned so it is a decision and not a surprise.
        Assert.Equal("value " + ErrorTextScrubber.Redacted + " rejected",
            ErrorTextScrubber.Scrub("value MaxReportsPerDevicePerHourAndSomeMoreWords rejected"));
    }

    [Fact]
    public void Clean_DropsControlCharactersAndCaps()
    {
        var cleaned = ErrorTextScrubber.Clean("a\u0007b\nc\td-" + string.Join(" ", Enumerable.Repeat("xx", 50)), 10);

        Assert.Equal("ab\nc\td-xx", cleaned);
    }

    [Theory]
    [InlineData("[SessionManager] CreateSession FAILED: access denied", true)]
    [InlineData("[App] UNHANDLED UI-THREAD EXCEPTION: System.NullReferenceException", true)]
    [InlineData("[Program] UNOBSERVED TASK: System.AggregateException", true)]
    [InlineData("[Program] FATAL: boom", true)]
    // The marker after the first ": ", in the verdict position - real Director lines (review round 2).
    [InlineData("[DirectorRestore] seat x: PassDevReportsAsync FAILED: timeout", true)]
    [InlineData("[ToolAutoUpdate] x: tool reconcile FAILED (ignored): y", true)]
    [InlineData("[MainWindow] InstanceTitleSuffix: registry read FAILED, falling back to none", true)]
    [InlineData("[GatewayClient] heartbeat ok", false)]
    [InlineData("[Updater] failed over to the second mirror", false)]
    [InlineData("[ErrorReporter] 3 report(s) not delivered: FAILED to connect", false)]
    // A marker word in the DETAIL is interpolated text, not the line's verdict: it must not make a report.
    [InlineData("[WingmanActionExecutor] performed Summarise: the build FAILED and an ERROR followed", false)]
    [InlineData("[GatewayRegistrationClient] registered: body={\"status\":\"FATAL\"}", false)]
    [InlineData("", false)]
    public void IsError_RecognisesTheHouseErrorForms(string line, bool expected)
        => Assert.Equal(expected, ErrorLine.IsError(line));

    [Fact]
    public void Parse_LineWithWholeException_SplitsSourceMessageTypeAndStack()
    {
        var line = "[SessionManager] CreateSession FAILED: System.IO.IOException: The disk is full\n"
                   + "   at CcDirector.Core.Sessions.SessionManager.Create()\n"
                   + "   at CcDirector.Core.Sessions.SessionManager.Start()";

        var (source, message, type, stack) = ErrorLine.Parse(line);

        Assert.Equal("SessionManager", source);
        Assert.Equal("CreateSession FAILED: System.IO.IOException: The disk is full", message);
        Assert.Equal("System.IO.IOException", type);
        Assert.StartsWith("   at CcDirector.Core.Sessions.SessionManager.Create()", stack);
    }

    [Theory]
    [InlineData("[App] UNHANDLED UI-THREAD EXCEPTION: x", "ui-thread")]
    [InlineData("[Program] UNOBSERVED TASK: x", "unobserved-task")]
    [InlineData("[Program] UNHANDLED (terminating): x", "unhandled")]
    [InlineData("[X] Save FAILED: x", "logged")]
    public void KindOf_NamesTheKind(string line, string expected)
        => Assert.Equal(expected, ErrorLine.KindOf(line));

    [Fact]
    public void MachineId_IsAStableHashNeverTheName()
    {
        var a = ErrorReportMachineId.Of("devthrottle-mac-mini");
        var b = ErrorReportMachineId.Of("DEVTHROTTLE-MAC-MINI");

        Assert.Equal(a, b);
        Assert.True(ErrorReportMachineId.IsHash(a));
        Assert.DoesNotContain("mac", a);
        Assert.NotEqual(a, ErrorReportMachineId.Of("SOREN_NORTH"));
    }
}
