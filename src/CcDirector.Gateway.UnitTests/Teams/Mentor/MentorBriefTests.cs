using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>What the Mentor's model is asked and what is accepted back (devthrottle_internal#2305). Pure: no database,
/// no model.</summary>
public sealed class MentorBriefTests
{
    private static readonly MentorWeek Week = new(2026, 40);

    private static MentorRequest Request(int prompts = 3) =>
        MentorBrief.Build(Week, Enumerable.Range(1, prompts)
            .Select(i => MentorRig.Record(new DateTime(2026, 9, 29, 9, i, 0, DateTimeKind.Utc), $"prompt number {i}") with { PromptId = $"id-{i}", PersonSubject = "sub-rob" })
            .ToList());

    private static string Answer(string tone = "hard", string? workedOn = "Worked on the signup page.", string? howItWent = null,
        string? wentBadly = "They restarted the same task four times.", string[]? quotes = null, string? oneThing = "Name the file first.") =>
        System.Text.Json.JsonSerializer.Serialize(new { tone, workedOn, howItWent, wentBadlyAndWhy = wentBadly, quotes = quotes ?? new[] { "P1" }, oneThingToTry = oneThing });

    [Fact]
    public void Instruction_IsLoadedFromItsOneFile_AndForbidsComparingPeople()
    {
        Assert.Contains("You are the Mentor", MentorBrief.Instruction);
        Assert.Contains("Never compare the person with anyone else", MentorBrief.Instruction);
        Assert.Contains("third person", MentorBrief.Instruction);
        Assert.Contains("never a name", MentorBrief.Instruction);
    }

    [Fact]
    public void Instruction_TellsTheModelBothRulesItsAnswerIsRefusedFor_AndItsOwnProseHasNoQuotationMark()
    {
        // Review H2: the model is told what loses the whole block, in plain words.
        Assert.Contains("Never put a double quotation mark anywhere in the text of a field", MentorBrief.Instruction);
        Assert.Contains("Say what a prompt asked in your own words. Never repeat several of its words in a row", MentorBrief.Instruction);
        // The number the model is told is the number the check uses (review J7).
        var oneLine = string.Join(' ', MentorBrief.Instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains($"copies {MentorBrief.MaxEchoedWords} or more words of a prompt", oneLine);
        Assert.Contains("list its id in the quotes field", MentorBrief.Instruction);
        Assert.Contains("Name a file by its last part only, never a whole path, command or web address", MentorBrief.Instruction);
        // The guidance before the field list sets the example, so it carries no double quotation mark itself.
        var guidance = MentorBrief.Instruction[..MentorBrief.Instruction.IndexOf("Fields:", StringComparison.Ordinal)];
        Assert.DoesNotContain("\"", guidance);
    }

    [Fact]
    public void Build_LabelsEachPromptOldestFirst_AndSpeaksOfThePersonAsThey()
    {
        var request = Request();

        Assert.Equal(new[] { "P1", "P2", "P3" }, request.PromptsByLabel.Keys.OrderBy(k => k));
        Assert.Equal("prompt number 1", request.PromptsByLabel["P1"].Text);
        Assert.Contains("[P3]", request.Text);
        Assert.Contains("as they:", request.Text);
    }

    [Fact]
    public void Build_ShowsAtMostTheMostRecentPrompts_AndCutsALongOne()
    {
        var many = Enumerable.Range(0, MentorBrief.MaxPromptsShown + 5)
            .Select(i => MentorRig.Record(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), i == MentorBrief.MaxPromptsShown + 4 ? new string('x', 2000) : $"p{i}") with { PromptId = $"id-{i}" })
            .ToList();

        var request = MentorBrief.Build(Week, many);

        Assert.Equal(MentorBrief.MaxPromptsShown, request.PromptsByLabel.Count);
        Assert.DoesNotContain("] p0", request.Text);
        Assert.Contains("[cut]", request.Text);
        // The record kept for quoting is whole.
        Assert.Equal(2000, request.PromptsByLabel[$"P{MentorBrief.MaxPromptsShown}"].Text.Length);
    }

    [Fact]
    public void Build_NoPrompts_Throws_BecauseAPersonWithNoneGetsNoModelCall()
    {
        Assert.Throws<ArgumentException>(() => MentorBrief.Build(Week, Array.Empty<PromptRecord>()));
    }

    [Fact]
    public void Check_AWellFormedHardWeek_IsAccepted()
    {
        var check = MentorBrief.Check(Answer(quotes: new[] { "P1", "P3" }), Request());

        Assert.Null(check.Refusal);
        Assert.Equal("hard", check.Answer!.Tone);
        Assert.Equal(new[] { "P1", "P3" }, check.Answer.QuoteLabels);
    }

    [Fact]
    public void Check_AWellFormedGoodWeek_WithNoQuotes_IsAccepted()
    {
        var check = MentorBrief.Check(Answer(tone: "good", howItWent: "Fine.", wentBadly: null, quotes: Array.Empty<string>()), Request());

        Assert.True(check.Answer is not null, check.Refusal);
        Assert.Empty(check.Answer!.QuoteLabels);
    }

    [Fact]
    public void Check_AnswerInsideOneCodeFence_IsAccepted()
    {
        Assert.NotNull(MentorBrief.Check("```json\n" + Answer() + "\n```", Request()).Answer);
    }

    public static TheoryData<string, string> RefusedAnswers => new()
    {
        { "", "not one JSON object" },
        { "Here is the block: " + Answer(), "not one JSON object" },
        { "[1,2]", "not one JSON object" },
        { "{\"tone\": }", "not valid JSON" },
        { Answer(tone: "great"), "tone" },
        { Answer(workedOn: null), "workedOn was not text" },
        { Answer(workedOn: "  "), "workedOn was empty" },
        { Answer(oneThing: new string('a', MentorBrief.MaxFieldChars + 1)), "at most" },
        { Answer(howItWent: null, wentBadly: null, quotes: Array.Empty<string>()), "neither how the week went" },
        { Answer(quotes: new[] { "P9" }), "not one of this person's prompts" },
        { Answer(quotes: new[] { "id-1" }), "not one of this person's prompts" },
        { Answer(quotes: new[] { "P1", "P1" }), "twice" },
        { Answer(quotes: new[] { "P1", "P2", "P3" }), "at most 2" },
        { Answer(quotes: Array.Empty<string>()), "without quoting a prompt" },
        { Answer(tone: "good", howItWent: "Fine.", wentBadly: null, quotes: new[] { "P1" }), "without saying where the week went badly" },
        { "{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"oneThingToTry\":\"z\"}", "left out fields: quotes" },
        { "{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":[],\"oneThingToTry\":\"z\",\"rank\":1}", "1 field(s) that were not asked for" },
        { "{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":\"P1\",\"oneThingToTry\":\"z\"}", "quotes were not a list" },
        { "{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":[1],\"oneThingToTry\":\"z\"}", "other than prompt ids" },
    };

    [Theory]
    [MemberData(nameof(RefusedAnswers))]
    public void Check_AnAnswerNotTheShapeAskedFor_IsRefused_WithAReason(string answer, string reasonContains)
    {
        var check = MentorBrief.Check(answer, Request());

        Assert.Null(check.Answer);
        Assert.Contains(reasonContains, check.Refusal);
    }

    // ---- a prompt reaches the page only as a quote (review G1) ------------------------------------------------------

    private const string Typed = "please fix the signup thing so it doesn't break on mobile again today";

    private static MentorRequest RequestWith(string text) =>
        MentorBrief.Build(Week, new[]
        {
            MentorRig.Record(new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc), text) with { PromptId = "id-1", PersonSubject = "sub-rob" },
        });

    public static TheoryData<string, string> EchoingAnswers => new()
    {
        // Eight consecutive words of the prompt, in each free-text field.
        { Answer(workedOn: "They asked to fix the signup thing so it doesn't break on mobile."), "workedOn repeated" },
        { Answer(tone: "good", howItWent: "Fine: fix the signup thing so it doesn't break on mobile.", wentBadly: null, quotes: Array.Empty<string>()), "howItWent repeated" },
        { Answer(wentBadly: "They typed fix the signup thing so it doesn't break on mobile four times."), "wentBadlyAndWhy repeated" },
        { Answer(oneThing: "Instead of fix the signup thing so it doesn't break on mobile, name the file."), "oneThingToTry repeated" },
        // Without case or punctuation: the same words still count.
        { Answer(wentBadly: "They typed FIX THE SIGNUP THING, SO IT DOESNT BREAK ON MOBILE four times."), "wentBadlyAndWhy repeated" },
        // Any double quotation mark, straight or curly, even around words of their own.
        { Answer(wentBadly: "They asked for \"the fix\" four times."), "wentBadlyAndWhy contained a quotation mark" },
        { Answer(workedOn: "The \u201Csignup\u201D page."), "workedOn contained a quotation mark" },
        // Words glued by hyphens, slashes or underscores are still words (review H4).
        { Answer(wentBadly: "They typed fix-the-signup-thing-so-it-doesnt-break-on-mobile four times."), "wentBadlyAndWhy repeated" },
        { Answer(wentBadly: "They typed fix/the/signup/thing/so/it/doesnt/break/on/mobile four times."), "wentBadlyAndWhy repeated" },
        { Answer(wentBadly: "They typed fix_the_signup_thing_so_it_doesnt_break_on_mobile four times."), "wentBadlyAndWhy repeated" },
    };

    [Theory]
    [MemberData(nameof(EchoingAnswers))]
    public void Check_AFreeTextFieldCarryingAPromptsWords_IsRefused(string answer, string reasonContains)
    {
        var check = MentorBrief.Check(answer, RequestWith(Typed));

        Assert.Null(check.Answer);
        Assert.Contains(reasonContains, check.Refusal);
    }

    [Fact]
    public void Check_SevenConsecutiveWordsOfAPrompt_AreNotAnEcho_AndTheAnswerIsAccepted()
    {
        // "fix the signup thing so it doesn't" - seven words of the prompt, then the answer's own.
        var check = MentorBrief.Check(Answer(wentBadly: "They asked to fix the signup thing so it doesn't, and then restarted."), RequestWith(Typed));

        Assert.True(check.Answer is not null, check.Refusal);
    }

    [Fact]
    public void Check_AnEchoOfAPromptNotShownToTheModel_IsNotChecked_BecauseTheModelNeverSawIt()
    {
        // The check is against the prompts THIS request showed; the words of another prompt are not the model's to repeat.
        var check = MentorBrief.Check(Answer(wentBadly: "They typed fix the signup thing so it doesn't break on mobile four times."), Request());

        Assert.True(check.Answer is not null, check.Refusal);
    }

    // ---- a refusal never carries what the model wrote (review G2) ---------------------------------------------------

    public static TheoryData<string> AnswersCarryingModelText => new()
    {
        "{\"please fix the signup thing so it doesnt break\": 1}",
        "{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":[],\"oneThingToTry\":\"z\",\"please fix the signup thing so it doesnt break\":1}",
        Answer(quotes: new[] { "please fix the signup thing so it doesnt break" }),
        "{\"tone\": please fix the signup thing so it doesnt break}",
    };

    [Theory]
    [MemberData(nameof(AnswersCarryingModelText))]
    public void Check_ARefusal_NamesTheKindOnly_NeverTextTheModelWrote(string answer)
    {
        var check = MentorBrief.Check(answer, Request());

        Assert.Null(check.Answer);
        Assert.DoesNotContain("signup", check.Refusal);
        Assert.DoesNotContain("please", check.Refusal);
    }

    [Theory]
    [InlineData("good", "a good week")]
    [InlineData("mixed", "a mixed week")]
    [InlineData("hard", "a hard week")]
    public void Label_NamesEachTone(string tone, string label) => Assert.Equal(label, MentorTones.Label(tone));

    [Fact]
    public void Label_NotATone_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => MentorTones.Label("great"));
}
