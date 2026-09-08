using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's test_check_report.py, the quotation side: a prompts file with two sessions
/// whose names carry spaces and hyphens, a third session cited by its id8, prompts at known minutes (one
/// minute shared by two sessions, one prompt spanning two lines), and a report that cites them. A conforming
/// report passes with the right counts; each rule is then broken in the REPORT and the failure must name the
/// line, the reference, the minute and the fragment.
/// </summary>
public sealed class ReportCheckTests
{
    private static readonly (string Id, string Name, (string Minute, string Text)[] Entries)[] Sessions =
    {
        ("0a1b2c3d-0000-4000-8000-000000000001", "repo-alpha - build screen", new[]
        {
            ("2026-08-25 10:30", "alpha beta gamma delta"),
            ("2026-08-25 11:05", "kappa lambda sigma\nomega zulu tango"),
        }),
        ("1b2c3d4e-0000-4000-8000-000000000002", "garden watering", new[]
        {
            ("2026-08-25 10:30", "juliet romeo alpha"),
            ("2026-08-26 09:15", "foxtrot bravo quebec yankee"),
        }),
        ("2c3d4e5f-0000-4000-8000-000000000003", "third thing", new[]
        {
            ("2026-08-27 14:00", "Beta Gamma Delta"),
        }),
    };

    private static readonly string[] ConformingReport =
    {
        "# Your week",
        "",
        "Short asks: repo-alpha - build screen, 2026-08-25 10:30 (\"beta gamma\"). Then: garden watering, 2026-08-26 09:15.",
        "A second line: repo-alpha - build screen, 2026-08-25 11:05 (\"omega zulu tango\").",
        "By id: 2c3d4e5f, 2026-08-27 14:00 (\"Beta Gamma\").",
        "",
    };

    private static string PromptsFileText(IEnumerable<(string Id, string Name, (string Minute, string Text)[] Entries)>? sessions = null)
    {
        var all = (sessions ?? Sessions).ToList();
        var prompts = all.Sum(s => s.Entries.Length);
        var words = all.Sum(s => s.Entries.Sum(e => Origin.CountWords(e.Text)));
        var lines = new List<string>
        {
            "# Your prompts - one - 2026-W35", "",
            "- account: one",
            "- week: 2026-W35, Monday 2026-08-24 to Sunday 2026-08-30, local time",
            "- time zone: America/Toronto",
            "- human prompts: " + prompts + "; words: " + words + "; sessions they fall in: " + all.Count,
            "- This file holds the developer's own prompts and nothing another account wrote.",
            "",
            "## Session index", "",
            "One line per session below, in the same order: the first eight characters of the session "
            + "id | repository name | human prompts | started by | session name.", "",
        };
        foreach (var (sid, name, entries) in all)
            lines.Add("- " + sid.Substring(0, 8) + " | owner/repo | " + entries.Length + " | human | " + name);
        lines.Add("");
        foreach (var (sid, name, entries) in all)
        {
            lines.AddRange(new[] { "#### Session " + sid + " - " + name, "", "repo: owner/repo", "agent: ClaudeCode", "start: 2026-08-24 08:00; end: 2026-08-24 09:00", "started by: human", "" });
            foreach (var (minute, text) in entries)
            {
                lines.Add("[" + minute + " local, typed/desktop, " + Origin.CountWords(text) + " words]");
                lines.AddRange(text.Split('\n').Select(part => "    " + part));
                lines.Add("");
            }
        }
        lines.Add("End of prompts. " + prompts + " human prompts.");
        return string.Join("\n", lines) + "\n";
    }

    private static string ReportText(IEnumerable<string>? lines = null) => string.Join("\n", lines ?? ConformingReport) + "\n";

    private static ReportCheck.Prompts Prompts() => ReportCheck.ParsePrompts(PromptsFileText(), "prompts-human.md");

    private static List<string> Failures(string report, ReportCheck.Prompts? prompts = null)
    {
        var error = Assert.Throws<ReportCheckException>(() => ReportCheck.CheckText(report, prompts ?? Prompts()));
        return error.Message.Split('\n').ToList();
    }

    [Fact]
    public void Conforming_report_passes_with_the_counts()
    {
        var result = ReportCheck.CheckText(ReportText(), Prompts());
        Assert.Equal((3, 4, 3), (result.Quoted, result.Citations, result.Sessions.Count));
    }

    [Fact]
    public void Fragment_off_by_one_character_is_named()
    {
        var lines = Failures(ReportText().Replace("(\"beta gamma\")", "(\"beta gamme\")"));
        var only = Assert.Single(lines);
        Assert.StartsWith("line 3: ", only);
        Assert.Contains("quotation not found", only);
        Assert.Contains("'repo-alpha - build screen', 2026-08-25 10:30", only);
        Assert.Contains("\"beta gamme\"", only);
    }

    [Fact]
    public void Fragment_differing_only_in_case_is_named()
    {
        var lines = Failures(ReportText().Replace("(\"Beta Gamma\")", "(\"beta gamma\")"));
        var only = Assert.Single(lines);
        Assert.StartsWith("line 5: ", only);
        Assert.Contains("'2c3d4e5f', 2026-08-27 14:00", only);
    }

    [Fact]
    public void Right_minute_in_the_wrong_session_is_named()
    {
        var lines = Failures(ReportText().Replace("garden watering, 2026-08-26 09:15", "garden watering, 2026-08-25 11:05"));
        var only = Assert.Single(lines);
        Assert.Contains("no prompt at 2026-08-25 11:05 in session 'garden watering'", only);
    }

    [Fact]
    public void Session_with_no_prompt_at_the_minute_is_named()
    {
        var lines = Failures(ReportText().Replace("garden watering, 2026-08-26 09:15", "garden watering, 2026-08-26 09:16"));
        Assert.Equal(new[] { "line 3: no prompt at 2026-08-26 09:16 in session 'garden watering'" }, lines);
    }

    [Fact]
    public void A_minute_with_no_session_before_it_is_named()
    {
        var lines = Failures(ReportText().Replace("Then: garden watering, 2026-08-26 09:15", "Then at 2026-08-26 09:15"));
        Assert.Equal(new[] { "line 3: citation names no session at that minute: 2026-08-26 09:15" }, lines);
    }

    [Fact]
    public void A_bare_quoted_span_is_named()
    {
        var lines = Failures(ReportText().Replace("Then: garden watering", "Then add a \"Done when\" line: garden watering"));
        Assert.Equal(new[] { "line 3: a quotation must sit in a citation: \"Done when\"" }, lines);
    }

    [Fact]
    public void Citations_without_any_quoted_fragment_are_refused()
    {
        var text = ReportText();
        foreach (var fragment in new[] { " (\"beta gamma\")", " (\"omega zulu tango\")", " (\"Beta Gamma\")" })
            text = text.Replace(fragment, "");
        Assert.Equal(new[] { ReportCheck.NoQuotedCitation }, Failures(text));
    }

    private const string PlantedHeading = "### 1. Ask: repo-alpha - build screen, 2026-08-25 10:30 (\"not in any prompt\"); "
        + "then garden watering, 2026-08-25 10:31";

    [Fact]
    public void A_heading_line_is_checked_like_a_body_line()
    {
        var lines = Failures(ReportText(new[] { PlantedHeading }.Concat(ConformingReport)));
        Assert.Equal(new[]
        {
            "line 1: quotation not found in the prompt at 'repo-alpha - build screen', 2026-08-25 10:30: \"not in any prompt\"",
            "line 1: no prompt at 2026-08-25 10:31 in session 'garden watering'",
        }, lines);
    }

    [Fact]
    public void A_bare_quoted_span_in_a_heading_is_named()
    {
        var lines = Failures(ReportText(new[] { "### 1. Say \"done\" when it is" }.Concat(ConformingReport)));
        Assert.Equal(new[] { "line 1: a quotation must sit in a citation: \"done\"" }, lines);
    }

    [Fact]
    public void A_proven_quotation_in_a_heading_counts()
    {
        var heading = "### 1. Ask: repo-alpha - build screen, 2026-08-25 10:30 (\"gamma delta\")";
        var result = ReportCheck.CheckText(ReportText(new[] { heading }.Concat(ConformingReport)), Prompts());
        Assert.Equal((4, 5), (result.Quoted, result.Citations));
    }

    [Theory]
    [InlineData("(\"\")")]
    [InlineData("(\"   \")")]
    [InlineData("(\"beta ga\")")]
    public void A_fragment_under_eight_characters_is_refused_naming_the_line(string planted)
    {
        var lines = Failures(ReportText().Replace("(\"beta gamma\")", planted));
        var only = Assert.Single(lines);
        Assert.StartsWith("line 3: ", only);
        Assert.Contains("at least " + ReportCheck.MinFragment + " characters", only);
        Assert.Contains("'repo-alpha - build screen', 2026-08-25 10:30", only);
    }

    [Fact]
    public void An_eight_character_fragment_that_is_present_passes()
    {
        var result = ReportCheck.CheckText(ReportText().Replace("(\"beta gamma\")", "(\"beta gam\")"), Prompts());
        Assert.Equal((3, 4), (result.Quoted, result.Citations));
    }

    private const string ShortSessionId = "3d4e5f60-0000-4000-8000-000000000004";
    private const string ShortLine = "Terse: short ask, 2026-08-28 08:00 (\"ok\").";

    private static ReportCheck.Prompts WithShortPrompt(string text)
        => ReportCheck.ParsePrompts(PromptsFileText(Sessions.Append((ShortSessionId, "short ask", new[] { ("2026-08-28 08:00", text) }))), "prompts-human.md");

    [Fact]
    public void A_two_character_fragment_equal_to_the_whole_prompt_passes()
    {
        var result = ReportCheck.CheckText(ReportText(ConformingReport.Append(ShortLine)), WithShortPrompt("ok"));
        Assert.Equal((4, 5), (result.Quoted, result.Citations));
    }

    [Fact]
    public void The_same_two_characters_as_a_substring_of_a_longer_prompt_are_refused()
    {
        var lines = Failures(ReportText(ConformingReport.Append(ShortLine)), WithShortPrompt("ok then fine"));
        var only = Assert.Single(lines);
        Assert.StartsWith("line 7: ", only);
        Assert.Contains("at least " + ReportCheck.MinFragment + " characters", only);
        Assert.Contains("'short ask', 2026-08-28 08:00", only);
    }

    [Fact]
    public void Every_fragment_emptied_is_refused_not_counted()
    {
        var text = ReportText();
        foreach (var fragment in new[] { "(\"beta gamma\")", "(\"omega zulu tango\")", "(\"Beta Gamma\")" })
            text = text.Replace(fragment, "(\"\")");
        var lines = Failures(text);
        Assert.Equal(new[] { "line 3", "line 4", "line 5" }, lines.Take(3).Select(l => l.Split(':')[0]));
        Assert.All(lines.Take(3), l => Assert.Contains("at least " + ReportCheck.MinFragment + " characters", l));
        Assert.Equal(new[] { ReportCheck.NoQuotedCitation }, lines.Skip(3));
    }

    [Theory]
    [InlineData("- human prompts: ", "'- human prompts: <N>; words: ...' line")]
    [InlineData("#### Session ", "'#### Session <id> - <name>' heading")]
    [InlineData("[2026-08-2", "no prompt stamp")]
    public void A_prompts_file_that_does_not_parse_names_the_missing_piece(string cut, string missing)
    {
        var text = string.Join("\n", PromptsFileText().Split('\n').Where(line => !line.StartsWith(cut, StringComparison.Ordinal))) + "\n";
        var error = Assert.Throws<PromptsFileException>(() => ReportCheck.ParsePrompts(text, "prompts-human.md"));
        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void The_quoted_spans_are_the_fragments_marks_and_brackets_included()
    {
        var spans = 0;
        foreach (var line in ReportText().Split('\n'))
        {
            foreach (var (start, end) in ReportCheck.QuotedSpans(line))
            {
                var span = line.Substring(start, end - start);
                Assert.StartsWith(" (\"", span);
                Assert.EndsWith("\")", span);
                spans++;
            }
        }
        Assert.Equal(3, spans);
    }
}
