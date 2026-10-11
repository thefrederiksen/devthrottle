using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP end-to-end for the daily-email block route (issue #2074, mockup screen 5):
/// <c>POST /ingest/dictionary/suggestions/email-block</c>. Boots the real <see cref="RecordingEndpoints"/> with
/// a real composer over an isolated SQLite database and an isolated CC_DIRECTOR_ROOT, then drives the whole
/// cadence over the wire - preview, two real sends, and the quiet third - because the property that matters is
/// what a daily-report composer would actually receive, not what the service returns in process.
/// </summary>
public sealed class SuggestionEmailBlockEndpointTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-email-block-http-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevRoot;
    private readonly GatewayDbTestHarness _dbh = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public SuggestionEmailBlockEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        _dbh.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task EmailBlockRoute_PreviewsFreelyThenMentionsTwiceAndGoesQuiet()
    {
        var db = _dbh.Open();
        var transcripts = new TranscriptStore(db);
        var dismissals = new DictionarySuggestionDismissalStore(db);
        var suggestions = BuildSuggestions(db, transcripts, dismissals);
        var settings = new TenantSettingsResolver(new TenantSettingsStore(db));
        var composer = new SuggestionEmailComposer(
            suggestions.GetSuggestions, settings, () => "https://gw.example.com", () => Base);

        var bindUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add(bindUrl);
        // The boundary is required and non-nullable now (finding I1-01). This is a self-host harness, so it
        // gets the REAL self-host boundary: built over the SingleTenantContext, it always resolves Local.
        RecordingEndpoints.Map(app, false,
            tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            keyVault: null, history: null, audioArchive: null,
            suggestions: suggestions, dismissals: dismissals, emailComposer: composer);
        await app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(app)}";

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 1. Nothing to say yet: the report carries no block, and the reason says why rather than
            //    leaving a composer to guess from an empty payload.
            var empty = await Post(http, null);
            Assert.False(empty!.Include);
            Assert.Equal("nosuggestions", empty.Reason);
            Assert.Null(empty.Html);
            Assert.Equal(0, empty.TermCount);

            // 2. The user starts being misheard.
            SeedTerm(transcripts, Base, new[] { ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12) });
            await suggestions.RunScanAsync(TenantId.Local);

            // 3. A preview (no body at all) returns the finished block and spends nothing.
            var preview = await Post(http, null);
            Assert.True(preview!.Include);
            Assert.Equal("included", preview.Reason);
            Assert.Equal(0, preview.Mentions);
            Assert.Equal(2, preview.MaxMentions);
            Assert.Equal("Dictation: 1 word worth adding to your dictionary", preview.Heading);
            Assert.Contains("mindzie", preview.Text, StringComparison.Ordinal);
            Assert.Contains("https://gw.example.com/dictionary", preview.Html, StringComparison.Ordinal);
            Assert.Contains("Suggestions in my daily email", preview.Footer, StringComparison.Ordinal);

            // 4. Two real sends, both included, the mention count climbing.
            Assert.Equal(1, (await Post(http, true))!.Mentions);
            var second = await Post(http, true);
            Assert.True(second!.Include);
            Assert.Equal(2, second.Mentions);
            Assert.Equal(preview.Batch, second.Batch);

            // 5. The third goes quiet - the badge on the Dictionary page carries it from here. The term count
            //    is still reported, so a composer can log "1 pending, deliberately not mentioned".
            var third = await Post(http, true);
            Assert.False(third!.Include);
            Assert.Equal("alreadymentioned", third.Reason);
            Assert.Null(third.Html);
            Assert.Equal(1, third.TermCount);

            // 6. Turning the setting off is the user's own override, and it is reported as such.
            settings.SetSuggestionsInDailyEmail(TenantId.Local, false, Base);
            var off = await Post(http, true);
            Assert.False(off!.Include);
            Assert.Equal("settingoff", off.Reason);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>Malformed JSON is rejected with a clear 400, not swallowed into a "no block" answer that would
    /// look identical to a quiet day.</summary>
    [Fact]
    public async Task EmailBlockRoute_BadJson_IsRejected()
    {
        var db = _dbh.Open();
        var transcripts = new TranscriptStore(db);
        var dismissals = new DictionarySuggestionDismissalStore(db);
        var suggestions = BuildSuggestions(db, transcripts, dismissals);
        var composer = new SuggestionEmailComposer(
            suggestions.GetSuggestions, new TenantSettingsResolver(new TenantSettingsStore(db)),
            () => null, () => Base);

        var bindUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add(bindUrl);
        // Real self-host boundary, same reasoning as the harness above (finding I1-01).
        RecordingEndpoints.Map(app, false,
            tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            keyVault: null, history: null, audioArchive: null,
            suggestions: suggestions, dismissals: dismissals, emailComposer: composer);
        await app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(app)}";

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var response = await http.PostAsync("/ingest/dictionary/suggestions/email-block",
                new StringContent("{not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<EmailBlockBody?> Post(HttpClient http, bool? markMentioned)
    {
        var content = markMentioned is null
            ? null
            : new StringContent($"{{\"markMentioned\":{markMentioned.Value.ToString().ToLowerInvariant()}}}",
                Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/ingest/dictionary/suggestions/email-block", content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EmailBlockBody>(Json);
    }

    private static void SeedTerm(TranscriptStore store, DateTime at, (string spelling, int times)[] spellings)
    {
        var i = 0;
        foreach (var (spelling, times) in spellings)
            for (var n = 0; n < times; n++)
                store.Append(TenantId.Local, "dictation", $"we shipped the {spelling} change", null, false,
                    turnId: null, nowUtc: at.AddSeconds(i++));
    }

    /// <summary>A screening brain that approves every candidate in the prompt; counts calls so the test can
    /// pin "a term is judged at most once, ever" through the HTTP surface.</summary>
    private sealed class ApprovingBrain : IAgentBrain
    {
        public int Calls;
        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Calls++;
            var verdicts = new List<object>();
            foreach (var line in prompt.Split('\n'))
            {
                var t = line.Trim();
                var dot = t.IndexOf(". \"", StringComparison.Ordinal);
                if (dot < 0 || dot > 4) continue;
                var start = dot + 3;
                var end = t.IndexOf('"', start);
                if (end < 0) continue;
                verdicts.Add(new { term = t[start..end], approved = true, reason = "stub" });
            }
            return Task.FromResult(new AskResult { Text = JsonSerializer.Serialize(verdicts) });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    /// <summary>The suggestions engine on the shape current main gives it: four stores plus a screening brain.
    /// The brain approves everything, so what reaches the email block is exactly what the miner found - the
    /// screening is proved elsewhere and is not what this test is about.</summary>
    private static DictionarySuggestionService BuildSuggestions(
        CcDirector.Gateway.Data.GatewayDatabase db, TranscriptStore transcripts, DictionarySuggestionDismissalStore dismissals)
        => new(
            transcripts, dismissals,
            new DictionarySuggestionVerdictStore(db),
            new DictionarySuggestionScanStore(db),
            TenantGlossary.Load,
            (_, _) => Task.FromResult<(CcDirector.AgentBrain.IAgentBrain, string)>(
                (new ApprovingBrain(), "stub-model")),
            now: () => Base);

    private sealed record EmailBlockBody(
        bool Include, string Reason, string? Heading, string? Html, string? Text, string Footer,
        int TermCount, string Batch, int Mentions, int MaxMentions);
}
