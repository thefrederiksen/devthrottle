using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

public sealed class DictionaryLoaderTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var dict = DictionaryLoader.Parse("");
        Assert.Empty(dict.Vocabulary);
        Assert.Empty(dict.CommonMistranscriptions);
    }

    [Fact]
    public void Parse_NullYaml_ReturnsEmpty()
    {
        var dict = DictionaryLoader.Parse("# just a comment\n");
        Assert.Empty(dict.Vocabulary);
    }

    [Fact]
    public void Parse_VocabularyOnly_ParsesAndTrims()
    {
        var yaml = """
            vocabulary:
              - mindzie
              - "  CenCon  "
              - ConPTY
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.Equal(new[] { "mindzie", "CenCon", "ConPTY" }, dict.Vocabulary);
    }

    [Fact]
    public void Parse_MistranscriptionsAreCaseSensitiveCanonical()
    {
        var yaml = """
            common_mistranscriptions:
              ConPTY: [Contui, ContUI]
              mindzie: [Minzy, Mindsy]
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.True(dict.CommonMistranscriptions.ContainsKey("ConPTY"));
        Assert.True(dict.CommonMistranscriptions.ContainsKey("mindzie"));
        Assert.False(dict.CommonMistranscriptions.ContainsKey("conpty"));
        Assert.Equal(2, dict.CommonMistranscriptions["ConPTY"].Count);
    }

    [Fact]
    public void Parse_ProfilesAreCaseInsensitiveLookup()
    {
        var yaml = """
            profiles:
              code:
                cleanup_enabled: false
              email:
                cleanup_enabled: true
                style_prompt: "tighten to professional prose"
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.True(dict.Profiles.ContainsKey("CODE"));
        Assert.False(dict.Profiles["code"].CleanupEnabled);
        Assert.Equal("tighten to professional prose", dict.Profiles["email"].StylePrompt);
    }

    [Fact]
    public void Parse_AlwaysProvidesDefaultProfile()
    {
        var yaml = "vocabulary: [foo]";
        var dict = DictionaryLoader.Parse(yaml);
        Assert.True(dict.Profiles.ContainsKey("default"));
        Assert.True(dict.Profiles["default"].CleanupEnabled);
    }

    [Fact]
    public void Parse_RespectsUserDefinedDefaultProfile()
    {
        var yaml = """
            profiles:
              default:
                cleanup_enabled: false
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.False(dict.Profiles["default"].CleanupEnabled);
    }

    [Fact]
    public void Parse_DropsEmptyVocabularyEntries()
    {
        var yaml = """
            vocabulary:
              - mindzie
              - ""
              - "   "
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.Single(dict.Vocabulary);
        Assert.Equal("mindzie", dict.Vocabulary[0]);
    }

    [Fact]
    public void Parse_DropsMistranscriptionsWithEmptyVariantList()
    {
        var yaml = """
            common_mistranscriptions:
              ConPTY: []
              mindzie: [Minzy]
            """;
        var dict = DictionaryLoader.Parse(yaml);
        Assert.False(dict.CommonMistranscriptions.ContainsKey("ConPTY"));
        Assert.True(dict.CommonMistranscriptions.ContainsKey("mindzie"));
    }

    [Fact]
    public void BuildSttPrompt_EmptyVocab_ReturnsEmpty()
    {
        Assert.Equal("", DictionaryLoader.BuildSttPrompt(DictationDictionary.Empty));
    }

    [Fact]
    public void BuildSttPrompt_PacksTermsCommaSeparated()
    {
        var dict = new DictationDictionary(
            new[] { "mindzie", "CenCon", "ConPTY" },
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile>());
        var prompt = DictionaryLoader.BuildSttPrompt(dict);
        Assert.Contains("mindzie", prompt);
        Assert.Contains("CenCon", prompt);
        Assert.Contains("ConPTY", prompt);
        Assert.Contains("Glossary", prompt);
    }

    [Fact]
    public void Serialize_RoundTrips_Vocabulary_Patterns_AndProfiles()
    {
        var original = new DictationDictionary(
            new[] { "mindzie", "CenCon", "ConPTY" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ConPTY"] = new[] { "Contui", "ContUI" },
                ["mindzie"] = new[] { "Minzy" },
            },
            new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new DictationProfile("default", CleanupEnabled: true, StylePrompt: null),
                ["email"] = new DictationProfile("email", CleanupEnabled: true, StylePrompt: "tighten to professional prose"),
                ["code"] = new DictationProfile("code", CleanupEnabled: false, StylePrompt: null),
            });

        var reparsed = DictionaryLoader.Parse(DictionaryLoader.Serialize(original));

        Assert.Equal(original.Vocabulary, reparsed.Vocabulary);
        Assert.Equal(new[] { "Contui", "ContUI" }, reparsed.CommonMistranscriptions["ConPTY"]);
        Assert.Equal(new[] { "Minzy" }, reparsed.CommonMistranscriptions["mindzie"]);
        Assert.False(reparsed.Profiles["code"].CleanupEnabled);
        Assert.Equal("tighten to professional prose", reparsed.Profiles["email"].StylePrompt);
        Assert.Null(reparsed.Profiles["default"].StylePrompt);
    }

    [Fact]
    public void Serialize_Empty_RoundTripsToEmptyWithDefaultProfile()
    {
        var reparsed = DictionaryLoader.Parse(DictionaryLoader.Serialize(DictationDictionary.Empty));
        Assert.Empty(reparsed.Vocabulary);
        Assert.Empty(reparsed.CommonMistranscriptions);
        Assert.True(reparsed.Profiles.ContainsKey("default"));
    }

    [Fact]
    public void WriteToDisk_ThenLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(dir, "dictation", "dictionary.yaml");
        try
        {
            var original = new DictationDictionary(
                new[] { "hello", "world" },
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["hello"] = new[] { "helo" },
                },
                new Dictionary<string, DictationProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DictationProfile("default", CleanupEnabled: true, StylePrompt: null),
                });

            DictionaryLoader.WriteToDisk(path, original);
            var loaded = DictionaryLoader.LoadFromDisk(path);

            Assert.Equal(new[] { "hello", "world" }, loaded.Vocabulary);
            Assert.Equal(new[] { "helo" }, loaded.CommonMistranscriptions["hello"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromDisk_MissingFile_ReturnsEmpty()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".yaml");
        var dict = DictionaryLoader.LoadFromDisk(nowhere);
        Assert.Empty(dict.Vocabulary);
    }

    [Fact]
    public void LoadFromDisk_ValidFile_LoadsContents()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "vocabulary: [hello, world]\n");
            var dict = DictionaryLoader.LoadFromDisk(tmp);
            Assert.Equal(new[] { "hello", "world" }, dict.Vocabulary);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
