using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Questions;

/// <summary>
/// The Gateway reads a report version's questions the way the note-taking script does (CONTRACT.md section 2), so the
/// words and labels a member's answer carries to the session are the report's own (devthrottle_internal#2307).
/// </summary>
public sealed class DevReportQuestionsTests
{
    private static string Report(string questions) =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>T</h1></header>" +
        "<section data-dev-report=\"summary\"><p>S</p></section>" +
        $"<section data-dev-report=\"questions\">{questions}</section>" +
        "<section data-dev-report=\"detail\"><p>D</p></section>";

    private const string TwoOptions =
        "<label><input type=\"radio\" name=\"q\" value=\"a\" data-recommended> Option   A </label>" +
        "<label><input type=\"radio\" name=\"q\" value=\"b\"> Option B</label>";

    [Fact]
    public void Read_TheQuestionTextAttribute_TheOptionLabels_AndTheRecommendation()
    {
        var q = Assert.Single(DevReportQuestions.Read(Report(
            $"<div data-dev-report-question=\"q\" data-dev-report-question-text=\"  When   should we deploy? \"><h3>Ignored</h3>{TwoOptions}</div>")));

        Assert.Equal(("q", "When should we deploy?"), (q.Id, q.Text));
        Assert.Equal(new[] { new DevReportQuestionOption("a", "Option A", true), new DevReportQuestionOption("b", "Option B", false) }, q.Options);
        Assert.Equal("Option B", q.Option("b")!.Label);
        Assert.Null(q.Option("c"));
    }

    [Fact]
    public void Read_TheTextFallsBackToTheFirstHeadingThenTheId_AndALabelToItsForAttributeThenTheValue()
    {
        var questions = DevReportQuestions.Read(Report(
            "<div data-dev-report-question=\"one\"><h4>Heading words</h4>" +
            "<input type=\"radio\" id=\"x1\" name=\"one\" value=\"v1\" data-recommended><label for=\"x1\">Pointed at</label>" +
            "<input type=\"radio\" name=\"one\" value=\"v2\"></div>" +
            "<div data-dev-report-question=\"two\">" +
            "<label><input type=\"radio\" name=\"two\" value=\"a\" data-recommended> A</label><label><input type=\"radio\" name=\"two\" value=\"b\"> B</label></div>"));

        Assert.Equal(new[] { ("one", "Heading words"), ("two", "two") }, questions.Select(q => (q.Id, q.Text)));
        Assert.Equal(new[] { "Pointed at", "v2" }, questions[0].Options.Select(o => o.Label));
    }

    [Theory]
    [InlineData("<div data-dev-report-question=\"q\"><label><input type=\"radio\" name=\"q\" value=\"a\" data-recommended> A</label></div>")] // one option
    [InlineData("<div data-dev-report-question=\"q\"><label><input type=\"radio\" name=\"q\" value=\"a\"> A</label><label><input type=\"radio\" name=\"q\" value=\"b\"> B</label></div>")] // none recommended
    [InlineData("<div data-dev-report-question=\"q\"><label><input type=\"radio\" name=\"q\" value=\"a\" data-recommended> A</label><label><input type=\"radio\" name=\"q\" value=\"a\"> B</label></div>")] // one value twice
    [InlineData("<div data-dev-report-question=\"bad id\">" + TwoOptions + "</div>")] // an id the shape check refuses
    [InlineData("<div data-dev-report-question=\"outer\"><div data-dev-report-question=\"q\">" + TwoOptions + "</div></div>")] // nested
    public void Read_AQuestionTheShapeCheckWouldRefuse_IsNotOffered(string questions)
    {
        Assert.Empty(DevReportQuestions.Read(Report(questions)));
    }

    [Fact]
    public void Read_AQuestionOutsideTheQuestionsSection_OrInAReportWithNoneSaysSo_IsNotOffered()
    {
        Assert.Empty(DevReportQuestions.Read(
            "<section data-dev-report=\"questions\"><p data-dev-report-no-questions>No questions.</p></section>" +
            $"<section data-dev-report=\"detail\"><div data-dev-report-question=\"q\">{TwoOptions}</div></section>"));
        Assert.Empty(DevReportQuestions.Read("<p>no sections at all</p>"));
    }

    [Fact]
    public void Read_AnIdUsedTwice_OffersOnlyTheFirst()
    {
        var two = TwoOptions.Replace("name=\"q\"", "name=\"q2\"", StringComparison.Ordinal);
        var q = Assert.Single(DevReportQuestions.Read(Report(
            $"<div data-dev-report-question=\"q\" data-dev-report-question-text=\"First\">{TwoOptions}</div>" +
            $"<div data-dev-report-question=\"q\" data-dev-report-question-text=\"Second\">{two}</div>")));
        Assert.Equal("First", q.Text);
    }
}
