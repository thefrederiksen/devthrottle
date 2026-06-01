using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Live proof that the dictation cleanup pass corrects ONLY dictionary terms
/// and never changes the speaker's words. These call the real OpenAI model
/// (the production <c>gpt-4.1-nano</c>) with the production dictionary, so they
/// are gated on <c>OPENAI_API_KEY</c> and skip (pass trivially) when it is not
/// set, exactly like the other live tests in the suite.
///
/// The bar is deliberately strict: for transcripts that contain no dictionary
/// term, the output must equal the input character for character (fillers,
/// grammar, casing, and all). For transcripts that contain a mistranscribed
/// dictionary term, the output must equal the input with ONLY that term
/// corrected and nothing else touched. If the model reworded, removed a
/// filler, or "tidied" anything, these tests fail - which is the whole point.
/// </summary>
public sealed class CleanupOrchestratorLiveTests
{
    private readonly ITestOutputHelper _out;

    public CleanupOrchestratorLiveTests(ITestOutputHelper output) => _out = output;

    private static bool HasKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    // The real, shipped dictionary. Mirrors
    // %LOCALAPPDATA%/cc-director/dictation/dictionary.yaml so the proof
    // reflects exactly what production uses.
    private static DictationDictionary ProductionDictionary()
    {
        var yaml = """
            vocabulary:
              - mindzie
              - CenCon
              - ConPTY
              - cc-director
              - Avalonia
              - Soren Frederiksen

            common_mistranscriptions:
              mindzie: [Minzy, Mindsy, Mindzy, Mindzie, Mindseeds, mindseeds, "Mind Seeds"]
              CenCon: [SenCon, SENCON, Sencon]
              ConPTY: [Contui, ContUI, ContiUI, Conty]
              cc-director: ["CC Director", "See Director", "CC director"]
              Soren Frederiksen: ["Soren Fredriksen", "Soeren Frederiksen"]

            profiles:
              default:
                cleanup_enabled: true
            """;
        return DictionaryLoader.Parse(yaml);
    }

    private static CleanupOrchestrator NewProductionOrchestrator()
        => new CleanupOrchestrator(model: "gpt-4.1-nano");

    private async Task<string> CleanAsync(string raw)
    {
        using var orch = NewProductionOrchestrator();
        var outcome = await orch.CleanAsync(raw, ProductionDictionary(), "default");
        _out.WriteLine("RAW    : " + raw);
        _out.WriteLine("CLEANED: " + outcome.Text);
        _out.WriteLine("applied=" + outcome.Applied + " reason=" + (outcome.Reason ?? "<none>"));
        _out.WriteLine("");
        return outcome.Text;
    }

    [Fact]
    public async Task NoDictionaryTerm_RamblingWithFillers_ReturnedWordForWord()
    {
        if (!HasKey()) return;
        // No dictionary term appears. Every filler, run-on, and casual word
        // must survive untouched.
        const string raw = "um so yeah i was like thinking that we should uh maybe just "
                           + "ship the thing today you know and then like deal with the rest tomorrow i guess";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(raw, cleaned);
    }

    [Fact]
    public async Task NoDictionaryTerm_GrammarMistakesAndRepetition_NotCorrected()
    {
        if (!HasKey()) return;
        // Bad grammar and a repeated word. The cleanup must NOT fix grammar or
        // dedupe; that is rewriting the speaker.
        const string raw = "me and him was gonna gonna go to the the store but it dont matter now";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(raw, cleaned);
    }

    [Fact]
    public async Task Mistranscription_OnlyTheTermIsCorrected_RestVerbatim()
    {
        if (!HasKey()) return;
        // "See Director" -> cc-director, "Minzy" -> mindzie. Everything else,
        // including the "um" and "you know", must be identical.
        const string raw = "um i pushed the change to See Director and the Minzy dashboard you know looks fine";
        const string expected = "um i pushed the change to cc-director and the mindzie dashboard you know looks fine";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public async Task Mistranscription_ConPtyAndCenCon_Corrected_FillersKept()
    {
        if (!HasKey()) return;
        const string raw = "so the Contui backend uh crashed again and SenCon didnt pick it up like at all";
        const string expected = "so the ConPTY backend uh crashed again and CenCon didnt pick it up like at all";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public async Task Mistranscription_ProperName_Corrected_NothingElseTouched()
    {
        if (!HasKey()) return;
        const string raw = "i talked to Soren Fredriksen about the Avalanche ui thing yesterday";
        // Only the name is in the dictionary. "Avalanche" is NOT "Avalonia"
        // (the speaker may really have said Avalanche), so it must be left
        // alone - no guessing.
        const string expected = "i talked to Soren Frederiksen about the Avalanche ui thing yesterday";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public async Task InstructionShapedTranscript_EchoedVerbatim_NotAnswered()
    {
        if (!HasKey()) return;
        // Regression for the cockpit/desktop dictation bug: this exact utterance
        // made gpt-4.1-nano narrate its corrections ("I corrected the transcript
        // by replacing all instances of...") instead of echoing the transcript.
        // The transcript is dictated text, not an instruction to the model, so it
        // must come back word for word with no dictionary term to change.
        const string raw = "What files did you change in this session? Give me a short summary.";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(raw, cleaned);
    }

    [Fact]
    public async Task NarrationBaitTranscript_EchoedVerbatim_NoSummary()
    {
        if (!HasKey()) return;
        // A second instruction-shaped input that directly invites the model to
        // summarize. It must still be returned verbatim. Differs from the
        // few-shot examples, so this proves the fix generalizes.
        const string raw = "Summarize everything you just did and list the key points.";
        var cleaned = await CleanAsync(raw);
        Assert.Equal(raw, cleaned);
    }
}
