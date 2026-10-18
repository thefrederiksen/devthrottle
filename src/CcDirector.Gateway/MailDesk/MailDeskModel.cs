using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>The one email a Mail Desk question is about: what the screening may show the model, and nothing more.
/// None of it is ever written to a log.</summary>
/// <param name="SenderName">The sender's display name; empty when the mailbox gave none.</param>
/// <param name="SenderAddress">The sender's address; empty when the mailbox gave none.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Preview">The start of the body. Only the first <see cref="MailDeskModel.PreviewCharacters"/>
/// characters are shown to the model.</param>
public sealed record MailDeskEmail(string SenderName, string SenderAddress, string Subject, string Preview);

/// <summary>One route the route question offers: its name (the label answered) and the line saying what it handles
/// (the text the model is shown).</summary>
public sealed record MailDeskRouteChoice(string Name, string Handles);

/// <summary>What one Mail Desk question came back as.</summary>
public enum MailDeskAnswerKind
{
    /// <summary>The model answered exactly one listed letter; <see cref="MailDeskModelResult.Label"/> is its label.</summary>
    Label,

    /// <summary>The model answered, but not with exactly one listed letter. The call was made and is paid for.</summary>
    OffLabels,

    /// <summary>No answer could be had: no key, an HTTP error, past the deadline, a reply that is not a chat
    /// completion, or a reply with no token count. <see cref="MailDeskModelResult.ErrorKind"/> says which.</summary>
    Failed,
}

/// <summary>The error kinds a failed question carries - short, fixed words a decision row and a run reason can name.</summary>
public static class MailDeskModelErrorKinds
{
    /// <summary>The Gateway holds no key for the hosted model.</summary>
    public const string NoKey = "no-key";
    /// <summary>A hosted Gateway that cannot name the account on the call (its service credential is missing).</summary>
    public const string NoAccountCredential = "no-account-credential";
    /// <summary>The per-call deadline passed before the reply came.</summary>
    public const string Timeout = "timeout";
    /// <summary>The caller cancelled the question (the whole screening's deadline, or the Gateway stopping).</summary>
    public const string Cancelled = "cancelled";
    /// <summary>The request never got an answer: the connection failed.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>The web client failed in a way none of the other kinds name (a reply it cannot read as text, for
    /// one). The error sentence names the exception's type, never its message.</summary>
    public const string Unexpected = "unexpected";
    /// <summary>The account's plan does not include the Wingman, so the question is not asked.</summary>
    public const string PlanRefused = "plan-refused";
    /// <summary>The account's plan could not be read, so the question is not asked and nothing is said about a plan.</summary>
    public const string PlanUnknown = "plan-unknown";
    /// <summary>The reply was not a chat completion.</summary>
    public const string NotAChatCompletion = "not-a-chat-completion";
    /// <summary>The reply carried no token count, so its cost is unknown and it cannot be held to the spend limit.</summary>
    public const string NoUsage = "no-usage";

    /// <summary>A reply with a status that is not success: <c>http-500</c>, <c>http-402</c>, <c>http-429</c>, ...</summary>
    public static string Http(int status) => "http-" + status.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The typed answer to one Mail Desk question (contract section 5): the label or off-labels or a failure, the
/// confidence, and what the call cost.
/// </summary>
/// <param name="Kind">Which of the three it is.</param>
/// <param name="Label">The answered label when <see cref="Kind"/> is <see cref="MailDeskAnswerKind.Label"/>; otherwise null.</param>
/// <param name="Confidence"><c>exp(logprob)</c> of the reply's first token when that token is the answered letter;
/// null when the reply carried no log probabilities, or for anything that is not a label. Never guessed.</param>
/// <param name="CostUsd">The call's cost from its token counts; null when no reply with a token count came back.</param>
/// <param name="InputTokens">The reply's <c>usage.prompt_tokens</c>; null with <see cref="CostUsd"/>.</param>
/// <param name="OutputTokens">The reply's <c>usage.completion_tokens</c>; null with <see cref="CostUsd"/>.</param>
/// <param name="ErrorKind">One of <see cref="MailDeskModelErrorKinds"/> when failed; otherwise null.</param>
/// <param name="Error">A plain sentence saying why it failed, holding nothing of the email; otherwise null.</param>
public sealed record MailDeskModelResult(
    MailDeskAnswerKind Kind,
    string? Label,
    double? Confidence,
    decimal? CostUsd,
    int? InputTokens,
    int? OutputTokens,
    string? ErrorKind,
    string? Error)
{
    /// <summary>A question that got no answer.</summary>
    public static MailDeskModelResult Failure(string errorKind, string error)
        => new(MailDeskAnswerKind.Failed, null, null, null, null, null, errorKind, error);
}

/// <summary>The Mail Desk's two questions about one email. <see cref="MailDeskModel"/> asks them of the hosted model;
/// <see cref="MailDeskModelFactory"/> hands back one that fails every question when the Gateway cannot ask.</summary>
public interface IMailDeskModel
{
    /// <summary>Layer 3b: is the sender a real person writing to us, or spam? Labels <c>person</c> and <c>spam</c>.</summary>
    Task<MailDeskModelResult> AskPersonOrSpamAsync(MailDeskEmail email, CancellationToken ct = default);

    /// <summary>Layer 4: which route should handle the email? The labels are the routes' names in the order given,
    /// then <c>unsure</c> last. At least one and at most <see cref="MailDeskModel.MaxRoutes"/> routes.</summary>
    Task<MailDeskModelResult> AskRouteAsync(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes, CancellationToken ct = default);
}

/// <summary>
/// The Mail Desk's model call (Mail Desk phase 3 contract, section 5): one stateless chat-completions call to the
/// DevThrottle inference proxy per question, shaped like <see cref="Transcription.HostedCandidateJudge"/>.
///
/// The model only picks a label. It is shown the labels as letters and asked for one letter, with two output tokens
/// at most, and anything but exactly one listed letter is off-labels - so nothing it writes is ever stored, and
/// nothing it answers can do more than choose among the labels the screening offered.
///
/// There is NO retry inside a call. A failed question is a failed decision, and the screening reads the same email
/// again on its next run; a retry here would only double the spend on a backend already failing.
///
/// The credential is Bearer-presented and NEVER logged (security rule DT-05). Nothing of the email is logged either -
/// not the sender, the subject, the preview, nor the reply text (an off-labels reply could echo the email). The log
/// lines carry the question, the number of labels, the status code, the error kind, token counts and timings.
/// </summary>
public sealed class MailDeskModel : IMailDeskModel
{
    /// <summary>The per-call deadline. Past it the question fails with <see cref="MailDeskModelErrorKinds.Timeout"/>.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How much of the body's start the model is shown (phase 0's <c>PREVIEW_CHARS</c>).</summary>
    public const int PreviewCharacters = 200;

    /// <summary>The most routes one route question offers: letters A to Y, with <c>unsure</c> on Z.</summary>
    public const int MaxRoutes = 25;

    /// <summary>The label the route question offers last, for none of the routes.</summary>
    public const string UnsureLabel = "unsure";

    /// <summary>The line the route question shows for <see cref="UnsureLabel"/> (phase 0's <c>routes.json</c>).</summary>
    public const string UnsureHandles = "none of the above, or you cannot tell";

    /// <summary>The labels of the person-or-spam question, in letter order (A, B).</summary>
    public static readonly IReadOnlyList<string> PersonOrSpamLabels = new[] { "person", "spam" };

    internal const string QuestionPersonOrSpam = "person-or-spam";
    internal const string QuestionRoute = "route";

    /// <summary>Shared client with an infinite timeout: the deadline is owned per call by a token, so the client never
    /// imposes a second, racing bound (the HostedInferenceBrain lesson).</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly Regex Placeholder = new(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant);

    internal static readonly string SystemPrompt = ReadPrompt("system.txt").Trim();
    internal static readonly string PersonOrSpamTemplate = ReadPrompt("person_or_spam.txt");
    internal static readonly string RouteTemplate = ReadPrompt("route.txt");

    private readonly HttpClient _http;
    private readonly string _chatUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly AiCallTag _tag;
    private readonly TimeSpan _callTimeout;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    /// <param name="baseUrl">The provider-compatible <c>/v1</c> base URL.</param>
    /// <param name="apiKey">Credential presented as the Bearer token. Never logged.</param>
    /// <param name="model">The chat model, as the PROVEN included type. The contract names
    /// <see cref="IncludedModelId.WingmanFast"/>, and the cost is computed at its price.</param>
    /// <param name="tag">The <c>mail-desk</c> tag with the account the call is for. Required, so the compiler proves
    /// every call is recorded under its feature and its account.</param>
    /// <param name="http">HTTP client; tests inject one over a fake handler.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    /// <param name="callTimeout">Per-call deadline; <see cref="DefaultCallTimeout"/> when null.</param>
    /// <param name="time">The clock the deadline runs on; the system clock when null. Tests inject a virtual one.</param>
    public MailDeskModel(
        string baseUrl,
        string apiKey,
        IncludedModelId model,
        AiCallTag tag,
        HttpClient? http = null,
        Action<string>? log = null,
        TimeSpan? callTimeout = null,
        TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey is required; a Gateway with no key uses MailDeskModelFactory, which fails every question", nameof(apiKey));
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tag);
        _http = http ?? SharedHttp;
        _chatUrl = baseUrl.TrimEnd('/') + "/chat/completions";
        _apiKey = apiKey;
        _model = model.Value;
        _tag = tag;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _time = time ?? TimeProvider.System;
        _log = log ?? FileLog.Write;
    }

    /// <summary>The tag every request carries.</summary>
    internal AiCallTag Tag => _tag;

    /// <inheritdoc />
    public Task<MailDeskModelResult> AskPersonOrSpamAsync(MailDeskEmail email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var prompt = BuildPersonOrSpamPrompt(email);
        return AskAsync(QuestionPersonOrSpam, prompt, PersonOrSpamLabels, ct);
    }

    /// <inheritdoc />
    public Task<MailDeskModelResult> AskRouteAsync(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var labels = RouteLabels(routes);
        var prompt = BuildRoutePrompt(email, routes);
        return AskAsync(QuestionRoute, prompt, labels, ct);
    }

    /// <summary>The person-or-spam question's user prompt for <paramref name="email"/>: phase 0's template, filled.</summary>
    internal static string BuildPersonOrSpamPrompt(MailDeskEmail email)
        => Fill(PersonOrSpamTemplate, EmailFields(email));

    /// <summary>The route question's user prompt: phase 0's template with one lettered line per route, then
    /// <c>unsure</c>, and the letters listed as "A, B or C".</summary>
    internal static string BuildRoutePrompt(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes)
    {
        var handles = RouteChoicesWithUnsure(routes).Select(r => r.Handles).ToList();
        var letters = Letters(handles.Count);
        var fields = EmailFields(email);
        fields["route_lines"] = string.Join("\n", letters.Select((l, i) => $"{l}. {handles[i]}"));
        fields["letters"] = string.Join(", ", letters.Take(letters.Count - 1)) + " or " + letters[^1];
        return Fill(RouteTemplate, fields);
    }

    /// <summary>The route question's labels: the routes' names in order, then <c>unsure</c>.</summary>
    internal static IReadOnlyList<string> RouteLabels(IReadOnlyList<MailDeskRouteChoice> routes)
        => RouteChoicesWithUnsure(routes).Select(r => r.Name).ToList();

    private static IEnumerable<MailDeskRouteChoice> RouteChoicesWithUnsure(IReadOnlyList<MailDeskRouteChoice> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
            throw new ArgumentException("The route question needs at least one route; with none, nothing is asked.", nameof(routes));
        if (routes.Count > MaxRoutes)
            throw new ArgumentException($"The route question offers at most {MaxRoutes} routes (letters A to Y, unsure on Z); {routes.Count} were given.", nameof(routes));
        return routes.Append(new MailDeskRouteChoice(UnsureLabel, UnsureHandles));
    }

    private async Task<MailDeskModelResult> AskAsync(string question, string prompt, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var letters = Letters(labels.Count);
        var started = _time.GetTimestamp();
        _log($"[MailDeskModel] AskAsync: question={question}, labels={labels.Count}");

        using var deadline = new CancellationTokenSource(_callTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = prompt },
                },
                temperature = 0,
                max_tokens = 2,
                logprobs = true,
                top_logprobs = 5,
                stream = false,
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            _tag.ApplyTo(req);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode)
                return Failed(question, started, MailDeskModelErrorKinds.Http(status),
                    $"the hosted model answered HTTP {status}");

            var body = await res.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var reply = ParseReply(body);
            if (reply is null)
                return Failed(question, started, MailDeskModelErrorKinds.NotAChatCompletion,
                    "the hosted model's reply was not a chat completion");
            if (reply.InputTokens is not { } input || reply.OutputTokens is not { } output)
                return Failed(question, started, MailDeskModelErrorKinds.NoUsage,
                    "the hosted model's reply carried no token count, so its cost is unknown");

            var cost = IncludedModelPrice.WingmanFastCostUsd(input, output);
            var answer = reply.Content.Trim();
            var index = IndexOfLetter(letters, answer);
            MailDeskModelResult result = index < 0
                ? new(MailDeskAnswerKind.OffLabels, null, null, cost, input, output, null, null)
                : new(MailDeskAnswerKind.Label, labels[index],
                    reply.FirstToken is { } token && string.Equals(token.Text.Trim(), answer, StringComparison.Ordinal)
                        ? Math.Exp(token.Logprob)
                        : null,
                    cost, input, output, null, null);

            _log($"[MailDeskModel] AskAsync: question={question}, answer={(index < 0 ? "off-labels" : "letter " + letters[index])}, "
                 + $"confidence={(result.Confidence is { } c ? c.ToString("0.###", CultureInfo.InvariantCulture) : "none")}, "
                 + $"tokens={input}+{output}, elapsedMs={Elapsed(started)}");
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Failed(question, started, MailDeskModelErrorKinds.Timeout,
                $"the hosted model did not answer within {_callTimeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException)
        {
            return Failed(question, started, MailDeskModelErrorKinds.Cancelled, "the question was cancelled before the hosted model answered");
        }
        catch (HttpRequestException ex)
        {
            // The exception's message can carry the host, never the credential or the email; only its type is logged.
            return Failed(question, started, MailDeskModelErrorKinds.Unreachable,
                $"the hosted model could not be reached ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            // The external-service boundary, as in HostedCandidateJudge: anything else the web client throws (a
            // success reply whose Content-Type names an unknown character set, for one) is this question's typed
            // failure, never an exception into the screening. Only the type is recorded; the message could carry
            // the host or the body.
            return Failed(question, started, MailDeskModelErrorKinds.Unexpected,
                $"the hosted model's reply could not be read ({ex.GetType().Name})");
        }
    }

    private MailDeskModelResult Failed(string question, long started, string errorKind, string error)
    {
        _log($"[MailDeskModel] AskAsync FAILED: question={question}, errorKind={errorKind}, elapsedMs={Elapsed(started)}");
        return MailDeskModelResult.Failure(errorKind, error);
    }

    private string Elapsed(long started)
        => _time.GetElapsedTime(started).TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static int IndexOfLetter(IReadOnlyList<string> letters, string answer)
    {
        for (var i = 0; i < letters.Count; i++)
            if (string.Equals(letters[i], answer, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static IReadOnlyList<string> Letters(int count)
        => Enumerable.Range(0, count).Select(i => ((char)('A' + i)).ToString()).ToList();

    private static Dictionary<string, string> EmailFields(MailDeskEmail email)
    {
        var preview = email.Preview ?? "";
        if (preview.Length > PreviewCharacters)
            // Never cut a character in half: a surrogate pair straddling the cut is dropped whole.
            preview = preview[..(char.IsHighSurrogate(preview[PreviewCharacters - 1]) ? PreviewCharacters - 1 : PreviewCharacters)];
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sender_name"] = email.SenderName ?? "",
            ["sender_address"] = email.SenderAddress ?? "",
            ["subject"] = email.Subject ?? "",
            ["preview"] = preview,
        };
    }

    // One pass over the template, so a subject that itself reads "{preview}" is shown as written, never filled in -
    // the same as phase 0's str.format.
    private static string Fill(string template, IReadOnlyDictionary<string, string> fields)
        => Placeholder.Replace(template, m => fields.TryGetValue(m.Groups[1].Value, out var value)
            ? value
            : throw new InvalidOperationException($"The Mail Desk prompt names a field '{m.Groups[1].Value}' that is never filled."));

    private static string ReadPrompt(string fileName)
    {
        var name = "CcDirector.Gateway.MailDesk.Prompts." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded Mail Desk prompt '{name}' is missing from the Gateway binary.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal sealed record ReplyToken(string Text, double Logprob);

    internal sealed record Reply(string Content, int? InputTokens, int? OutputTokens, ReplyToken? FirstToken);

    /// <summary>
    /// Read a chat-completions body: <c>choices[0].message.content</c> (required, a string), <c>usage</c>'s two token
    /// counts (null when either is missing), and the first token of <c>choices[0].logprobs.content</c> (null when the
    /// reply carried none). Null when the body is not a chat completion at all.
    /// </summary>
    internal static Reply? ParseReply(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;

            int? input = null, output = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = TokenCount(usage, "prompt_tokens");
                output = TokenCount(usage, "completion_tokens");
            }

            ReplyToken? token = null;
            if (first.TryGetProperty("logprobs", out var logprobs) && logprobs.ValueKind == JsonValueKind.Object
                && logprobs.TryGetProperty("content", out var tokens) && tokens.ValueKind == JsonValueKind.Array
                && tokens.GetArrayLength() > 0)
            {
                var t = tokens[0];
                if (t.ValueKind == JsonValueKind.Object
                    && t.TryGetProperty("token", out var text) && text.ValueKind == JsonValueKind.String
                    && t.TryGetProperty("logprob", out var lp) && lp.ValueKind == JsonValueKind.Number)
                    token = new ReplyToken(text.GetString()!, lp.GetDouble());
            }

            return new Reply(content.GetString()!, input, output, token);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TokenCount(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0
            ? n
            : null;
}
