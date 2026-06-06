using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end smoke tests for the Director's screenshots-gallery endpoints
/// (GET /screenshots, GET /screenshots/file, DELETE /screenshots/file). Runs a real
/// ControlApiHost on an ephemeral port with CC_DIRECTOR_ROOT redirected to a temp dir AND
/// the screenshots folder pinned (via config.json) into that temp dir, so nothing touches the
/// user's real Pictures\Screenshots folder. In the "DirectorRoot" collection (serializes
/// root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class ScreenshotEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private string _shotsDir = null!;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    // A tiny valid 1x1 PNG (decodes cleanly), used as the seeded screenshot bytes.
    private static readonly byte[] OnePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public ScreenshotEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-shots-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Pin the screenshots folder into the temp root via config.json so CcStorage.Screenshots()
        // resolves to it instead of the real Pictures\Screenshots.
        _shotsDir = Path.Combine(_root, "shots");
        Directory.CreateDirectory(_shotsDir);
        var configDir = CcStorage.Config();
        Directory.CreateDirectory(configDir);
        var json = JsonSerializer.Serialize(new
        {
            screenshots = new { source_directory = _shotsDir },
        });
        await File.WriteAllTextAsync(CcStorage.ConfigJson(), json);

        // Two seeded screenshots with distinct write times so newest-first ordering is testable.
        var older = Path.Combine(_shotsDir, "shot-older.png");
        var newer = Path.Combine(_shotsDir, "shot-newer.png");
        await File.WriteAllBytesAsync(older, OnePngBytes);
        await File.WriteAllBytesAsync(newer, OnePngBytes);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        // A non-image file that must be ignored by the listing.
        await File.WriteAllTextAsync(Path.Combine(_shotsDir, "notes.txt"), "ignore me");

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed record ShotItem(string FileName, string Path, string TimeLabel, DateTimeOffset LastWriteUtc, long SizeBytes);
    private sealed record ShotList(string Directory, int Total, List<ShotItem> Items);

    [Fact]
    public async Task List_returns_only_images_newest_first_with_labels()
    {
        var list = await _client.GetFromJsonAsync<ShotList>("screenshots");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);                       // notes.txt excluded
        Assert.Equal(2, list.Total);
        Assert.Equal("shot-newer.png", list.Items[0].FileName);   // newest first
        Assert.Equal("shot-older.png", list.Items[1].FileName);
        Assert.All(list.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.TimeLabel)));
        Assert.All(list.Items, i => Assert.True(i.SizeBytes > 0));
        // Each item carries its absolute on-disk path (injected into the composer on tap).
        Assert.Equal(Path.Combine(_shotsDir, "shot-newer.png"), list.Items[0].Path);
    }

    [Fact]
    public async Task List_count_caps_items_but_total_reports_the_folder()
    {
        var list = await _client.GetFromJsonAsync<ShotList>("screenshots?count=1");
        Assert.NotNull(list);
        Assert.Single(list!.Items);                               // capped to the newest one
        Assert.Equal("shot-newer.png", list.Items[0].FileName);
        Assert.Equal(2, list.Total);                              // folder count, not the cap
    }

    [Theory]
    [InlineData("screenshots?count=0")]    // <=0 falls back to the default cap
    [InlineData("screenshots?count=-5")]
    [InlineData("screenshots?count=999")]  // cap above the folder size
    public async Task List_count_edge_values_are_safe(string url)
    {
        var list = await _client.GetFromJsonAsync<ShotList>(url);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(2, list.Total);
    }

    [Fact]
    public async Task List_omitted_count_caps_at_default_and_ignores_older_files()
    {
        // Seed enough images to exceed the default cap; all OLDER than the two fixtures so
        // the newest-first cap keeps the fixtures and drops the tail.
        var extra = ControlEndpoints.DefaultScreenshotCount + 3;
        for (var i = 0; i < extra; i++)
        {
            var p = Path.Combine(_shotsDir, $"bulk-{i:D3}.png");
            await File.WriteAllBytesAsync(p, OnePngBytes);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-60 - i));
        }

        var list = await _client.GetFromJsonAsync<ShotList>("screenshots");
        Assert.NotNull(list);
        Assert.Equal(ControlEndpoints.DefaultScreenshotCount, list!.Items.Count); // capped
        Assert.Equal(extra + 2, list.Total);                                      // full folder count
        Assert.Equal("shot-newer.png", list.Items[0].FileName);                   // newest still first
    }

    [Fact]
    public async Task File_serves_image_bytes_with_cors_and_cache_headers()
    {
        var resp = await _client.GetAsync("screenshots/file?name=shot-newer.png");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        Assert.True(resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var acao));
        Assert.Equal("*", acao!.Single());
        // Screenshot files are immutable once written - the browser may cache thumbnails so
        // session switches don't re-download the same bytes.
        Assert.True(resp.Headers.TryGetValues("Cache-Control", out var cache));
        Assert.Equal("public, max-age=3600", cache!.Single());
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(OnePngBytes.Length, bytes.Length);
    }

    [Fact]
    public async Task File_returns_404_for_unknown_name()
    {
        var resp = await _client.GetAsync("screenshots/file?name=nope.png");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Fsecret.png")]   // url-encoded ../../
    [InlineData("sub%2Fshot.png")]          // url-encoded subdir/
    [InlineData("notes.txt")]               // non-image extension
    public async Task File_rejects_traversal_and_non_images(string name)
    {
        var resp = await _client.GetAsync($"screenshots/file?name={name}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_file_off_disk()
    {
        var resp = await _client.DeleteAsync("screenshots/file?name=shot-older.png");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(File.Exists(Path.Combine(_shotsDir, "shot-older.png")));

        var list = await _client.GetFromJsonAsync<ShotList>("screenshots");
        Assert.Single(list!.Items);
        Assert.Equal("shot-newer.png", list.Items[0].FileName);
    }

    [Fact]
    public async Task Delete_returns_404_for_unknown_name()
    {
        var resp = await _client.DeleteAsync("screenshots/file?name=ghost.png");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
