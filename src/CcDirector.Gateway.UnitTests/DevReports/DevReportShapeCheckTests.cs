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

    [Theory]
    [InlineData("<p data-dev-report-no-questions>&nbsp;</p>")]
    [InlineData("<p data-dev-report-no-questions>&#160; &#x20;</p>")]
    [InlineData("<p data-dev-report-no-questions><div>No questions - nothing needed from you.</div></p>")]
    public void Check_NoQuestionsElementWithNoWordsOfItsOwn_Fails(string body)
        => AssertFailsWith(Report(questionsBody: body), "no-questions element is empty");

    [Fact]
    public void Check_NoQuestionsWordsInsideAnInlineElement_Pass()
        => Assert.True(DevReportShapeCheck.Check(Report(questionsBody: "<p data-dev-report-no-questions><b>No questions</b> - nothing needed.</p>")).Passed);

    [Fact]
    public void Check_TwoNoQuestionsElements_Fails()
        => AssertFailsWith(Report(questionsBody: "<div data-dev-report-no-questions>None.<div data-dev-report-no-questions>None.</div></div>"), "2 no-questions elements");

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
    public void Check_UnclosedQuestionFollowedByAnother_IsJudgedAsTheBrowserNestsIt()
    {
        // A browser does not close a div at the next div: the second question ends up inside the first, and so
        // do its options. The check reads the page the browser builds, so it says so.
        var unclosed = Question.Replace("</div>", "") + Question.Replace("rerun", "second");
        AssertFailsWith(Report(questionsBody: unclosed), "\"second\" is nested inside the question \"rerun\"");
    }

    [Fact]
    public void Check_UnclosedQuestionAtTheEndOfTheSection_IsClosedByTheSectionAndPasses()
    {
        var verdict = DevReportShapeCheck.Check(Report(questionsBody: Question.Replace("</div>", "")));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    [Fact]
    public void Check_UnclosedQuestionsSection_SwallowsTheNextSectionAndFails()
    {
        var html = Report(questions: false, detail: false, evidence: false) +
                   "<section data-dev-report=\"questions\">" + Question + "<section data-dev-report=\"detail\"><p>Detail.</p></section>";
        AssertFailsWith(html, "detail section is inside the questions section");
    }

    // --- the radio group of each question ------------------------------------------------------------

    [Fact]
    public void Check_OptionsOfOneQuestionWithDifferentNames_Fails()
    {
        // Inspection finding 2: two names make two groups, so the browser lets both options be checked and the
        // recommended one stays checked when the owner clicks the other.
        var twoNames = "<div data-dev-report-question=\"pick\"><h3>Pick?</h3>" +
                       "<label><input type=\"radio\" name=\"first\" value=\"first\"> First</label>" +
                       "<label><input type=\"radio\" name=\"second\" value=\"recommended\" data-recommended> Second</label></div>";
        AssertFailsWith(Report(questionsBody: twoNames), "\"pick\" use 2 different names (\"first\", \"second\")");
    }

    [Fact]
    public void Check_OptionWithNoName_Fails()
        => AssertFailsWith(Report(questionsBody: Question.Replace("name=\"rerun\" value=\"no\"", "value=\"no\"")), "\"rerun\" has 1 option(s) with no name");

    [Fact]
    public void Check_TwoQuestionsSharingOneName_Fails()
    {
        var shared = Question + Question.Replace("data-dev-report-question=\"rerun\"", "data-dev-report-question=\"other\"");
        AssertFailsWith(Report(questionsBody: shared), "name \"rerun\" of the options of the question \"rerun\" is also used");
    }

    // --- what the owner can see ------------------------------------------------------------------------

    [Theory]
    [InlineData("<section data-dev-report=\"summary\" hidden><p>Summary.</p></section>")]
    [InlineData("<div hidden><section data-dev-report=\"summary\"><p>Summary.</p></section></div>")]
    public void Check_HiddenSummary_Fails(string summary)
        => AssertFailsWith(Report(summary: false, afterHeader: summary), "executive summary is hidden");

    [Fact]
    public void Check_HiddenSummaryAndQuestionsSection_Fails()
    {
        // Inspection finding 3: the payload with both sections hidden passed.
        var html = Report()
            .Replace("<section data-dev-report=\"summary\">", "<section data-dev-report=\"summary\" hidden>")
            .Replace("<section data-dev-report=\"questions\">", "<section data-dev-report=\"questions\" hidden>");
        AssertFailsWith(html, "executive summary is hidden");
        AssertFailsWith(html, "questions section is hidden");
    }

    [Theory]
    [InlineData("<svg><desc>Words nobody sees.</desc></svg>")]
    [InlineData("<svg><title>Words nobody sees.</title></svg>")]
    [InlineData("<svg><metadata>Words nobody sees.</metadata></svg>")]
    public void Check_SummaryWhoseOnlyWordsAreSvgDescriptions_Fails(string body)
    {
        // Inspection round 2, finding 3: SVG desc, title and metadata are not drawn, so their text is not words.
        var summary = $"<section data-dev-report=\"summary\">{body}</section>";
        AssertFailsWith(Report(summary: false, afterHeader: summary), "executive summary is empty");
    }

    [Fact]
    public void Check_SummaryWithDrawnSvgText_Passes()
    {
        var summary = "<section data-dev-report=\"summary\"><svg><desc>Chart.</desc><text>Words on the page.</text></svg></section>";
        var verdict = DevReportShapeCheck.Check(Report(summary: false, afterHeader: summary));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    [Theory]
    [InlineData("<section data-dev-report=\"summary\"></section>")]
    [InlineData("<section data-dev-report=\"summary\"><p> &nbsp; </p></section>")]
    [InlineData("<section data-dev-report=\"summary\"><style>p { color: red }</style><p hidden>Hidden words.</p></section>")]
    public void Check_SummaryWithNoWords_Fails(string summary)
        => AssertFailsWith(Report(summary: false, afterHeader: summary), "executive summary is empty");

    [Theory]
    [InlineData("<style>[data-dev-report=summary] { display: none }</style>", null)]
    [InlineData(null, "style=\"display:none\"")]
    [InlineData(null, "style=\"display:/**/none\"")]
    public void Check_SectionHiddenByStyles_Passes_BecauseTheCheckDoesNotJudgeCss(string? stylesheet, string? inlineStyle)
    {
        // Architect ruling, inspection round 2: the check judges structure, not CSS. Styles - a stylesheet rule or
        // an inline style attribute - can hide a section and the check does not try to detect it; the host policy
        // is the boundary. Recorded so nobody reads the hidden-section rule as more than it is.
        var html = Report(tail: stylesheet);
        if (inlineStyle is not null)
        {
            html = html.Replace("<section data-dev-report=\"summary\">", $"<section data-dev-report=\"summary\" {inlineStyle}>");
        }
        Assert.True(DevReportShapeCheck.Check(html).Passed);
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
    public void Check_ReportWithAScript_Fails()
        => AssertFailsWith(Report(tail: "<script>parent.postMessage({}, '*');</script>"), "1 <script> element(s)");

    [Fact]
    public void Check_ReportWithAnInlineEventHandler_Fails()
        => AssertFailsWith(Report(tail: "<img src=\"x.png\" onerror=\"alert(1)\">"), "onerror on <img>");

    [Fact]
    public void Check_ScriptInsideSvg_Fails()
        => AssertFailsWith(Report(tail: "<svg><script>parent.postMessage({}, '*');</script></svg>"), "1 <script> element(s)");

    [Fact]
    public void Check_ScriptAfterATemplateWithACommentedStartTag_Fails()
    {
        // Inspection finding 1, second payload: the scanner read "<template>" inside the comment as a nested
        // template and skipped to the end of the file, missing a script the browser runs.
        AssertFailsWith(Report(tail: "<template><!-- <template> --></template><script>parent.postMessage({}, '*');</script>"), "1 <script> element(s)");
    }

    [Fact]
    public void Check_ScriptInsideTemplateContent_IsNotInTheLiveDocument()
    {
        var verdict = DevReportShapeCheck.Check(Report(tail: "<template><script>parent.postMessage({}, '*');</script><img onerror=\"x()\"></template>"));
        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors));
    }

    [Fact]
    public void Check_MarkersInsideATemplateWithACommentedEndTag_DoNotCount()
    {
        // Inspection finding 1, first payload: the scanner read "</template>" inside the comment as the end of
        // the inner template and counted markers that are inert content of the outer one.
        var html = "<template><template><!-- </template> --></template>" + Report() + "</template>";
        AssertFailsWith(html, "has no header");
    }

    [Fact]
    public void Check_MarkersInCommentsStylesAndTemplates_DoNotCount()
    {
        var hidden = "<!-- <section data-dev-report=\"summary\"></section> -->" +
                     "<style>/* <section data-dev-report=\"summary\"> */</style>" +
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

    [Fact]
    public void Check_MarkersInsideANestedTemplate_DoNotCount()
    {
        // Second review, finding 4: the outer template's content runs past the inner template's end tag.
        var html = "<template><template></template>" + Report() + "</template>";
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

    [Theory]
    [InlineData("<template data-dev-report=\"header\" data-dev-report-status=\"done\">Report</template>")]
    [InlineData("<noscript data-dev-report=\"header\" data-dev-report-status=\"done\">Report</noscript>")]
    [InlineData("<meta data-dev-report=\"header\" data-dev-report-status=\"done\">")]
    [InlineData("<style data-dev-report=\"header\" data-dev-report-status=\"done\"></style>")]
    public void Check_UnrenderedElementAsTheHeader_DoesNotCount(string header)
    {
        // Inspection round 2, finding 1: the element itself, not only what is inside it, must be rendered. A meta
        // is also moved into the head by the parser.
        var html = Report().Replace(
            "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Report</h1></header>", header);
        Assert.Contains(header, html);
        AssertFailsWith(html, "has no header");
    }

    [Theory]
    [InlineData("template")]
    [InlineData("noscript")]
    [InlineData("style")]
    public void Check_UnrenderedElementAsTheDetail_DoesNotCount(string element)
    {
        var detail = $"<{element} data-dev-report=\"detail\">Detail.</{element}>";
        AssertFailsWith(Report(detail: false, evidence: false, tail: detail), "has no detail section");
    }

    [Fact]
    public void Check_UnrenderedNoQuestionsElement_DoesNotCount()
    {
        var body = "<noscript data-dev-report-no-questions>No questions - nothing needed from you.</noscript>";
        AssertFailsWith(Report(questionsBody: body), "questions section is empty");
    }

    [Fact]
    public void Check_ReportAtThePublishLimit_IsCheckedInBoundedTime()
    {
        // Inspection round 2, finding 4: the radio-name check scanned every radio once per question, so a valid
        // report grew quadratically (37.5 s for 10,000 questions). A report at the 10 MB publish limit (mission
        // ruling 6), all questions (49,202 of them), is the worst valid case. Measured on the development machine
        // on 2026-09-16: 1.6 to 2.1 s in Release, 1.6 to 2.2 s in Debug. The 30 s bound leaves wide headroom for a
        // slow or busy machine; the quadratic check took 37.5 s on a report a fifth this size.
        var body = new System.Text.StringBuilder();
        var i = 0;
        while (body.Length < 10 * 1024 * 1024 - 1024)
        {
            body.Append($"<div data-dev-report-question=\"q{i}\"><h3>Question {i}?</h3>")
                .Append($"<label><input type=\"radio\" name=\"q{i}\" value=\"yes\" data-recommended> Yes</label>")
                .Append($"<label><input type=\"radio\" name=\"q{i}\" value=\"no\"> No</label></div>");
            i++;
        }
        var html = Report(questionsBody: body.ToString());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var verdict = DevReportShapeCheck.Check(html);
        clock.Stop();

        Assert.True(verdict.Passed, string.Join("\n", verdict.Errors.Take(5)));
        Assert.True(clock.Elapsed < System.TimeSpan.FromSeconds(30),
            $"Checking a {html.Length:N0}-character report with {i:N0} questions took {clock.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public void Check_UppercaseAttributesSingleQuotesAndUnquotedValues_AreRead()
    {
        var html = "<HEADER DATA-DEV-REPORT='header' data-dev-report-status=done></HEADER>" +
                   "<section data-dev-report=summary>Summary.</section>" +
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
