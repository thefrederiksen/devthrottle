using System.Net;
using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>
/// End-to-end tests for the Gateway's prompt-log front door (issue #1551), over a real HTTP pipeline:
/// a Director POSTs what it captured, and anyone asking for history GETs it back. This is the whole
/// point of the log living on the Gateway - the history is already there to ask for.
/// </summary>
public sealed class PromptEndpointsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gw-prompt-ep-" + Guid.NewGuid().ToString("N"));

        // A real HTTP server on an ephemeral port, over a prompt log pinned to a throwaway directory -
        // the same shape the other Gateway endpoint tests use.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        // Self-host harness: the REAL self-host boundary, built over the SingleTenantContext, which always
        // resolves Local. A null boundary is a refusal in every mode now that the mode is a value the host
        // hands over rather than the process environment, so the absence is no longer a self-host form.
        PromptEndpoints.Map(_app, new GatewayPromptLog(_dir),
            tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false));
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PromptRecord Rec(DateTime ts, string text, string role = "user") => new()
    {
        TsUtc = ts,
        Machine = "SOREN_NORTH",
        SessionId = "session-1",
        ContextId = "ctx-1",
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "ClaudeCode",
        Role = role,
        Modality = role == "user" ? "voice" : null,
        Surface = role == "user" ? "phone" : null,
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        Text = text,
    };

    private static string Day(DateTime ts) => ts.ToString("yyyy-MM-dd");

    [Fact]
    public async Task A_director_pushes_and_the_gateway_serves_it_back()
    {
        var ts = DateTime.UtcNow;

        var post = await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest
        {
            Records = new[] { Rec(ts, "make the button blue"), Rec(ts.AddSeconds(3), "Done.", "assistant") },
        });

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var ack = await post.Content.ReadFromJsonAsync<PromptIngestResponse>();
        Assert.Equal(2, ack!.Written);

        // Now ask the Gateway for the history - it already has it.
        var get = await _client.GetAsync($"/prompts?from={Day(ts)}&to={Day(ts)}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<PromptsResponse>();
        Assert.Equal(2, body!.Count);
        Assert.Equal("make the button blue", body.Records[0].Text);
        Assert.Equal("voice", body.Records[0].Modality);
        Assert.Equal("phone", body.Records[0].Surface);
        Assert.Equal("SOREN_NORTH", body.Records[0].Machine);
        Assert.Equal("assistant", body.Records[1].Role);
    }

    [Fact]
    public async Task A_bare_get_defaults_to_today()
    {
        await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest { Records = new[] { Rec(DateTime.UtcNow, "today's prompt") } });

        var body = await _client.GetFromJsonAsync<PromptsResponse>("/prompts");

        Assert.Equal("today's prompt", Assert.Single(body!.Records).Text);
    }

    [Fact]
    public async Task An_empty_push_is_rejected_rather_than_silently_accepted()
    {
        var resp = await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest { Records = Array.Empty<PromptRecord>() });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_backwards_range_is_rejected()
    {
        var today = DateTime.UtcNow;
        var resp = await _client.GetAsync($"/prompts?from={Day(today)}&to={Day(today.AddDays(-3))}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Pushes_from_two_machines_both_land_and_stay_distinguishable()
    {
        var ts = DateTime.UtcNow;
        await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest { Records = new[] { Rec(ts, "from north") with { Machine = "SOREN_NORTH" } } });
        await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest { Records = new[] { Rec(ts.AddMinutes(1), "from laptop") with { Machine = "SOREN_LAPTOP" } } });

        var body = await _client.GetFromJsonAsync<PromptsResponse>($"/prompts?from={Day(ts)}&to={Day(ts)}");

        Assert.Equal(2, body!.Count);
        Assert.Equal(new[] { "SOREN_NORTH", "SOREN_LAPTOP" }, body.Records.Select(r => r.Machine));
    }

    // ---- Export and delete: the account data rights (CR-3b) ----------------------------------------

    [Fact]
    public async Task Export_downloads_the_whole_history_regardless_of_age()
    {
        var now = DateTime.UtcNow;
        await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest
        {
            Records = new[] { Rec(now.AddDays(-200), "long ago"), Rec(now, "just now") },
        });

        var resp = await _client.GetAsync("/prompts/export");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);
        Assert.Contains("prompt-history-", resp.Content.Headers.ContentDisposition!.FileName);
        var body = await resp.Content.ReadFromJsonAsync<ExportResponse>();
        Assert.Equal(2, body!.Count);
        Assert.Equal(new[] { "long ago", "just now" }, body.Records.Select(r => r.Text));
        Assert.True(body.ExportedAtUtc > now.AddMinutes(-1));
    }

    [Fact]
    public async Task Delete_erases_the_history_and_a_read_afterwards_finds_nothing()
    {
        var now = DateTime.UtcNow;
        await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest
        {
            Records = new[] { Rec(now.AddDays(-3), "old"), Rec(now, "new") },
        });

        var del = await _client.DeleteAsync("/prompts");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var ack = await del.Content.ReadFromJsonAsync<DeleteResponse>();
        Assert.Equal(2, ack!.DeletedFiles);

        // The proof the exit row asks for: the rows are GONE, from both the ranged read and the export.
        var after = await _client.GetFromJsonAsync<PromptsResponse>($"/prompts?from={Day(now.AddDays(-3))}&to={Day(now)}");
        Assert.Equal(0, after!.Count);
        var export = await (await _client.GetAsync("/prompts/export")).Content.ReadFromJsonAsync<ExportResponse>();
        Assert.Equal(0, export!.Count);
    }

    [Fact]
    public async Task Deleting_an_empty_history_succeeds_with_nothing_to_do()
    {
        var del = await _client.DeleteAsync("/prompts");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await del.Content.ReadFromJsonAsync<DeleteResponse>())!.DeletedFiles);
    }

    /// <summary>The GET /prompts body shape.</summary>
    private sealed record PromptsResponse(int Count, IReadOnlyList<PromptRecord> Records);

    /// <summary>The GET /prompts/export body shape.</summary>
    private sealed record ExportResponse(DateTime ExportedAtUtc, int Count, IReadOnlyList<PromptRecord> Records);

    /// <summary>The DELETE /prompts body shape.</summary>
    private sealed record DeleteResponse(int DeletedFiles);
}
