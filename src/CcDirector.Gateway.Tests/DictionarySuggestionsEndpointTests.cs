using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP end-to-end for the dictionary-suggestions API (devthrottle #2075, redesigned in #2115). Boots the
/// real <see cref="RecordingEndpoints"/> - with the scan-based suggestion service (stub screening brain),
/// verdict, scan, and dismissal stores over an isolated SQLite database and an isolated CC_DIRECTOR_ROOT -
/// on an ephemeral loopback port, then drives the whole customer flow over HTTP: empty before any scan,
/// scan-now produces the screened suggestions, apply a term (which must land in the glossary FILE as both a
/// vocabulary term and its wrong spellings) and see it leave the stored list at once, dismiss another, list
/// the dismissed, restore it, and rescan to see it return WITHOUT a second model call (the verdict is
/// persisted). Self-host (no tenant boundary) resolves the single Local tenant throughout.
/// </summary>
public sealed class DictionarySuggestionsEndpointTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-dict-suggest-http-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevRoot;
    private readonly GatewayDbTestHarness _dbh = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public DictionarySuggestionsEndpointTests()
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

    [Fact]
    public async Task FullApiFlow_ScanApplyDismissRestoreRescan()
    {
        var db = _dbh.Open();
        var transcripts = new TranscriptStore(db);
        var dismissals = new DictionarySuggestionDismissalStore(db);
        var brain = new ApprovingBrain();
        var suggestions = new DictionarySuggestionService(
            transcripts, dismissals,
            new DictionarySuggestionVerdictStore(db),
            new DictionarySuggestionScanStore(db),
            TenantGlossary.Load,
            (_, _) => Task.FromResult<(IAgentBrain, string)>((brain, "stub-model")),
            now: () => Base);

        // Two terms the model keeps getting wrong, neither in the (empty) glossary.
        SeedTerm(transcripts, Base, new[] { ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12) });
        SeedTerm(transcripts, Base.AddHours(1),
            new[] { ("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12) });

        // Issue #2161: bind an operating-system-assigned port, then learn the number it gave us. The
        // address cannot be built up front any more - "0" is not something a client can dial.
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
            suggestions: suggestions, dismissals: dismissals);
        await app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(app)}";

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 1. Before any scan: empty, never scanned, no model call was spent behind the read.
            var before = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal(0, before!.Count);
            Assert.Null(before.ScannedAtUtc);
            Assert.True(before.ScreeningOk);
            Assert.Equal(0, brain.Calls);

            // 2. Scan now: both terms are mined, screened in ONE model call, and stored.
            var scanResp = await http.PostAsync("/ingest/dictionary/suggestions/scan", null);
            scanResp.EnsureSuccessStatusCode();
            var scanned = await scanResp.Content.ReadFromJsonAsync<SuggestionsBody>(Json);
            Assert.Equal(2, scanned!.Count);
            Assert.Equal(Base, scanned.ScannedAtUtc);
            Assert.True(scanned.ScreeningOk);
            Assert.Equal(1, brain.Calls);
            var mindzie = scanned.Suggestions.Single(s => s.Term == "mindzie");
            Assert.Equal(47, mindzie.WrongCount);
            Assert.Equal(91, mindzie.TotalCount);
            Assert.Contains(mindzie.Variants, v => v.Heard == "Mindsee");

            // The count route (the nav badge) reads the same stored result.
            var count = await http.GetFromJsonAsync<CountBody>("/ingest/dictionary/suggestions/count", Json);
            Assert.Equal(2, count!.Count);

            // 3. Apply "mindzie" - it must land in the glossary as a vocabulary term AND its wrong spellings
            //    must land as Common mistranscriptions, in one call; the stored list reflects it at once.
            var applyResp = await http.PostAsJsonAsync("/ingest/dictionary/suggestions/apply",
                new { terms = new[] { "mindzie" } });
            applyResp.EnsureSuccessStatusCode();
            var apply = await applyResp.Content.ReadFromJsonAsync<ApplyBody>(Json);
            Assert.Equal(new[] { "mindzie" }, apply!.Applied.ToArray());
            Assert.Contains("mindzie", apply.Dictionary.Vocabulary);
            Assert.True(apply.Dictionary.CommonMistranscriptions.ContainsKey("mindzie"));
            Assert.Contains("Mindsee", apply.Dictionary.CommonMistranscriptions["mindzie"]);
            Assert.Equal(1, apply.Count);

            // 4. The glossary the editor route reads now shows the applied term too (same file).
            var glossary = await http.GetFromJsonAsync<DictionaryBody>("/ingest/dictionary", Json);
            Assert.Contains("mindzie", glossary!.Vocabulary);

            // 5. Only Frederiksen remains in the stored suggestions.
            var afterApply = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal("Frederiksen", afterApply!.Suggestions.Single().Term);

            // 6. Dismiss Frederiksen - it disappears at once and the count goes to zero.
            var dismissResp = await http.PostAsJsonAsync("/ingest/dictionary/suggestions/dismiss",
                new { term = "Frederiksen" });
            dismissResp.EnsureSuccessStatusCode();
            var afterDismiss = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal(0, afterDismiss!.Count);

            // 7. The dismissed list shows it with its evidence snapshot.
            var dismissed = await http.GetFromJsonAsync<DismissedBody>("/ingest/dictionary/dismissed", Json);
            var d = Assert.Single(dismissed!.Dismissed);
            Assert.Equal("Frederiksen", d.Term);
            Assert.Contains(d.Variants, v => v.Heard == "Fredriksson");

            // 8. Restore it - the stored list stays as-is until a scan runs...
            var restoreResp = await http.PostAsJsonAsync("/ingest/dictionary/dismissed/restore",
                new { term = "Frederiksen" });
            restoreResp.EnsureSuccessStatusCode();
            var afterRestore = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal(0, afterRestore!.Count);

            // ...and the next scan brings it back WITHOUT a second model call: its verdict is persisted,
            // so the term is judged at most once, ever (through the HTTP surface too).
            var rescanResp = await http.PostAsync("/ingest/dictionary/suggestions/scan", null);
            rescanResp.EnsureSuccessStatusCode();
            var rescanned = await rescanResp.Content.ReadFromJsonAsync<SuggestionsBody>(Json);
            Assert.Equal("Frederiksen", rescanned!.Suggestions.Single().Term);
            Assert.Equal(1, brain.Calls);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // Response shapes (camelCase from the endpoint; case-insensitive binding).
    private sealed record VariantBody(string Heard, int Count);
    private sealed record SuggestionBody(string Term, List<VariantBody> Variants, int WrongCount, int TotalCount);
    private sealed record SuggestionsBody(
        List<SuggestionBody> Suggestions, int Count, DateTime? ScannedAtUtc, bool ScreeningOk, string ScreeningError);
    private sealed record CountBody(int Count);
    private sealed record DictionaryBody(
        List<string> Vocabulary, Dictionary<string, List<string>> CommonMistranscriptions);
    private sealed record ApplyBody(DictionaryBody Dictionary, List<string> Applied, int Count);
    private sealed record DismissedItemBody(string Term, List<VariantBody> Variants, int WrongCount, int TotalCount);
    private sealed record DismissedBody(List<DismissedItemBody> Dismissed);
}
