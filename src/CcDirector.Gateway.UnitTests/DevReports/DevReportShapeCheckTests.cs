using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// The dev report shape check (issue #2940): a good report passes, and every rule has a report that breaks
/// exactly that rule and fails with an error naming it. Each failing case starts from the good report and
/// changes one thing, so a test that goes green for the wrong reason would also have to pass the good one.
/// </summary>
public sealed class DevReportShapeCheckTests
{
    private const string Question = """
        <div data-dev-report-question="rerun">
          <h3>Rerun tonight?</h3>
          <label><input type="radio" name="rerun" value="yes" data-recommended> Yes</label>
          <label><input type="radio" name="rerun" value="no"> No</label>
        </div>
        """;

    private static string Report(
        string status = "waiting-on-you",
        string? headerAttrs = null,
        string questionsBody = Question,
        string? afterHeader = null,
        string? beforeHeader = null,
        string? tail = null,
        bool summary = true,
        bool questions = true,
        bool detail = true,
        bool evidence = true)
    {
        var header = $"<header data-dev-report=\"header\" {headerAttrs ?? $"data-dev-report-status=\"{status}\""}><h1>Report</h1></header>";
        return "<!doctype html><html><body><main>" +
               (beforeHeader ?? "") +
               header +
               (afterHeader ?? "") +
               (summary ? "<section data-dev-report=\"summary\"><p>Summary.</p></section>" : "") +
               (questions ? $"<section data-dev-report=\"questions\">{questionsBody}</section>" : "") +
               (detail ? "<section data-dev-report=\"detail\"><p>Detail.</p></section>" : "") +
               (evidence ? "<section data-dev-report=\"evidence\"><p>Not proven: nothing.</p></section>" : "") +
               (tail ?? "") +
               "</main></body></html>";
    }

    private static void AssertFailsWith(string html, string fragment)
    {
        var verdict = DevReportShapeCheck.Check(html);
        Assert.False(verdict.Passed, "The report passed, but it breaks the rule: " + fragment);
        Assert.True(verdict.Errors.Any(e => e.Contains(fragment)),
            $"No error mentions \"{fragment}\". Errors were:\n{string.Join("\n", verdict.Errors)}");
    }

    [Fact]
    public void Check_GoodReport_Passes()
    {
        var verdict = DevReportShapeCheck.Check(Report());
        Assert.Empty(verdict.Errors);
        Assert.True(verdict.Passed);
        Assert.Equal("waiting-on-you", verdict.Status);
    }

    [Theory]
    [InlineData("waiting-on-you")]
    [InlineData("agent-working")]
    [InlineData("done")]
    public void Check_EachAllowedStatus_Passes(string status)
    {
        var verdict = DevReportShapeCheck.Check(Report(status: status));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
        Assert.Equal(status, verdict.Status);
    }

    [Fact]
    public void Check_NoQuestionsButSaysSo_Passes()
    {
        var html = Report(questionsBody: "<p data-dev-report-no-questions>No questions - nothing needed from you.</p>");
        var verdict = DevReportShapeCheck.Check(html);
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    [Fact]
    public void Check_EvidenceIsOptional_Passes()
    {
        Assert.True(DevReportShapeCheck.Check(Report(evidence: false)).Passed);
    }

    [Fact]
    public void Check_TheSampleReportUsedByTheBrowserProof_Passes()
    {
        var path = Path.Combine(RepoRoot(), "packages", "client-core", "browser-tests", "dev-report-notes-proof", "sample-report.html");
        var verdict = DevReportShapeCheck.Check(File.ReadAllText(path));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    // --- the header and its status -------------------------------------------------------------------

    [Fact]
    public void Check_NoHeader_Fails()
        => AssertFailsWith(Report().Replace("data-dev-report=\"header\"", ""), "has no header");

    [Fact]
    public void Check_TwoHeaders_Fails()
        => AssertFailsWith(Report(tail: "<header data-dev-report=\"header\" data-dev-report-status=\"done\"></header>"), "2 headers");

    [Fact]
    public void Check_HeaderWithoutStatus_Fails()
        => AssertFailsWith(Report(headerAttrs: ""), "header has no status");

    [Fact]
    public void Check_HeaderWithUnknownStatus_Fails()
    {
        AssertFailsWith(Report(status: "finished"), "status \"finished\" is not allowed");
        Assert.Null(DevReportShapeCheck.Check(Report(status: "finished")).Status);
    }

    [Fact]
    public void Check_SectionBeforeHeader_Fails()
        => AssertFailsWith(Report(beforeHeader: "<section data-dev-report=\"detail\"></section>"), "header must come before every other section");

    // --- the summary first -----------------------------------------------------------------------------

    [Fact]
    public void Check_NoSummary_Fails()
        => AssertFailsWith(Report(summary: false), "has no executive summary");

    [Fact]
    public void Check_DetailBetweenHeaderAndSummary_Fails()
        => AssertFailsWith(Report(afterHeader: "<section data-dev-report=\"detail\"></section>"), "executive summary must come right after the header");

    [Fact]
    public void Check_TwoSummaries_Fails()
        => AssertFailsWith(Report(tail: "<section data-dev-report=\"summary\"></section>"), "2 of data-dev-report=\"summary\"");

    // --- the questions right after -------------------------------------------------------------------

    [Fact]
    public void Check_NoQuestionsSection_Fails()
        => AssertFailsWith(Report(questions: false), "has no questions section");

    [Fact]
    public void Check_QuestionsAfterDetail_Fails()
    {
        var html = Report(questions: false, tail: $"<section data-dev-report=\"questions\">{Question}</section>");
        AssertFailsWith(html, "questions section must come right after the summary");
    }

    [Fact]
    public void Check_EmptyQuestionsSection_Fails()
        => AssertFailsWith(Report(questionsBody: "<p>Nothing.</p>"), "questions section is empty");

    [Fact]
    public void Check_QuestionsAndNoQuestionsMarker_Fails()
        => AssertFailsWith(Report(questionsBody: Question + "<p data-dev-report-no-questions>None.</p>"), "has questions and also says there are none");

    [Fact]
    public void Check_QuestionOutsideTheQuestionsSection_Fails()
        => AssertFailsWith(Report(questionsBody: "<p data-dev-report-no-questions>None.</p>", tail: Question.Replace("rerun", "stray")), "question \"stray\" is outside the questions section");

    [Fact]
    public void Check_QuestionWithOneOption_Fails()
    {
        var one = Question.Replace("<label><input type=\"radio\" name=\"rerun\" value=\"no\"> No</label>", "");
        AssertFailsWith(Report(questionsBody: one), "has 1 option(s)");
    }

    [Fact]
    public void Check_QuestionWithNoRecommendation_Fails()
        => AssertFailsWith(Report(questionsBody: Question.Replace(" data-recommended", "")), "has 0 recommended options");

    [Fact]
    public void Check_QuestionWithTwoRecommendations_Fails()
        => AssertFailsWith(Report(questionsBody: Question.Replace("value=\"no\"", "value=\"no\" data-recommended")), "has 2 recommended options");

    [Fact]
    public void Check_DuplicateQuestionIds_Fails()
        => AssertFailsWith(Report(questionsBody: Question + Question), "\"rerun\" is used more than once");

    [Fact]
    public void Check_BadQuestionId_Fails()
        => AssertFailsWith(Report(questionsBody: Question.Replace("data-dev-report-question=\"rerun\"", "data-dev-report-question=\"re run\"")), "id \"re run\" is not allowed");

    [Fact]
    public void Check_EmptyNoQuestionsElement_Fails()
        => AssertFailsWith(Report(questionsBody: "<p data-dev-report-no-questions>  <span></span> </p>"), "no-questions element is empty");

    [Fact]
    public void Check_RadioOptionsAfterTheQuestionCloses_Fails()
    {
        // Review finding 5: options placed after the question's end tag look like its options by order, but
        // the page only offers what is inside the question element.
        var detached = "<div data-dev-report-question=\"rerun\"><h3>Rerun?</h3></div>" +
                       "<label><input type=\"radio\" name=\"rerun\" value=\"yes\" data-recommended> Yes</label>" +
                       "<label><input type=\"radio\" name=\"rerun\" value=\"no\"> No</label>";
        AssertFailsWith(Report(questionsBody: detached), "\"yes\" in the questions section is not inside any question");
        AssertFailsWith(Report(questionsBody: detached), "\"rerun\" has 0 option(s)");
    }

    [Fact]
    public void Check_NestedQuestions_Fails()
    {
        var nested = "<div data-dev-report-question=\"outer\">" +
                     "<label><input type=\"radio\" name=\"o\" value=\"o1\" data-recommended> O1</label>" +
                     "<label><input type=\"radio\" name=\"o\" value=\"o2\"> O2</label>" +
                     Question.Replace("rerun", "inner") + "</div>";
        AssertFailsWith(Report(questionsBody: nested), "\"inner\" is nested inside the question \"outer\"");
    }

    [Fact]
    public void Check_UnclosedQuestion_Fails()
        => AssertFailsWith(Report(questionsBody: Question.Replace("</div>", "")), "has no closing </div> tag");

    [Fact]
    public void Check_UnclosedQuestionsSection_Fails()
    {
        var html = Report(questions: false, detail: false, evidence: false) +
                   "<section data-dev-report=\"questions\">" + Question + "<section data-dev-report=\"detail\"></section>";
        AssertFailsWith(html, "questions section (<section data-dev-report=\"questions\">) has no closing");
    }

    // --- then the detail --------------------------------------------------------------------------------

    [Fact]
    public void Check_NoDetail_Fails()
        => AssertFailsWith(Report(detail: false), "has no detail section");

    [Fact]
    public void Check_EvidenceNotLast_Fails()
        => AssertFailsWith(Report(tail: "<section data-dev-report=\"detail\"></section>"), "evidence section must be the last section");

    [Fact]
    public void Check_UnknownSectionMarker_Fails()
        => AssertFailsWith(Report(tail: "<section data-dev-report=\"appendix\"></section>"), "\"appendix\" is not a section this check knows");

    // --- the reader ------------------------------------------------------------------------------------

    [Fact]
    public void Check_MarkersInCommentsScriptsAndTemplates_DoNotCount()
    {
        var hidden = "<!-- <section data-dev-report=\"summary\"></section> -->" +
                     "<script>var s = '<section data-dev-report=\"summary\">';</script>" +
                     "<template><section data-dev-report=\"detail\"></section></template>" +
                     "<textarea><section data-dev-report=\"summary\"></textarea>";
        var verdict = DevReportShapeCheck.Check(Report(afterHeader: hidden));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    [Fact]
    public void Check_EveryMarkerAfterPlaintext_DoesNotCount()
    {
        // Review finding 4: a browser renders everything after <plaintext> as text, so a report whose markers
        // all sit after it has no sections at all.
        var html = "<plaintext>" + Report();
        var verdict = DevReportShapeCheck.Check(html);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Errors, e => e.Contains("has no header"));
    }

    [Fact]
    public void Check_AnEndTagThatOnlyStartsLikeScript_DoesNotEndTheScript()
    {
        // Review finding 4: "</scripture" does not close a script in a browser, so the markers after it are
        // still script text.
        var html = "<script>var x = '</scripture>" + Report() + "';</script>";
        AssertFailsWith(html, "has no header");
    }

    [Theory]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("noscript")]
    public void Check_MarkersInsideOtherTextOnlyElements_DoNotCount(string element)
        => AssertFailsWith($"<{element}>{Report()}</{element}>", "has no header");

    [Fact]
    public void Check_UppercaseAttributesSingleQuotesAndUnquotedValues_AreRead()
    {
        var html = "<HEADER DATA-DEV-REPORT='header' data-dev-report-status=done></HEADER>" +
                   "<section data-dev-report=summary></section>" +
                   "<section data-dev-report = 'questions'><p data-dev-report-no-questions>None.</p></section>" +
                   "<section data-dev-report=\"detail\"></section>";
        var verdict = DevReportShapeCheck.Check(html);
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
        Assert.Equal("done", verdict.Status);
    }

    [Fact]
    public void Check_EmptyPage_ReportsEveryMissingSection()
    {
        var verdict = DevReportShapeCheck.Check("");
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Errors, e => e.Contains("has no header"));
        Assert.Contains(verdict.Errors, e => e.Contains("has no executive summary"));
        Assert.Contains(verdict.Errors, e => e.Contains("has no questions section"));
        Assert.Contains(verdict.Errors, e => e.Contains("has no detail section"));
    }

    [Fact]
    public void Check_UnterminatedTagsAndComments_DoNotThrow()
    {
        foreach (var html in new[] { "<", "<section data-dev-report=\"summ", "<!-- open", "<script>never closed", "<a b='x" })
        {
            Assert.False(DevReportShapeCheck.Check(html).Passed);
        }
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/DevReports/DevReportShapeCheckTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        var marker = Path.Combine(root, "packages", "client-core", "src", "devreports", "CONTRACT.md");
        Assert.True(File.Exists(marker), $"Resolved the repository root to {root}, but it has no {marker}. Run the suite from a checkout.");
        return root;
    }
}
