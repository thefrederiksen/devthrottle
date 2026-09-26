using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The wingman as a STATELESS, hosted chat-completions call (the account-first hosted-AI direction).
/// Unlike the warm <c>claude.exe</c> brain, this makes one provider-compatible
/// <c>POST {base}/chat/completions</c> per ask to the DevThrottle inference proxy. The base URL,
/// credential name, and model all come from the one routing spot
/// (<see cref="Core.Configuration.TranscriptionEndpointResolver.ResolveWingman"/>).
///
/// It implements <see cref="IAgentBrain"/> so <see cref="WingmanTranslator"/> - which only ever calls
/// <see cref="AskAsync"/> then <see cref="ClearAsync"/> - drives it unchanged. There is no process and
/// no conversation state, so clear/cancel/restart/kill are no-ops and each ask stands alone (which is
/// exactly what the translator wants: it clears context between every translation anyway).
///
/// The credential is Bearer-presented and NEVER logged (security rule DT-05): this class logs only the
/// outcome (status code, byte counts), never the key or the prompt/reply text.
/// </summary>
public sealed class HostedInferenceBrain : IAgentBrain
{
    /// <summary>
    /// The bound on ONE model round trip, and the whole reason this class owns a deadline at all.
    ///
    /// This client used to carry a flat <c>Timeout = TimeSpan.FromMinutes(3)</c> and nothing else. When
    /// a hosted worker stalled (a cold start, a slow provider minute), the translation call did not fail
    /// - it BLOCKED for the full three minutes and then threw. That is the wingman's model leg, which the
    /// voice path runs on every turn-end and on the manual "generate" button, so a single stalled call
    /// froze a session's voice for three minutes and then died with a raw cancellation - logged as a
    /// silent FAILED on the auto path, and returned as a 502 the phone mislabelled "this session's
    /// computer is offline" on the manual path. Measured on 2026-07-17: many translations finished in
    /// ~10-20 seconds while the stalls went the full 180 (see the Gateway log's "HttpClient.Timeout of
    /// 180 seconds elapsing" lines). The speech leg was already bounded and fast-failed to a Retrying
    /// state (issue #1322 / the 2026-07-15 work); the model leg was left with this naive client and
    /// skipped all of it.
    ///
    /// So the deadline is bounded and OWNED here (one timeout, one owner, matching TtsSynthesis and the
    /// shared speech client): the static client's own timeout is INFINITE and each call is bounded by a
    /// linked CancellationTokenSource. A stall now fails in <see cref="DefaultCallTimeout"/>, not 180s,
    /// as a clear <see cref="TimeoutException"/> the caller maps to "audio on its way, retrying" and
    /// retries on its own (the voice sweep). 60 seconds is deliberately generous over the ~10-20s a real
    /// translation takes - the goal is to end the pathological three-minute hang without false-timing-out
    /// a legitimately slow-but-working call; tune it down only with measurements.
    /// </summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Shared client: the deadline is owned per-call by a linked token (see
    /// <see cref="DefaultCallTimeout"/>), so the client itself never imposes a second, racing bound.</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly string _chatUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _callTimeout;
    private readonly bool _thinkingOff;
    private readonly double? _temperature;
    private readonly int? _maxTokens;
    private readonly AiCallTag? _tag;
    private readonly Action<string> _log;

    /// <param name="baseUrl">The provider-compatible <c>/v1</c> base URL.</param>
    /// <param name="apiKey">The credential to present as the Bearer token. Must be non-empty.</param>
    /// <param name="model">The chat model id, as the PROVEN included type (issue #1360). This class is
    /// only ever constructed with the DevThrottle deployment credential, so the model must be an
    /// internal included id - a catalog id would bill credits on an internal feature. The guard is the
    /// TYPE, not a check in this constructor: an earlier base-URL string-equality check here was
    /// bypassed by construction with the equivalent <c>https://devthrottle.com:443/api/v1</c> spelling
    /// (phase-2 inspection round 2), so this constructor no longer tries to recognize the endpoint -
    /// it simply cannot be handed an unvalidated string. The only mint path is
    /// <see cref="Core.Configuration.IncludedModelId"/>.</param>
    /// <param name="http">HTTP client (tests inject a stub over a fake handler); a shared client when null.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    /// <param name="callTimeout">Per-call deadline (tests pass a tiny value to prove the fast-fail without
    /// a real wait); <see cref="DefaultCallTimeout"/> when null.</param>
    /// <param name="thinkingOff">Ask the model NOT to reason out loud before it answers - see
    /// <see cref="ThinkingOffTemplateArgument"/>. OFF by default, so a brain built anywhere else keeps the
    /// behaviour it has today; only a caller that has MEASURED its model both ways turns it on.</param>
    /// <param name="tag">What this brain's calls are FOR and which account they are for, sent on every call so
    /// the API records it (see <see cref="AiCallTag"/>). Every production construction passes one - a test
    /// pins that - and null sends no tag, which the API records as untagged.</param>
    /// <param name="temperature">The sampling temperature to ask for, or null to send none and take the host's
    /// default. Only a caller that measured its answers at a fixed temperature sets it: Call A answers at 0, because
    /// without it the same prompt flipped between red and calm on 23 of 381 stops (turn pipeline phase 1).</param>
    /// <param name="maxTokens">The most output the model may write, or null to send no cap. Only a caller whose
    /// whole answer is known to be short sets it: Call A answers one word.</param>
    public HostedInferenceBrain(string baseUrl, string apiKey, Core.Configuration.IncludedModelId model, HttpClient? http = null, Action<string>? log = null, TimeSpan? callTimeout = null, bool thinkingOff = false, AiCallTag? tag = null, double? temperature = null, int? maxTokens = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        ArgumentNullException.ThrowIfNull(model);
        _http = http ?? SharedHttp;
        _chatUrl = baseUrl.TrimEnd('/') + "/chat/completions";
        _apiKey = apiKey ?? "";
        _model = model.Value;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _thinkingOff = thinkingOff;
        _temperature = temperature;
        _maxTokens = maxTokens;
        _tag = tag;
        _log = log ?? FileLog.Write;
    }

    /// <summary>
    /// THE ONE ARGUMENT THAT TURNS THE REASONING OFF, and why it is a request field rather than a prompt line.
    ///
    /// A reasoning model writes its working out before its answer, and that working is billed and waited for
    /// like any other output. Measured on this account on 2026-09-20: <c>devthrottle/wingman</c> produced 4,290
    /// output tokens a call with reasoning on, cost about $0.0102 and answered in roughly 144 seconds at the
    /// median - which is why the judge was put on the fast tier in the first place. The SAME model with this
    /// argument set answers the same question in 1.3 seconds for about $0.0008, and is right on 85 of 85 of the
    /// picker screens rather than 72.
    ///
    /// It rides the request as <c>chat_template_kwargs</c>, which the hosted proxy passes upstream untouched
    /// (<c>buildUpstreamRequest</c> spreads the client body), so nothing on the website has to know about it.
    /// A model that does not understand the argument ignores it: it is a chat-template variable, not an API
    /// parameter, so this cannot fail a call for a model that has no reasoning to turn off.
    /// </summary>
    private const string ThinkingOffTemplateArgument = "chat_template_kwargs";

    /// <summary>Whether this brain asks its model not to reason out loud. Read by the tests that pin WHICH
    /// callers turn it on - the judge does, and nothing else may without its own measurement.</summary>
    internal bool ThinkingOff => _thinkingOff;

    /// <summary>The temperature this brain asks for, or null when it sends none.</summary>
    internal double? Temperature => _temperature;

    /// <summary>The output cap this brain sends, or null when it sends none.</summary>
    internal int? MaxTokens => _maxTokens;

    /// <summary>The deadline one round trip on this brain is held to. Read by the test that pins the turn
    /// verdict judge to its measured thirty seconds rather than to <see cref="DefaultCallTimeout"/>.</summary>
    internal TimeSpan CallTimeout => _callTimeout;

    /// <summary>The tag every call from this brain carries. Read by the tests that pin each call site's feature.</summary>
    internal AiCallTag? Tag => _tag;

    /// <summary>Stateless - there is no agent-internal session.</summary>
    public string? SessionId => null;

    /// <summary>
    /// One chat-completions round trip: POST the prompt as a single user message and return the
    /// assistant's text. A missing credential or a non-success response throws with the fix named
    /// (no-fallback rule) - the caller surfaces it rather than speaking a wrong or empty summary.
    /// </summary>
    public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "[HostedInferenceBrain] No DevThrottle account key is configured. Sign in to DevThrottle " +
                "so the wingman can reach the model.");

        // The body is built as a dictionary rather than an anonymous type because the thinking argument is
        // present or ABSENT - never present-and-false-shaped. A model that reasons by default must see no
        // such key at all when this brain is not the judge's.
        var request = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["stream"] = false,
        };
        if (_thinkingOff)
            request[ThinkingOffTemplateArgument] = new Dictionary<string, object> { ["thinking"] = false };
        if (_temperature is { } temperature)
            request["temperature"] = temperature;
        if (_maxTokens is { } maxTokens)
            request["max_tokens"] = maxTokens;
        var payload = JsonSerializer.Serialize(request);

        var sw = Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _tag?.ApplyTo(req);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Bound this one round trip (see DefaultCallTimeout). The linked source cancels on EITHER the
        // caller's token or our deadline; we then tell the two apart below so a real caller-cancel is
        // never mislabelled a timeout and vice versa.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_callTimeout);
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's token. This is the stall the bound exists for: fail
            // fast and clearly as a TimeoutException so the voice path maps it to Retrying ("audio on
            // its way") and retries on its own, instead of blocking three minutes then dying.
            sw.Stop();
            _log($"[HostedInferenceBrain] chat/completions model={_model} did not answer within {_callTimeout.TotalSeconds:F0}s - giving up this attempt (the session retries on its own)");
            throw new TimeoutException(
                $"The wingman model call did not answer within {_callTimeout.TotalSeconds:F0} seconds.");
        }
        sw.Stop();

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[HostedInferenceBrain] chat/completions model={_model} -> {(int)resp.StatusCode} ({text.Length} bytes)");
                // Out of credits / monthly cap (issue #939): use the ONE shared message, branched by the
                // 402 code (insufficient_credits vs monthly_limit_reached) - not a hand-written string that
                // can only say "out of credits" and drifts from the other surfaces.
                var detail = resp.StatusCode == System.Net.HttpStatusCode.PaymentRequired
                    ? HostedAiMessages.For(HostedAiErrorMapper.Map402(text)).Text
                    : "Check the AI provider settings and that the account/key is valid.";
                // Rate limited (issue #1324): surface a TYPED signal carrying the provider's Retry-After so
                // the caller can back off for exactly as long as asked instead of hammering it into a 429
                // storm. It extends InvalidOperationException, so every existing catch stays unchanged.
                if ((int)resp.StatusCode == 429)
                    throw new WingmanModelRateLimitedException(
                        $"The wingman model call failed: {(int)resp.StatusCode} {resp.StatusCode}. " + detail,
                        CcDirector.Core.HostedAi.RetryAfterHeader.Parse(resp.Headers.RetryAfter));
                throw new InvalidOperationException(
                    $"The wingman model call failed: {(int)resp.StatusCode} {resp.StatusCode}. " + detail);
            }
        }

        var content = ExtractContent(text);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                "[HostedInferenceBrain] The model returned an empty message for a non-empty prompt.");

        _log($"[HostedInferenceBrain] chat/completions model={_model} OK: {content.Length} chars in {sw.Elapsed.TotalSeconds:F1}s");
        return new AskResult { Text = content, ReplySeconds = sw.Elapsed.TotalSeconds };
    }


    /// <summary>
    /// Pull the assistant message text out of a provider-compatible chat-completions response
    /// (<c>choices[0].message.content</c>). Internal so a test can assert the parse. Returns "" when
    /// the shape is unexpected (the caller treats an empty result as a failure).
    /// </summary>
    internal static string ExtractContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    return contentEl.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // An unparseable body is treated as an empty result by the caller (no-fallback: it throws).
        }
        return "";
    }

    // --- Stateless: no process, no conversation to reset or recover. ---

    /// <summary>No-op: there is no running turn to abort.</summary>
    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: each ask is independent, so there is no context to clear.</summary>
    public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());

    /// <summary>No-op: there is no process to restart.</summary>
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: there is no process to kill.</summary>
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Alive when a credential is configured; nothing to spawn.</summary>
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var hasKey = !string.IsNullOrWhiteSpace(_apiKey);
        return Task.FromResult(new BrainHealth
        {
            IsAlive = hasKey,
            Status = hasKey ? "Running" : "NotStarted",
            ActivityState = hasKey ? "Quiet" : "NotStarted",
        });
    }

    /// <summary>Nothing owned to dispose (the HTTP client is shared).</summary>
    public void Dispose() { }
}
