using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Runs a final transcript through an OpenAI chat-completion model to repair
/// mistranscriptions and apply per-profile style. The vocabulary and known
/// mistranscription patterns from the dictionary are passed as the system
/// prompt so the model can both fix listed cases and generalize to near
/// misses.
///
/// Defaults to <c>gpt-4o-mini</c> (cheap, fast, plenty smart for this task);
/// callers can specify any chat-completion-capable OpenAI model. The
/// <c>nano</c> tier (<c>gpt-4.1-nano</c>, etc.) typically gives ~1 second
/// latency at fractional-cent cost per call.
///
/// Fails open: on any error (network failure, bad response, etc.) the
/// returned <see cref="CleanupOutcome"/> carries the raw transcript verbatim
/// and a failure reason. Callers should ship the raw text rather than block.
///
/// History: an earlier version of this class shelled out to
/// <c>claude --print --model haiku</c>, which carried ~10-20 seconds of
/// process-spawn and CLI bootstrap overhead per call. The HTTP variant is
/// 5-10x faster and uses the same API key we already have for transcription.
/// </summary>
public sealed class CleanupOrchestrator : IDisposable
{
    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string DefaultModel = "gpt-4o-mini";
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _model;

    /// <param name="apiKey">OpenAI API key. Reads <c>OPENAI_API_KEY</c> env var if blank.</param>
    /// <param name="model">Chat model. Defaults to <c>gpt-4o-mini</c>.</param>
    /// <param name="httpClient">Optional shared HttpClient. Provider creates and owns one if null.</param>
    public CleanupOrchestrator(
        string? apiKey = null,
        string model = DefaultModel,
        HttpClient? httpClient = null)
    {
        _apiKey = ResolveApiKey(apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = HttpTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <summary>
    /// Clean a raw transcript using the dictionary and the specified profile.
    /// Returns the original text unchanged when cleanup is disabled or fails.
    /// </summary>
    public async Task<CleanupOutcome> CleanAsync(
        string rawTranscript,
        DictationDictionary dictionary,
        string profileName,
        CancellationToken ct = default)
    {
        FileLog.Write($"[CleanupOrchestrator] CleanAsync: profile={profileName}, model={_model}, len={rawTranscript?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");

        var profile = ResolveProfile(dictionary, profileName);
        if (!profile.CleanupEnabled)
        {
            FileLog.Write($"[CleanupOrchestrator] CleanAsync: cleanup disabled for profile '{profile.Name}', returning verbatim");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: $"profile '{profile.Name}' has cleanup disabled");
        }

        var systemPrompt = BuildSystemPrompt(dictionary, profile);

        var sw = Stopwatch.StartNew();
        try
        {
            var cleaned = await CallOpenAiAsync(systemPrompt, rawTranscript, ct);
            sw.Stop();
            FileLog.Write($"[CleanupOrchestrator] CleanAsync done in {sw.ElapsedMilliseconds}ms");
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                FileLog.Write("[CleanupOrchestrator] CleanAsync: model returned empty content, falling back to raw");
                return new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup returned empty");
            }
            return new CleanupOutcome(cleaned, Applied: true, Reason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileLog.Write($"[CleanupOrchestrator] CleanAsync FAILED in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Build the system prompt. Exposed internally so tests can inspect it
    /// without invoking the model.
    /// </summary>
    internal static string BuildSystemPrompt(
        DictationDictionary dictionary,
        DictationProfile profile)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are cleaning up a voice dictation transcript from a software engineer.");
        sb.AppendLine();

        if (dictionary.Vocabulary.Count > 0)
        {
            sb.AppendLine(
                "The speaker uses these technical terms and proper nouns, which MUST appear "
                + "correctly in the output (exact capitalization and punctuation):");
            foreach (var term in dictionary.Vocabulary)
                sb.AppendLine($"  - {term}");
            sb.AppendLine();
        }

        if (dictionary.CommonMistranscriptions.Count > 0)
        {
            sb.AppendLine(
                "Speech-to-text often mishears these terms. Here are mistranscription "
                + "patterns observed in real use. When you see one of these in the "
                + "transcript, replace it with the canonical term on the left:");
            foreach (var kv in dictionary.CommonMistranscriptions)
            {
                var quoted = string.Join(", ", kv.Value.Select(v => $"\"{v}\""));
                sb.AppendLine($"  - {kv.Key} : {quoted}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(
            "This list is not exhaustive. If you see a word that is not a standard "
            + "English word AND is a plausible near-miss for one of the listed terms, "
            + "also replace it. When unsure between two possible matches, pick the one "
            + "that fits the sentence context. If a word is truly ambiguous and you "
            + "have no way to decide, leave it alone rather than guessing.");
        sb.AppendLine();

        sb.AppendLine("Also fix obvious filler words (uh, um, like). Preserve all other words exactly as they appear.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(profile.StylePrompt))
        {
            sb.AppendLine($"Style guidance for this profile: {profile.StylePrompt}");
            sb.AppendLine();
        }

        sb.Append("Return ONLY the cleaned transcript text on a single line. No commentary, no quotes, no preamble.");

        return sb.ToString();
    }

    private static DictationProfile ResolveProfile(DictationDictionary dictionary, string profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName)
            && dictionary.Profiles.TryGetValue(profileName, out var found))
            return found;
        if (dictionary.Profiles.TryGetValue("default", out var def))
            return def;
        return new DictationProfile("default", CleanupEnabled: true, StylePrompt: null);
    }

    private async Task<string> CallOpenAiAsync(string systemPrompt, string userText, CancellationToken ct)
    {
        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userText },
            },
            temperature = 0.0,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            FileLog.Write($"[CleanupOrchestrator] HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");
            throw new HttpRequestException(
                $"OpenAI chat completions failed: HTTP {(int)resp.StatusCode} - {Truncate(body, 200)}");
        }

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            return "";
        var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return content.Trim();
    }

    private static string ResolveApiKey(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
            return explicitKey.Trim();
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(env))
            throw new InvalidOperationException(
                "OpenAI API key not provided and OPENAI_API_KEY environment variable is not set.");
        return env.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// Outcome of a single cleanup pass. <see cref="Text"/> always carries
/// something safe to ship: cleaned text on success, raw transcript on
/// failure or when cleanup is disabled for the profile.
/// </summary>
public sealed record CleanupOutcome(string Text, bool Applied, string? Reason);
