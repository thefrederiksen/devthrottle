using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.VoskStt;

/// <summary>
/// Custom dictionary for correcting Vosk transcription output.
/// Uses case-insensitive exact matching first, then Soundex phonetic matching.
/// Thread-safe. Persists to JSON at %LOCALAPPDATA%/cc-director/custom-dictionary.json.
/// </summary>
public sealed class CustomDictionary
{
    private static readonly string DictionaryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director",
        "custom-dictionary.json");

    private readonly object _lock = new();
    private string[] _words = [];

    // Pre-computed Soundex index: soundex code -> list of dictionary words with that code
    private Dictionary<string, List<string>> _soundexIndex = new();

    // Pre-computed sorted entries for matching (longest first)
    private SortedEntry[] _sortedEntries = [];

    public event Action? WordsChanged;

    public CustomDictionary()
    {
        Load();
    }

    public string[] GetWords()
    {
        lock (_lock)
            return [.. _words];
    }

    public void SetWords(string[] words)
    {
        FileLog.Write($"[CustomDictionary] SetWords: {words.Length} entries");
        lock (_lock)
        {
            _words = words.Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
            RebuildIndexes();
        }
        Save();
        WordsChanged?.Invoke();
    }

    /// <summary>
    /// Correct transcription by replacing recognized words with dictionary entries.
    /// First attempts case-insensitive exact match, then Soundex phonetic match.
    /// Multi-word entries are matched first (longest match wins).
    /// </summary>
    public string CorrectTranscription(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Remove [unk] tokens from Vosk grammar mode
        text = text.Replace("[unk]", "").Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        lock (_lock)
        {
            if (_words.Length == 0)
                return text;

            var inputWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return MatchWords(inputWords);
        }
    }

    private string MatchWords(string[] inputWords)
    {
        var result = new List<string>();
        var i = 0;

        while (i < inputWords.Length)
        {
            var matched = false;

            foreach (var entry in _sortedEntries)
            {
                var partCount = entry.Parts.Length;
                if (i + partCount > inputWords.Length)
                    continue;

                // Try exact case-insensitive match on multi-word span
                var span = inputWords.Skip(i).Take(partCount).ToArray();
                if (SpanMatchesExact(span, entry.Parts))
                {
                    FileLog.Write($"[CustomDictionary] Exact match: \"{string.Join(" ", span)}\" -> \"{entry.Entry}\"");
                    result.Add(entry.Entry);
                    i += partCount;
                    matched = true;
                    break;
                }

                // Try Soundex match (single-word entries only for phonetic matching)
                if (partCount == 1 && SpanMatchesSoundex(span[0], entry.Parts[0]))
                {
                    FileLog.Write($"[CustomDictionary] Soundex match: \"{span[0]}\" -> \"{entry.Entry}\"");
                    result.Add(entry.Entry);
                    i += 1;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                result.Add(inputWords[i]);
                i++;
            }
        }

        return string.Join(" ", result);
    }

    private static bool SpanMatchesExact(string[] input, string[] dictParts)
    {
        for (var j = 0; j < dictParts.Length; j++)
        {
            if (!string.Equals(input[j], dictParts[j], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private bool SpanMatchesSoundex(string inputWord, string dictWord)
    {
        // Don't Soundex-match very short words (too many false positives)
        if (inputWord.Length < 3)
            return false;

        var inputCode = ComputeSoundex(inputWord);
        return _soundexIndex.TryGetValue(inputCode, out var matches)
            && matches.Any(m => string.Equals(m, dictWord, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildIndexes()
    {
        // Soundex index
        var index = new Dictionary<string, List<string>>();
        foreach (var word in _words)
        {
            foreach (var part in word.Split(' '))
            {
                if (part.Length < 2) continue;
                var code = ComputeSoundex(part);
                if (!index.TryGetValue(code, out var list))
                {
                    list = [];
                    index[code] = list;
                }
                list.Add(part);
            }
        }
        _soundexIndex = index;

        // Pre-sorted entries for matching (longest first)
        _sortedEntries = _words
            .Select(w => new SortedEntry(w, w.Split(' ')))
            .OrderByDescending(e => e.Parts.Length)
            .ToArray();
    }

    /// <summary>
    /// American Soundex algorithm. Returns a 4-character code (letter + 3 digits).
    /// Maps phonetically similar names to the same code.
    /// </summary>
    internal static string ComputeSoundex(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return "0000";

        var cleaned = new string(word.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (cleaned.Length == 0)
            return "0000";

        var result = new char[4];
        result[0] = cleaned[0];
        var count = 1;
        var lastCode = SoundexDigit(cleaned[0]);

        for (var i = 1; i < cleaned.Length && count < 4; i++)
        {
            var code = SoundexDigit(cleaned[i]);
            if (code != '0' && code != lastCode)
            {
                result[count++] = code;
            }
            lastCode = code;
        }

        while (count < 4)
            result[count++] = '0';

        return new string(result);
    }

    private static char SoundexDigit(char c)
    {
        return char.ToUpperInvariant(c) switch
        {
            'B' or 'F' or 'P' or 'V' => '1',
            'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
            'D' or 'T' => '3',
            'L' => '4',
            'M' or 'N' => '5',
            'R' => '6',
            _ => '0', // A, E, I, O, U, H, W, Y
        };
    }

    private void Load()
    {
        if (!File.Exists(DictionaryPath))
        {
            FileLog.Write($"[CustomDictionary] No dictionary file found at {DictionaryPath}, starting empty");
            _words = [];
            RebuildIndexes();
            return;
        }

        var json = File.ReadAllText(DictionaryPath);
        var data = JsonSerializer.Deserialize<DictionaryData>(json);
        _words = data?.Words ?? [];
        RebuildIndexes();
        FileLog.Write($"[CustomDictionary] Loaded {_words.Length} words from disk");
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(DictionaryPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        string[] snapshot;
        lock (_lock)
            snapshot = [.. _words];

        var data = new DictionaryData { Words = snapshot };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(DictionaryPath, json);
        FileLog.Write($"[CustomDictionary] Saved {snapshot.Length} words to disk");
    }

    private sealed class DictionaryData
    {
        public string[] Words { get; set; } = [];
    }

    private sealed record SortedEntry(string Entry, string[] Parts);
}
