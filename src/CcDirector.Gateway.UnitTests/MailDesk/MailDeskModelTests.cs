using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.MailDesk;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE MAIL DESK MODEL CALL (Mail Desk phase 3 contract, section 5), over a fake HTTP handler: no real network, no
/// real port, no real clock. Every test here is one promise of the contract's Tests paragraph - the request body
/// exactly, each answer kind, the confidence and its absence, the cost and a reply without one, every way the backend
/// can fail, and nothing of the email in any log line.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskModelTests
{
    private const string Key = "dt_live_mail_desk_secret_key";
    private const string BaseUrl = "https://inference.example.com/api/v1";

    // Every field distinctive, so a log line holding any of them is caught by a plain search.
    private static readonly MailDeskEmail Email = new(
        "Quentin Zylberstein", "quentin.zylberstein@example.org", "Zebra invoice overdue 77341", "Preview-marker-ohm please look at the attached invoice");

    private static readonly IReadOnlyList<MailDeskRouteChoice> ThreeRoutes = new[]
    {
        new MailDeskRouteChoice("devthrottle/mail-desk", "customers and users of DevThrottle: support, billing, bugs"),
        new MailDeskRouteChoice("citizenship", "the citizenship application"),
        new MailDeskRouteChoice("notification", "automated notices from services"),
    };

    private static readonly AiCallTag Tag = new(AiFeature.MailDesk);

    private static MailDeskModel Model(FakeHandler handler, List<string>? log = null, TimeProvider? time = null)
        => new(BaseUrl, Key, IncludedModelId.WingmanFast, Tag, new HttpClient(handler),
            log is null ? _ => { } : log.Add, time: time);

    /// <summary>A chat-completions body with the given reply text, and optionally log probabilities and usage.</summary>
    private static string Completion(string content, string? firstToken = null, double? logprob = null,
        int? promptTokens = 120, int? completionTokens = 1, bool includeUsage = true)
    {
        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["message"] = new { role = "assistant", content },
            ["finish_reason"] = "stop",
        };
        if (firstToken is not null && logprob is not null)
            choice["logprobs"] = new
            {
                content = new[]
                {
                    new { token = firstToken, logprob = logprob.Value, top_logprobs = new[] { new { token = firstToken, logprob = logprob.Value } } },
                },
            };
        var body = new Dictionary<string, object?> { ["id"] = "chatcmpl-1", ["choices"] = new[] { choice } };
        if (includeUsage)
        {
            var usage = new Dictionary<string, object?>();
            if (promptTokens is not null) usage["prompt_tokens"] = promptTokens;
            if (completionTokens is not null) usage["completion_tokens"] = completionTokens;
            body["usage"] = usage;
        }
        return JsonSerializer.Serialize(body);
    }

    // ===== the request ===========================================================================

    [Fact]
    public async Task AskPersonOrSpamAsync_AnyEmail_SendsExactlyTheContractsRequest()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01));
        var model = Model(handler);

        // Act
        await model.AskPersonOrSpamAsync(Email);

        // Assert
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(BaseUrl + "/chat/completions", req.Uri);
        Assert.Equal("Bearer " + Key, req.Header("Authorization"));
        Assert.Equal("mail-desk", req.Header(AiCallTag.FeatureHeader));

        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal(
            new[] { "model", "messages", "temperature", "max_tokens", "logprobs", "top_logprobs", "stream" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("devthrottle/wingman-fast", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(2, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("logprobs").GetBoolean());
        Assert.Equal(5, root.GetProperty("top_logprobs").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(MailDeskModel.SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(
            "Question: is this a real person writing to us, or spam?\n\n"
            + "Labels:\n"
            + "A. person - a real person, or a real organisation we deal with, writing to us for a real reason (this includes genuine automated notices from a service we use)\n"
            + "B. spam - unsolicited marketing, cold sales outreach, phishing, scams, or bulk mail we never asked for\n\n"
            + "Email:\n"
            + "From: Quentin Zylberstein <quentin.zylberstein@example.org>\n"
            + "Subject: Zebra invoice overdue 77341\n"
            + "Start of the body: Preview-marker-ohm please look at the attached invoice\n\n"
            + "Answer with one letter: A or B.\n",
            messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AskRouteAsync_ThreeRoutes_ShowsEachRouteAsALetterThenUnsureLast()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("B", "B", -0.05));
        var model = Model(handler);

        // Act
        await model.AskRouteAsync(Email, ThreeRoutes);

        // Assert
        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.StartsWith("Question: which route should handle this email?", user, StringComparison.Ordinal);
        Assert.Contains(
            "Labels:\n"
            + "A. customers and users of DevThrottle: support, billing, bugs\n"
            + "B. the citizenship application\n"
            + "C. automated notices from services\n"
            + "D. none of the above, or you cannot tell\n\n",
            user, StringComparison.Ordinal);
        Assert.EndsWith("Answer with one letter: A, B, C or D.\n", user, StringComparison.Ordinal);
        Assert.Contains("From: Quentin Zylberstein <quentin.zylberstein@example.org>\n", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_ALongPreview_ShowsOnlyItsFirst200Characters()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01));
        var email = Email with { Preview = new string('p', 200) + "TAIL-NEVER-SHOWN" };

        // Act
        await Model(handler).AskPersonOrSpamAsync(email);

        // Assert
        var user = UserPrompt(handler);
        Assert.Contains("Start of the body: " + new string('p', 200) + "\n", user, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL-NEVER-SHOWN", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_ASubjectHoldingAPlaceholder_IsShownAsWrittenNotFilledIn()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01));
        var email = Email with { Subject = "about {preview} and {sender_address}" };

        // Act
        await Model(handler).AskPersonOrSpamAsync(email);

        // Assert
        Assert.Contains("Subject: about {preview} and {sender_address}\n", UserPrompt(handler), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskRouteAsync_TwentySixRoutes_IsRefusedAndNothingIsAsked()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01));
        var routes = Enumerable.Range(0, MailDeskModel.MaxRoutes + 1).Select(i => new MailDeskRouteChoice($"route-{i}", $"handles {i}")).ToList();

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Model(handler).AskRouteAsync(Email, routes));

        // Assert
        Assert.Contains("at most 25 routes", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AskRouteAsync_TwentyFiveRoutes_OffersLettersAToZWithUnsureOnZ()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("Z", "Z", -0.02));
        var routes = Enumerable.Range(0, MailDeskModel.MaxRoutes).Select(i => new MailDeskRouteChoice($"route-{i}", $"handles {i}")).ToList();

        // Act
        var result = await Model(handler).AskRouteAsync(Email, routes);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Label, result.Kind);
        Assert.Equal(MailDeskModel.UnsureLabel, result.Label);
        Assert.Contains("Y. handles 24\nZ. none of the above, or you cannot tell\n", UserPrompt(handler), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskRouteAsync_NoRoutes_IsRefusedAndNothingIsAsked()
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01));

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => Model(handler).AskRouteAsync(Email, Array.Empty<MailDeskRouteChoice>()));

        // Assert
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void EmbeddedPrompts_AreByteForBytePhaseZerosPrompts()
    {
        // Arrange
        var phaseZero = Path.Combine(RepositorySourceIndex.Root, "docs", "qa", "mail-desk", "phase-0", "c-model", "prompts");

        // Act
        var system = File.ReadAllText(Path.Combine(phaseZero, "system.txt"), Encoding.UTF8);
        var personOrSpam = File.ReadAllText(Path.Combine(phaseZero, "person_or_spam.txt"), Encoding.UTF8);
        var route = File.ReadAllText(Path.Combine(phaseZero, "route.txt"), Encoding.UTF8);

        // Assert
        Assert.Equal(system.Trim(), MailDeskModel.SystemPrompt);
        Assert.Equal(personOrSpam, MailDeskModel.PersonOrSpamTemplate);
        Assert.Equal(route, MailDeskModel.RouteTemplate);
    }

    // ===== each answer kind ======================================================================

    [Theory]
    [InlineData("A", "person")]
    [InlineData("B", "spam")]
    [InlineData(" B\n", "spam")]
    public async Task AskPersonOrSpamAsync_AListedLetter_IsThatLabel(string reply, string label)
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion(reply, reply, -0.1)));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Label, result.Kind);
        Assert.Equal(label, result.Label);
        Assert.Null(result.ErrorKind);
    }

    [Theory]
    [InlineData("A", "devthrottle/mail-desk")]
    [InlineData("B", "citizenship")]
    [InlineData("C", "notification")]
    [InlineData("D", "unsure")]
    public async Task AskRouteAsync_AListedLetter_IsThatRouteOrUnsure(string reply, string label)
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion(reply, reply, -0.1)));

        // Act
        var result = await model.AskRouteAsync(Email, ThreeRoutes);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Label, result.Kind);
        Assert.Equal(label, result.Label);
    }

    [Theory]
    [InlineData("C")]                        // a letter, but not one listed
    [InlineData("a")]                        // not exactly the letter
    [InlineData("A.")]
    [InlineData("AB")]
    [InlineData("")]
    [InlineData("I think the answer is A")]
    public async Task AskPersonOrSpamAsync_AnythingButOneListedLetter_IsOffLabelsAndStillCosted(string reply)
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion(reply, "A", -0.01, promptTokens: 100, completionTokens: 2)));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.OffLabels, result.Kind);
        Assert.Null(result.Label);
        Assert.Null(result.Confidence);
        Assert.Equal(IncludedModelPrice.WingmanFastCostUsd(100, 2), result.CostUsd);
        Assert.Null(result.ErrorKind);
    }

    // ===== the confidence ========================================================================

    [Fact]
    public async Task AskPersonOrSpamAsync_LogProbabilitiesForTheAnsweredLetter_ConfidenceIsTheirExponent()
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion("B", "B", Math.Log(0.97))));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.NotNull(result.Confidence);
        Assert.Equal(0.97, result.Confidence!.Value, 12);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_NoLogProbabilities_ConfidenceIsNullNeverOne()
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion("B")));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Label, result.Kind);
        Assert.Null(result.Confidence);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_FirstTokenIsNotTheAnsweredLetter_ConfidenceIsNull()
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion("B", "A", Math.Log(0.6))));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal("spam", result.Label);
        Assert.Null(result.Confidence);
    }

    // ===== the cost ==============================================================================

    [Fact]
    public async Task AskPersonOrSpamAsync_UsageInTheReply_CostIsTheTokensAtTheModelsPrice()
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion("A", "A", -0.01, promptTokens: 250_000, completionTokens: 500_000)));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert: 0.25 million at $0.36 plus 0.5 million at $0.40.
        Assert.Equal(0.09m + 0.20m, result.CostUsd);
        Assert.Equal(250_000, result.InputTokens);
        Assert.Equal(500_000, result.OutputTokens);
    }

    [Theory]
    [InlineData(false, 120, 1)]   // no usage object at all
    [InlineData(true, null, 1)]   // no prompt token count
    [InlineData(true, 120, null)] // no completion token count
    public async Task AskPersonOrSpamAsync_ReplyWithoutUsage_IsAFailureBecauseItsCostIsUnknown(bool includeUsage, int? prompt, int? completion)
    {
        // Arrange
        var model = Model(FakeHandler.Answering(Completion("A", "A", -0.01, prompt, completion, includeUsage)));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.NoUsage, result.ErrorKind);
        Assert.Null(result.Label);
        Assert.Null(result.CostUsd);
    }

    [Fact]
    public void WingmanFastCostUsd_OneMillionEach_IsTheNamedPricePair()
    {
        // Act
        var input = IncludedModelPrice.WingmanFastCostUsd(1_000_000, 0);
        var output = IncludedModelPrice.WingmanFastCostUsd(0, 1_000_000);

        // Assert
        Assert.Equal(0.36m, input);
        Assert.Equal(0.40m, output);
    }

    // ===== the backend failing ===================================================================

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http-500")]
    [InlineData(HttpStatusCode.PaymentRequired, "http-402")]
    [InlineData(HttpStatusCode.TooManyRequests, "http-429")]
    public async Task AskPersonOrSpamAsync_ANonSuccessStatus_IsAFailureNamingTheStatus(HttpStatusCode status, string errorKind)
    {
        // Arrange
        var handler = FakeHandler.Answering(Completion("A", "A", -0.01), status);

        // Act
        var result = await Model(handler).AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(errorKind, result.ErrorKind);
        Assert.Contains(((int)status).ToString(), result.Error, StringComparison.Ordinal);
        Assert.Null(result.Label);
        Assert.Null(result.CostUsd);
        Assert.Single(handler.Requests); // never a retry
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":7}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}")]
    [InlineData("")]
    public async Task AskPersonOrSpamAsync_ABodyThatIsNotAChatCompletion_IsAFailure(string body)
    {
        // Arrange
        var model = Model(FakeHandler.Answering(body));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.NotAChatCompletion, result.ErrorKind);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_NoAnswerWithinFifteenSeconds_IsATimeoutFailure()
    {
        // Arrange
        var clock = new VirtualClock(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        var handler = FakeHandler.Hanging();
        var model = Model(handler, time: clock);

        // Act
        var asking = model.AskPersonOrSpamAsync(Email);
        await handler.Entered;
        clock.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromMilliseconds(1));
        var stillWaiting = !asking.IsCompleted;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await asking;

        // Assert
        Assert.True(stillWaiting, "the question gave up before its 15-second deadline");
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.Timeout, result.ErrorKind);
        Assert.Contains("15 seconds", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_TheCallerCancels_IsACancelledFailureNotATimeout()
    {
        // Arrange
        var handler = FakeHandler.Hanging();
        using var cts = new CancellationTokenSource();
        var model = Model(handler, time: new VirtualClock(DateTimeOffset.UnixEpoch));

        // Act
        var asking = model.AskPersonOrSpamAsync(Email, cts.Token);
        await handler.Entered;
        cts.Cancel();
        var result = await asking;

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.Cancelled, result.ErrorKind);
    }

    [Fact]
    public async Task AskPersonOrSpamAsync_TheConnectionFails_IsAnUnreachableFailure()
    {
        // Arrange
        var model = Model(FakeHandler.Throwing(new HttpRequestException("connection refused")));

        // Act
        var result = await model.AskPersonOrSpamAsync(Email);

        // Assert
        Assert.Equal(MailDeskAnswerKind.Failed, result.Kind);
        Assert.Equal(MailDeskModelErrorKinds.Unreachable, result.ErrorKind);
    }

    // ===== nothing of the email, and never the credential, in any log line ========================

    [Fact]
    public async Task EveryOutcome_LogsNothingOfTheEmailAndNeverTheCredential()
    {
        // Arrange: an off-labels reply that echoes the subject, so a log of the reply text would be caught too.
        var log = new List<string>();
        var bodies = new (string Body, HttpStatusCode Status)[]
        {
            (Completion("A", "A", -0.01), HttpStatusCode.OK),
            (Completion("Zebra invoice overdue 77341"), HttpStatusCode.OK),
            (Completion("A", "A", -0.01, includeUsage: false), HttpStatusCode.OK),
            ("not json Zebra invoice overdue 77341", HttpStatusCode.OK),
            ("{\"error\":\"quentin.zylberstein@example.org\"}", HttpStatusCode.InternalServerError),
        };

        // Act
        foreach (var (body, status) in bodies)
        {
            await Model(FakeHandler.Answering(body, status), log).AskPersonOrSpamAsync(Email);
            await Model(FakeHandler.Answering(body, status), log).AskRouteAsync(Email, ThreeRoutes);
        }
        await Model(FakeHandler.Throwing(new HttpRequestException("quentin.zylberstein@example.org")), log).AskPersonOrSpamAsync(Email);

        // Assert: the sink was written to, then holds none of the email and not the key.
        Assert.True(log.Count >= 22, $"expected a line per question start and end, got {log.Count}");
        var all = string.Join("\n", log);
        foreach (var secret in new[] { "Quentin", "Zylberstein", "zylberstein@example.org", "example.org", "Zebra", "77341", "Preview-marker-ohm", Key })
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
    }

    private static string UserPrompt(FakeHandler handler)
    {
        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        return doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    }
}

/// <summary>A recorded request: what the fake handler saw, read before the request is disposed.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A fake model backend: answers a fixed body, hangs until cancelled, or throws. Records every request.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Completes when the first request reaches the handler.</summary>
    public Task Entered => _entered.Task;

    public static FakeHandler Answering(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    public static FakeHandler Hanging()
        => new(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    public static FakeHandler Throwing(Exception ex) => new(_ => Task.FromException<HttpResponseMessage>(ex));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body, headers));
        _entered.TrySetResult();
        return await _respond(ct);
    }
}
