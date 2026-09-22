using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The dictation judge as one bounded, stateless chat-completions call to the DevThrottle inference
/// proxy - the thing that decides whether an unlisted word was really misheard.
///
/// This is the language model coming back to dictation cleanup, and it is worth being exact about what
/// changed since the o4-mini pass was removed in July. That one was handed the whole transcript and
/// asked to work out what was wrong: an open-ended job that cost about five seconds on every turn. This
/// one is asked a closed question - here is a sentence, here are at most a dozen candidates our own
/// code already isolated, which are real - and answers with nothing but their numbers. It is skipped
/// entirely when the matcher nominates nothing, which is most utterances.
///
/// What it can do to the user's words is bounded by the interface, not by trust: it returns ids, so it
/// cannot rewrite, reword, summarize, refuse into the transcript, or name a span nobody offered. The
/// worst a broken or hostile backend achieves is accepting a bad candidate - which is exactly what the
/// deterministic matcher did unsupervised - and it can never invent one.
///
/// The credential is Bearer-presented and NEVER logged (security rule DT-05). Neither is the transcript:
/// this class logs candidate COUNTS, timings and status codes, never the user's words or the reply text.
/// The model is the PROVEN included type (issue #1360), so the deployment credential cannot be pointed
/// at a catalog model that would bill a member's credits for an internal feature.
/// </summary>
public sealed class HostedCandidateJudge : ICandidateJudge
{
    /// <summary>
    /// The deadline on one ruling, and it is deliberately tight.
    ///
    /// This sits in the middle of every dictation turn, so it is not allowed to become the reason
    /// dictation feels slow. Transcription itself is the dominant cost at roughly 4.2 seconds median; a
    /// judge that answers in a few hundred milliseconds is invisible next to that, and one that takes
    /// seconds is the five-second regression that got the previous model pass deleted. Past this bound
    /// the call is abandoned and the turn ships the words the user said.
    ///
    /// There is NO retry. A retry doubles the worst case on the one path where the worst case is the
    /// whole problem, to recover a correction whose absence costs a wrong spelling.
    /// </summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Shared client with an infinite timeout: the deadline is owned per call by a linked
    /// token, so the client never imposes a second, racing bound (the HostedInferenceBrain lesson).</summary>
    private static readonly HttpClient SharedHttp = Traffic.OutboundTraffic.CreateClient(Timeout.InfiniteTimeSpan);

    private readonly HttpClient _http;
    private readonly string _chatUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _callTimeout;
    private readonly Action<string> _log;

    /// <param name="baseUrl">The provider-compatible <c>/v1</c> base URL.</param>
    /// <param name="apiKey">Credential presented as the Bearer token. Never logged.</param>
    /// <param name="model">The chat model id as the PROVEN included type - a raw string cannot reach
    /// the wire, because the only mint path is <see cref="IncludedModelId"/>.</param>
    /// <param name="http">HTTP client; tests inject one over a fake handler.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    /// <param name="callTimeout">Per-call deadline; <see cref="DefaultCallTimeout"/> when null.</param>
    public HostedCandidateJudge(
        string baseUrl,
        string apiKey,
        IncludedModelId model,
        HttpClient? http = null,
        Action<string>? log = null,
        TimeSpan? callTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        ArgumentNullException.ThrowIfNull(model);
        _http = http ?? SharedHttp;
        _chatUrl = baseUrl.TrimEnd('/') + "/chat/completions";
        _apiKey = apiKey ?? "";
        _model = model.Value;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _log = log ?? FileLog.Write;
    }

    /// <summary>
    /// Ask for a ruling. Returns null for every unhappy path - unreachable, non-success status, past the
    /// deadline, cancelled, or a reply that is not exactly the shape we asked for - because the caller
    /// treats null as "no ruling" and leaves the transcript alone. Nothing here throws: a judge that
    /// throws into the dictation path would turn a missing correction into a failed turn.
    /// </summary>
    public async Task<IReadOnlyList<int>?> AcceptAsync(
        string utterance,
        IReadOnlyList<JudgeCandidate> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Array.Empty<int>();

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_callTimeout);

            var payload = JsonSerializer.Serialize(new
            {
                model = _model,
                temperature = 0,
                messages = new object[]
                {
                    new { role = "system", content = CandidateJudgeProtocol.SystemPrompt },
                    new { role = "user", content = CandidateJudgeProtocol.BuildUserPrompt(utterance, candidates) },
                },
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                sw.Stop();
                _log($"[HostedCandidateJudge] no ruling: HTTP {(int)res.StatusCode} in {sw.Elapsed.TotalMilliseconds:0.###}ms");
                return null;
            }

            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var reply = ExtractContent(body);
            var accepted = CandidateJudgeProtocol.ParseAccepted(
                reply, candidates.Select(c => c.Id).ToArray());

            sw.Stop();
            _log(accepted is null
                ? $"[HostedCandidateJudge] no ruling: reply was not the shape asked for, on "
                  + $"{candidates.Count} candidate(s) in {sw.Elapsed.TotalMilliseconds:0.###}ms"
                : $"[HostedCandidateJudge] ruled on {candidates.Count} candidate(s), accepted "
                  + $"{accepted.Count} in {sw.Elapsed.TotalMilliseconds:0.###}ms");
            return accepted;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _log($"[HostedCandidateJudge] no ruling: past the {_callTimeout.TotalMilliseconds:0}ms deadline "
                 + $"(or cancelled) after {sw.Elapsed.TotalMilliseconds:0.###}ms");
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log($"[HostedCandidateJudge] no ruling: {ex.GetType().Name} after {sw.Elapsed.TotalMilliseconds:0.###}ms");
            return null;
        }
    }

    /// <summary>Pull <c>choices[0].message.content</c> out of a chat-completions body. Null when the
    /// body is not that shape - which the caller reads as no ruling, like every other unhappy path.</summary>
    internal static string? ExtractContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
