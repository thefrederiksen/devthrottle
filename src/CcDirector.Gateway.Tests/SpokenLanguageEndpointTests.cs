using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Language tab's surface, end to end over HTTP (issue #1010). Boots a real GatewayHost on an ephemeral
/// port with an isolated CC_DIRECTOR_ROOT.
///
/// WHY THESE ARE HTTP TESTS AND NOT COMPONENT TESTS. The client is deliberately dumb: it renders the document
/// this route returns and derives nothing. That is only a safety property if the DOCUMENT is complete and
/// correct, so the document is what gets asserted - the filtered voice list, the folded labels, the
/// per-language sample, the word under each choice, and the voice the account will actually be spoken with.
/// The reverted build's second defect was exactly a client deriving what the server should have handed it
/// (devthrottle_internal#547).
/// </summary>
[Collection("DirectorRoot")]
public sealed class SpokenLanguageEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-language-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public SpokenLanguageEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-language-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private Task<JsonObject?> Get() => _http.GetFromJsonAsync<JsonObject>("gateway/spoken-language");

    private string EffectiveVoice()
        => _gateway.TenantSettingsResolver.TtsVoice(TenantId.Local, TranscriptionModeConfig.Get());

    // ----------------------------------------------------------------------------------------------
    // The document.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A fresh account: English, the product's own default voice, and all three languages offered with the
    /// word that goes under each. English says "Default" rather than repeating its own name.
    /// </summary>
    [Fact]
    public async Task Get_returns_english_by_default_with_all_three_languages_offered()
    {
        var doc = await Get();

        Assert.NotNull(doc);
        Assert.Equal("en", (string?)doc!["language"]);
        Assert.Equal(EffectiveVoice(), (string?)doc["voice"]);

        var languages = doc["languages"]!.AsArray();
        Assert.Equal(new[] { "en", "fr", "es" }, languages.Select(l => (string?)l!["code"]).ToArray());
        Assert.Equal(new[] { "English", "French", "Spanish" }, languages.Select(l => (string?)l!["label"]).ToArray());
        Assert.Equal(new[] { "Default", "Francais", "Espanol" }, languages.Select(l => (string?)l!["note"]).ToArray());
    }

    /// <summary>
    /// THE ACCEPTANCE ROW: the voice list arrives ALREADY FILTERED, per language, with its label folded.
    ///
    /// French's list is one entry and it is still a list - the control stays visible in every language,
    /// because a control that vanishes between languages reads as a glitch.
    /// </summary>
    [Fact]
    public async Task Each_languages_voice_list_is_already_filtered_and_labelled()
    {
        var doc = await Get();
        var languages = doc!["languages"]!.AsArray();

        var french = languages.Single(l => (string?)l!["code"] == "fr")!["voices"]!.AsArray();
        Assert.Single(french);
        Assert.Equal("ff_siwis", (string?)french[0]!["id"]);
        Assert.Equal("Siwis - French, female", (string?)french[0]!["label"]);

        var spanish = languages.Single(l => (string?)l!["code"] == "es")!["voices"]!.AsArray();
        Assert.Equal(new[] { "ef_dora", "em_alex", "em_santa" }, spanish.Select(v => (string?)v!["id"]).ToArray());

        var english = languages.Single(l => (string?)l!["code"] == "en")!["voices"]!.AsArray();
        Assert.Equal(28, english.Count);
        // And no language is served another language's voices - the filtering is the property, not the count.
        foreach (var entry in languages)
        {
            var language = SpokenLanguages.Require((string?)entry!["code"]);
            foreach (var voice in entry["voices"]!.AsArray())
                Assert.True(SpokenVoices.Speaks(language, (string?)voice!["id"]),
                    $"The {language.EnglishName} list offers '{(string?)voice!["id"]}', which does not speak it.");
        }
    }

    /// <summary>
    /// The sample sentence is PER LANGUAGE and reaches the client with its accents intact.
    ///
    /// Both halves are load-bearing. A single English sample would audition a French voice on English words,
    /// which tests the wrong thing. And an accent lost anywhere between the phrase file and the browser is
    /// the silent failure the accents ruling names: the engine phonemizes what it is given, so a stripped
    /// accent is a different vowel and the audio still plays.
    ///
    /// Asserted with escape sequences rather than accented literals, so a test file decoded the same wrong
    /// way cannot agree with the bug - the same discipline as the phrase-file encoding test.
    /// </summary>
    [Fact]
    public async Task The_sample_sentence_is_per_language_and_keeps_its_accents_over_the_wire()
    {
        var doc = await Get();
        var samples = doc!["languages"]!.AsArray()
            .ToDictionary(l => (string)l!["code"]!, l => (string)l!["sample"]!);

        Assert.Equal(3, samples.Values.Distinct(StringComparer.Ordinal).Count());
        // Written as escapes, never as accented literals: this test file carries no byte order mark of its
        // own, so a literal here could be misdecoded in exactly the same way as the value it is checking and
        // the two would agree while both were wrong.
        // "Voila<a-grave> a<a-grave> quoi" in the French sample, and "Asi<i-acute>" in the Spanish one.
        Assert.Contains("Voil\u00E0 \u00E0 quoi", samples["fr"]);
        Assert.Contains("As\u00ED", samples["es"]);
        // The cp1252 misread of a UTF-8 e-acute, and the replacement character, are both absent.
        foreach (var sample in samples.Values)
        {
            Assert.DoesNotContain("\u00C3\u00A9", sample);
            Assert.DoesNotContain("\uFFFD", sample);
        }
    }

    /// <summary>
    /// NO SPEECH MODEL ANYWHERE ON THIS DOCUMENT, at any depth.
    ///
    /// Choosing a language switched the speech MODEL in the build that was reverted, and that engine could
    /// not say the lengths this product writes. The compiled guard in <c>SpokenLanguageContractTests</c>
    /// stops a method deriving one; this stops the WIRE offering one, which is where the reverted build's
    /// client got the idea. If a model ever needs to be on this screen, that is a decision to argue for,
    /// not something that arrives because a snapshot grew a field.
    /// </summary>
    [Fact]
    public async Task The_document_names_no_speech_model_at_any_depth()
    {
        var doc = await Get();
        var offenders = new List<string>();
        Walk(doc!, "", offenders);

        Assert.True(offenders.Count == 0,
            "The Language tab's document must not carry a speech model or engine. A language picks a VOICE "
            + "inside the one engine that already serves English (devthrottle_internal#547). Found: "
            + string.Join(", ", offenders));

        static void Walk(JsonNode node, string path, List<string> offenders)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, child) in o)
                    {
                        if (name.Contains("model", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("engine", StringComparison.OrdinalIgnoreCase))
                            offenders.Add($"{path}/{name}");
                        if (child is not null) Walk(child, $"{path}/{name}", offenders);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is { } item) Walk(item, $"{path}[{i}]", offenders);
                    break;
            }
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Writing.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Choosing French persists it AND changes the voice the account is spoken with - checked through the
    /// resolver the wingman itself reads, not only through the document the page just received.
    ///
    /// That second read is the point. A screen echoing back what it was sent is exactly how the last attempt
    /// looked like it worked three times over.
    /// </summary>
    [Fact]
    public async Task Put_language_persists_and_the_voice_the_wingman_reads_follows_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/spoken-language", new { language = "fr" });
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("fr", (string?)doc!["language"]);
        Assert.Equal("ff_siwis", (string?)doc["voice"]);

        Assert.Equal(SpokenLanguages.French, _gateway.TenantSettingsResolver.SpokenLanguage(TenantId.Local));
        Assert.Equal("ff_siwis", EffectiveVoice());
    }

    /// <summary>A language we do not speak is REFUSED, and nothing is stored. The reverted build's first
    ///  defect was offering twelve languages populated from what the engine was CAPABLE of; the offer is a
    ///  decision, and a code outside it fails where the person can see it.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("da")]
    [InlineData("")]
    public async Task Put_an_unsupported_language_is_refused_and_stores_nothing(string code)
    {
        var resp = await _http.PutAsJsonAsync("gateway/spoken-language", new { language = code });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(SpokenLanguages.English, _gateway.TenantSettingsResolver.SpokenLanguage(TenantId.Local));
    }

    /// <summary>A voice sent for the wrong language is refused, with a message that says which language it
    ///  belongs to. Storing it would leave a screen reading French while an American voice read French words
    ///  aloud - the "the setting does nothing" report, in a form that still plays audio.</summary>
    [Fact]
    public async Task Put_a_voice_that_does_not_speak_the_language_is_refused()
    {
        var resp = await _http.PutAsJsonAsync("gateway/spoken-language/voice",
            new { language = "fr", voice = "bm_george" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("not a French voice", (string?)body!["error"]);
        Assert.Equal(TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()), EffectiveVoice());
    }

    /// <summary>
    /// THE ACCEPTANCE ROW, over HTTP: English -> French -> English gives the English voice back.
    ///
    /// Driven entirely through the routes a client uses, because the per-language memory is the mechanism
    /// that replaces the reverted build's restore step and the client is what exercises it.
    /// </summary>
    [Fact]
    public async Task A_round_trip_through_French_gives_the_English_voice_back()
    {
        (await _http.PutAsJsonAsync("gateway/spoken-language/voice",
            new { language = "en", voice = "bm_george" })).EnsureSuccessStatusCode();
        Assert.Equal("bm_george", EffectiveVoice());

        (await _http.PutAsJsonAsync("gateway/spoken-language", new { language = "fr" })).EnsureSuccessStatusCode();
        Assert.Equal("ff_siwis", EffectiveVoice());

        var back = await _http.PutAsJsonAsync("gateway/spoken-language", new { language = "en" });
        back.EnsureSuccessStatusCode();

        Assert.Equal("bm_george", (string?)(await back.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);
        Assert.Equal("bm_george", EffectiveVoice());
    }

    /// <summary>Choosing a voice for the selected language persists it and comes back on the document. The
    ///  Spanish list has three, so this is a real choice rather than the only option.</summary>
    [Fact]
    public async Task Put_voice_persists_the_choice_for_that_language()
    {
        (await _http.PutAsJsonAsync("gateway/spoken-language", new { language = "es" })).EnsureSuccessStatusCode();

        var resp = await _http.PutAsJsonAsync("gateway/spoken-language/voice",
            new { language = "es", voice = "em_santa" });
        resp.EnsureSuccessStatusCode();

        Assert.Equal("em_santa", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);
        Assert.Equal("em_santa", EffectiveVoice());
    }

    /// <summary>A body missing either field is refused rather than half-applied - a voice with no language
    ///  would have to be filed against a guess, and the guess is the thing the explicit language exists to
    ///  remove.</summary>
    [Fact]
    public async Task Put_voice_without_a_language_is_refused()
    {
        var resp = await _http.PutAsJsonAsync("gateway/spoken-language/voice", new { voice = "ff_siwis" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>The selected language's list carries the account's EFFECTIVE voice even when it is not one of
    ///  ours - a self-hosted operator's own voice id, or one saved before this screen existed. A select whose
    ///  value matches no option renders blank, which reads as the account having no voice at all.</summary>
    [Fact]
    public async Task An_unknown_existing_voice_is_still_offered_so_the_control_is_never_blank()
    {
        _gateway.TenantSettingsResolver.SetTtsVoice(TenantId.Local, "an-operators-own-voice", DateTime.UtcNow);

        var doc = await Get();

        Assert.Equal("an-operators-own-voice", (string?)doc!["voice"]);
        var english = doc["languages"]!.AsArray().Single(l => (string?)l!["code"] == "en")!["voices"]!.AsArray();
        Assert.Equal("an-operators-own-voice", (string?)english[0]!["id"]);
        Assert.Equal(29, english.Count);
    }
}
