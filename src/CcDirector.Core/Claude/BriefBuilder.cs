using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// Builds the data for the Cockpit's full-page session Brief (docs/plans/cockpit-brief-view.md):
/// what the user asked, what the agent did (condensed), what the agent needs (verbatim).
///
/// Two halves:
///  - <see cref="Extract"/>: pure, testable extraction from the parsed JSONL widget stream
///    (first/last user prompt, full last assistant reply).
///  - <see cref="CondenseAsync"/>: a direct OpenAI chat call (the CleanupOrchestrator pattern,
///    NOT a claude --print side-spawn - issue #142 proved the cold-spawn latency kills
///    per-flip UX) that produces the DID bullets and extracts the NEEDS-YOU sentence(s).
///
/// Fidelity invariant: the needs-you text shown to the user must be VERBATIM. The model's
/// extraction is validated as a substring of the reply (whitespace-tolerant); when validation
/// fails the caller falls back to <see cref="FallbackNeedsYou"/>, which is verbatim by
/// construction. A paraphrased question is never shown.
/// </summary>
public sealed class BriefBuilder : IDisposable
{
    /// <summary>Default condenser model. Mini-tier: nano mangles structured extraction.</summary>
    public const string DefaultModel = "gpt-4.1-mini";

    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Max reply characters fed to the condenser (tail-biased: the ask is at the end).</summary>
    private const int CondenserInputMaxChars = 16_000;

    /// <summary>Display truncation caps (the FULL reply is never truncated in the response).</summary>
    public const int GoalMaxChars = 500;
    public const int LastAskMaxChars = 2_000;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _model;

    /// <summary>Model identity string reported in <c>BriefResponse.Condenser</c>.</summary>
    public string CondenserId => $"openai:{_model}";

    private BriefBuilder(string apiKey, string model, HttpClient? httpClient)
    {
        _apiKey = apiKey;
        _model = model;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = HttpTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    /// <summary>
    /// Create a condenser, or null when no OpenAI key is available. A null condenser is the
    /// EXPLICIT degrade path: the brief endpoint still serves the raw extraction and reports
    /// <c>Condenser = "unavailable"</c> - visible, never silent.
    /// </summary>
    public static BriefBuilder? TryCreate(string model = DefaultModel, HttpClient? httpClient = null)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            FileLog.Write("[BriefBuilder] TryCreate: OPENAI_API_KEY not set; condenser unavailable");
            return null;
        }
        return new BriefBuilder(key.Trim(), string.IsNullOrWhiteSpace(model) ? DefaultModel : model, httpClient);
    }

    // ====================================================================
    // Extraction (pure)
    // ====================================================================

    /// <summary>
    /// Raw brief facts pulled from the widget stream. No LLM involved.
    /// <see cref="ReplyPending"/>: the transcript's last user prompt has NO assistant text
    /// after it. Either the agent is still replying, or it is blocked inside an interactive
    /// tool (the AskUserQuestion picker) whose turn claude only flushes to the JSONL after
    /// completion - in both cases the transcript cannot describe the current turn, so the
    /// brief must not condense (it would summarize the PREVIOUS turn against the NEW ask).
    /// </summary>
    public sealed record BriefExtract(
        string? FirstUserPrompt,
        string? LastUserPrompt,
        string? LastAssistantText,
        int TurnCount,
        bool ReplyPending);

    /// <summary>
    /// Walk the widget stream once and pull the brief's raw facts. Unlike
    /// <see cref="SummaryBuilder"/> this keeps the last assistant reply UNTRUNCATED -
    /// it is both the [full reply] payload and the substring-validation reference.
    /// </summary>
    public static BriefExtract Extract(IReadOnlyList<TurnWidgetDto> widgets)
    {
        ArgumentNullException.ThrowIfNull(widgets);

        string? first = null;
        string? lastUser = null;
        string? lastText = null;
        var lastUserIdx = -1;
        var lastTextIdx = -1;
        for (var i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            switch (w.Kind)
            {
                case "UserMessage":
                    first ??= w.Content;
                    lastUser = w.Content;
                    lastUserIdx = i;
                    break;
                case "Text":
                    lastText = w.Content;
                    lastTextIdx = i;
                    break;
            }
        }
        return new BriefExtract(
            NullIfBlank(first),
            NullIfBlank(lastUser),
            NullIfBlank(lastText),
            widgets.Count,
            ReplyPending: lastUserIdx >= 0 && lastUserIdx > lastTextIdx);
    }

    /// <summary>
    /// Verbatim-by-construction needs-you fallback: the reply's final non-empty paragraph
    /// (capped). Used when the condenser is unavailable or its extraction fails validation.
    /// </summary>
    public static string? FallbackNeedsYou(string? reply, int maxChars = 600)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var paragraphs = reply.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        for (var i = paragraphs.Length - 1; i >= 0; i--)
        {
            var p = paragraphs[i].Trim();
            if (p.Length == 0) continue;
            return p.Length <= maxChars ? p : p[^maxChars..];
        }
        return null;
    }

    /// <summary>
    /// Locate <paramref name="candidate"/> inside <paramref name="reply"/> tolerating
    /// whitespace differences, and return the reply's OWN text for that span (so the result
    /// is verbatim even when the model normalized spacing). Null when not found.
    /// </summary>
    public static string? FindVerbatim(string reply, string candidate)
    {
        if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(candidate)) return null;

        var exact = reply.IndexOf(candidate, StringComparison.Ordinal);
        if (exact >= 0) return candidate;

        // Whitespace-tolerant: every token literally, any whitespace run between them.
        var tokens = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var pattern = string.Join(@"\s+", tokens.Select(Regex.Escape));
        try
        {
            var m = Regex.Match(reply, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return m.Success ? m.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            FileLog.Write("[BriefBuilder] FindVerbatim: regex timeout; rejecting extraction");
            return null;
        }
    }

    // ====================================================================
    // Condensation (OpenAI)
    // ====================================================================

    /// <summary>Condenser output. NeedsYouVerbatim is already substring-validated (or null).</summary>
    public sealed record Condensation(List<string> Bullets, string? NeedsYouVerbatim);

    /// <summary>
    /// One chat call: DID bullets + the verbatim needs-you extraction. Returns null on any
    /// failure (HTTP error, bad JSON) - the caller serves the raw brief with
    /// Condenser="unavailable" rather than guessing. Throws only on cancellation.
    /// </summary>
    public async Task<Condensation?> CondenseAsync(string? lastUserPrompt, string reply, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reply);
        FileLog.Write($"[BriefBuilder] CondenseAsync: model={_model}, replyChars={reply.Length}");

        var input = reply.Length <= CondenserInputMaxChars ? reply : reply[^CondenserInputMaxChars..];
        try
        {
            var content = await CallOpenAiAsync(lastUserPrompt, input, ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                FileLog.Write("[BriefBuilder] CondenseAsync: model returned empty content");
                return null;
            }

            using var doc = JsonDocument.Parse(content);
            var bullets = new List<string>();
            if (doc.RootElement.TryGetProperty("did", out var did) && did.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in did.EnumerateArray())
                {
                    var s = b.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) bullets.Add(s.Trim());
                }
            }

            string? needsYou = null;
            if (doc.RootElement.TryGetProperty("needs_you", out var ny) && ny.ValueKind == JsonValueKind.String)
            {
                var candidate = ny.GetString();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    needsYou = FindVerbatim(reply, candidate.Trim());
                    if (needsYou is null)
                        FileLog.Write("[BriefBuilder] CondenseAsync: needs_you failed substring validation; dropping");
                }
            }

            FileLog.Write($"[BriefBuilder] CondenseAsync done: bullets={bullets.Count}, needsYou={(needsYou is null ? "null" : "verbatim")}");
            return new Condensation(bullets, needsYou);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BriefBuilder] CondenseAsync FAILED: {ex.Message}");
            return null;
        }
    }

    private async Task<string> CallOpenAiAsync(string? lastUserPrompt, string reply, CancellationToken ct)
    {
        const string system =
            "You brief a busy engineering lead returning to an AI coding agent's session. " +
            "Respond ONLY with a JSON object: {\"did\": [...], \"needs_you\": string-or-null}. " +
            "\"did\": 3-6 bullets describing what the agent concretely did or decided in its reply " +
            "- past tense, specific, max ~15 words each, no fluff, no markdown. " +
            "\"needs_you\": the sentence(s) where the agent asks the user a question or for a " +
            "decision/approval, copied EXACTLY character-for-character from the reply (never " +
            "paraphrase, never trim words); null if the reply asks nothing of the user.";

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lastUserPrompt))
        {
            user.AppendLine("THE USER ASKED:");
            user.AppendLine(lastUserPrompt.Trim());
            user.AppendLine();
        }
        user.AppendLine("THE AGENT'S REPLY:");
        user.Append(reply);

        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user.ToString() },
            },
            temperature = 0.0,
            response_format = new { type = "json_object" },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI chat completions failed: HTTP {(int)resp.StatusCode} - {Truncate(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return "";
        return choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
