using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// AN ASK RULE ON A SEAT NOBODY WATCHES (Factory Definition mission, phase 5, QA finding 4). An ask rule binds even under
/// the Director's permission flag, so a seat started by a schedule or a check waits at the question until a person
/// answers it. The check says so, as a notice, on the seat's line naming each rule set with an ask rule - and only for a
/// seat that has starts: a seat a person starts has a person there to answer.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionCheckAskNoticeTests
{
    private const string BaseWithAnAsk = """
        {
          "permissions": {
            "allow": ["Bash(git status)"],
            "deny": ["Bash(git push:*)"],
            "ask": ["Bash(git --version)"]
          }
        }
        """;

    private static List<string> AskNotices(IReadOnlyDictionary<string, string> content)
        => Check(content).Findings.Where(f => f.Message == FactoryDefinitionCheck.AskStopsAnUnattendedRun)
            .Select(f => $"{f.File}:{f.Line}:{f.Severity}").ToList();

    [Fact]
    public void Check_ARuleSetWithAnAskRule_NamedBySeatsWithStarts_GetsTheNoticeOnEachSeatsRulesLine()
    {
        // The boss (rules on line 10) and outreach (rules on line 17) both have starts and both name base.
        var content = Valid();
        content["rules/base.json"] = BaseWithAnAsk.ReplaceLineEndings("\n");

        Assert.Equal(new[] { "seats/boss.yaml:10:notice", "seats/outreach.yaml:17:notice" }, AskNotices(content));
    }

    [Fact]
    public void Check_TheNotice_IsTheSentenceAPersonReads_AndNeverAnError()
    {
        var content = Valid();
        content["rules/base.json"] = BaseWithAnAsk.ReplaceLineEndings("\n");

        var result = Check(content);

        Assert.Equal(
            "This seat starts on a schedule or a check, with nobody there to answer: an ask rule stops the run until a person answers it.",
            FactoryDefinitionCheck.AskStopsAnUnattendedRun);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Check_ASeatWithNoStarts_NamingARuleSetWithAnAskRule_GetsNoNotice()
    {
        // The writer has no starts: a person starts it, and is there to answer.
        var content = Valid();
        content["rules/base.json"] = BaseWithAnAsk.ReplaceLineEndings("\n");
        content["seats/writer.yaml"] = content["seats/writer.yaml"].TrimEnd('\n') + "\nrules: [base]\n";

        Assert.DoesNotContain(AskNotices(content), n => n.StartsWith("seats/writer.yaml", StringComparison.Ordinal));
        Assert.Equal(2, AskNotices(content).Count);
    }

    [Fact]
    public void Check_ASeatWithStarts_WhoseRuleSetsAskNothing_GetsNoNotice()
    {
        // The valid definition's base has an empty ask list and outreach's rules have none.
        Assert.Empty(AskNotices(Valid()));
    }

    [Fact]
    public void Check_ARuleSetWithAnAskRule_NamedOnlyByOneSeatWithStarts_GetsTheNoticeOnThatSeatAlone()
    {
        var content = Valid();
        content["rules/outreach.json"] = "{\n  \"permissions\": {\n    \"deny\": [\"Bash(rm:*)\"],\n    \"ask\": [\"Bash(git push:*)\"]\n  }\n}\n";

        Assert.Equal(new[] { "seats/outreach.yaml:17:notice" }, AskNotices(content));
    }
}
