using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE LICENSE WARNING TRIANGLE ON THE DIRECTOR DESKTOP (issue 4728, which replaced the full-width banner). What these
/// hold down: the Director's client reads <c>GET /gateway/licenses/alarms</c>; the cache keeps its last answer when a read
/// fails and says so only when the answer changed; the triangle draws the Gateway's count, words and tone verbatim and
/// decides nothing - it is hidden only when no alarm is open, and a tone this build does not know is shown, uncoloured,
/// never guessed; and the click asks the Gateway for the Licenses page rather than composing it.
///
/// The client is the REAL <see cref="GatewayClient"/> behind a stub message handler. Plain [Fact]: nothing here touches
/// an Avalonia object.
/// </summary>
public sealed class LicenseAlarmTriangleTests
{
    private const string OneWarning =
        "{\"asOfUtc\":\"2026-11-06T12:00:00Z\",\"count\":1,\"summary\":\"License alarm: a machine cannot switch accounts.\","
        + "\"linkText\":\"Open Licenses\",\"tone\":\"warning\",\"alarms\":[]}";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(_answer(request));
        }
    }

    private sealed class FakeGateway : IGatewayLicenseAlarms
    {
        public LicenseAlarmsResponse? Answer { get; set; }
        public Exception? ReadFails { get; set; }

        public Task<LicenseAlarmsResponse?> GetLicenseAlarmsAsync(CancellationToken ct = default)
            => ReadFails is { } ex ? Task.FromException<LicenseAlarmsResponse?>(ex) : Task.FromResult(Answer);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GatewayClient ClientAnswering(StubHandler handler) =>
        new(new GatewayConfig { Url = "http://gateway.example", Token = "test-token" },
            directorId: "director-under-test", version: "1.0.0", handler);

    private static LicenseAlarmsResponse Open(string? tone, string summary = "License alarm: a machine cannot switch accounts.")
        => new() { Count = 1, Summary = summary, LinkText = "Open Licenses", Tone = tone };

    // ---- the client ------------------------------------------------------------------------------------

    [Fact]
    public async Task GetLicenseAlarmsAsync_ReadsTheAlarmsRoute_AndKeepsTheGatewaysWordsAndTone()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, OneWarning));

        var answer = await ClientAnswering(handler).GetLicenseAlarmsAsync();

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://gateway.example/gateway/licenses/alarms", handler.RequestUri);
        Assert.Equal((1, "License alarm: a machine cannot switch accounts.", "Open Licenses", "warning"),
            (answer!.Count, answer.Summary, answer.LinkText, answer.Tone));
    }

    [Fact]
    public async Task GetLicenseAlarmsAsync_ARefusedRead_Throws()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, "{\"code\":\"no_account\",\"error\":\"no account is bound to this request\"}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ClientAnswering(handler).GetLicenseAlarmsAsync());
    }

    // ---- the cache -------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_ANewAnswer_IsKeptAndRaisesChanged_TheSameAnswerAgainDoesNot()
    {
        var gateway = new FakeGateway { Answer = Open("warning") };
        var cache = new LicenseAlarmsCache(() => gateway);
        var changes = 0;
        cache.Changed += () => changes++;

        await cache.RefreshAsync();
        await cache.RefreshAsync();
        gateway.Answer = Open("critical");
        await cache.RefreshAsync();

        Assert.Equal(2, changes);
        Assert.Equal("critical", cache.Current!.Tone);
    }

    [Fact]
    public async Task RefreshAsync_AFailedRead_KeepsTheLastAnswer_AndSaysWhy()
    {
        var gateway = new FakeGateway { Answer = Open("warning") };
        var cache = new LicenseAlarmsCache(() => gateway);
        await cache.RefreshAsync();

        gateway.ReadFails = new InvalidOperationException("the Gateway did not answer");
        await cache.RefreshAsync();

        Assert.Equal("warning", cache.Current!.Tone);
        Assert.Equal("the Gateway did not answer", cache.Error);
    }

    [Fact]
    public async Task RefreshAsync_NoGatewayConfigured_ReadsNothing()
    {
        var cache = new LicenseAlarmsCache(() => null);

        await cache.RefreshAsync();

        Assert.Null(cache.Current);
        Assert.Null(cache.Error);
    }

    // ---- the triangle ----------------------------------------------------------------------------------

    private static LicenseAlarmsResponse Three(string? tone) => new()
    {
        Count = 3, Summary = "3 license problems", LinkText = "Open Licenses", Tone = tone,
        Alarms =
        {
            new LicenseAlarmDto { Text = "Renewing the sign-in for dev@example.com on LAPTOP failed.", Tone = "warning" },
            new LicenseAlarmDto { Text = "The approver for a@example.com needs a fresh sign-in.", Tone = "warning" },
            new LicenseAlarmDto { Text = "The approver for b@example.com needs a fresh sign-in.", Tone = "warning" },
        },
    };

    private static readonly string Gap = Environment.NewLine + Environment.NewLine;

    [Fact]
    public void From_NoAnswerOrNoAlarmOpen_IsHidden()
    {
        Assert.Equal(LicenseAlarmTriangleView.Hidden, LicenseAlarmTriangleView.From(null));
        Assert.Equal(LicenseAlarmTriangleView.Hidden, LicenseAlarmTriangleView.From(new LicenseAlarmsResponse { Count = 0 }));
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("critical")]
    public void From_OpenAlarms_ShowsTheGatewaysCount_ItsLinesVerbatim_InItsTone(string tone)
    {
        var view = LicenseAlarmTriangleView.From(Three(tone));

        Assert.True(view.IsVisible);
        Assert.Equal("3", view.Count);
        Assert.Equal(tone, view.Tone);
        Assert.Equal("3 license problems" + Gap
                     + "Renewing the sign-in for dev@example.com on LAPTOP failed." + Gap
                     + "The approver for a@example.com needs a fresh sign-in." + Gap
                     + "The approver for b@example.com needs a fresh sign-in." + Gap
                     + "Click: Open Licenses", view.ToolTip);
    }

    [Fact]
    public void From_AToneThisBuildDoesNotKnow_IsShownUncoloured_AndSaysSo()
    {
        var view = LicenseAlarmTriangleView.From(Open("purple"));

        Assert.True(view.IsVisible);
        Assert.Null(view.Tone);
        Assert.Equal("1", view.Count);
        Assert.Equal("License alarm: a machine cannot switch accounts." + Gap
                     + "(This Director cannot draw the tone \"purple\"; update it.)" + Gap
                     + "Click: Open Licenses", view.ToolTip);
    }

    [Fact]
    public void From_OpenAlarmsWithNoSummary_Throws()
        => Assert.Throws<InvalidOperationException>(() => LicenseAlarmTriangleView.From(new LicenseAlarmsResponse { Count = 1 }));

    [Fact]
    public void BuildCockpitLicensesRequestUrl_AsksTheGatewayForTheLicensesPage()
    {
        // The Gateway resolves {base}/licenses and hands it back whole; the desktop names only the Gateway's own route.
        Assert.Equal("http://127.0.0.1/cockpit?page=licenses", MainWindow.BuildCockpitLicensesRequestUrl("http://127.0.0.1"));
    }
}
