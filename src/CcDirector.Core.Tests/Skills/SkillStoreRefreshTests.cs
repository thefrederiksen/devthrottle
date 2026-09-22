using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// The skill store refresh downloads a skill only when the store does not already hold the
/// version the register serves. These tests stand up a hermetic Gateway (an in-memory
/// <see cref="HttpMessageHandler"/>) that counts every request, so "no download happened" is a
/// number, not an absence. The first test fails against the refresh as it was before
/// 22 September 2026, which fetched and rewrote every enabled skill on every cycle.
/// </summary>
public sealed class SkillStoreRefreshTests : IDisposable
{
    private readonly string _store;

    public SkillStoreRefreshTests()
    {
        _store = TestTempRoot.For("ccd-skill-store-");
        Directory.CreateDirectory(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_store, recursive: true); } catch { }
    }

    [Fact]
    public async Task ASecondRefreshWithAnUnchangedRegister_DownloadsNothing_AndLeavesTheFilesAlone()
    {
        var gateway = new FakeGateway();
        gateway.Serve("alpha", version: 3, hash: "h3", body: "alpha body");
        gateway.Serve("beta", version: 1, hash: "h1", body: "beta body");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");

        Assert.Equal(2, await refresh.RefreshAsync());
        Assert.Equal(2, gateway.VersionRequests);
        var markerBefore = File.GetLastWriteTimeUtc(Path.Combine(_store, "alpha", SkillDirectoryInstaller.MarkerFileName));
        var skillMdBefore = File.GetLastWriteTimeUtc(Path.Combine(_store, "alpha", "SKILL.md"));

        await Task.Delay(50); // so a rewrite would move the timestamps
        Assert.Equal(2, await refresh.RefreshAsync());

        Assert.Equal(2, gateway.VersionRequests); // not 4: nothing was downloaded again
        Assert.Equal(2, gateway.RegisterRequests); // the register itself is still read every time
        Assert.Equal(markerBefore, File.GetLastWriteTimeUtc(Path.Combine(_store, "alpha", SkillDirectoryInstaller.MarkerFileName)));
        Assert.Equal(skillMdBefore, File.GetLastWriteTimeUtc(Path.Combine(_store, "alpha", "SKILL.md")));
        Assert.Contains("alpha body", File.ReadAllText(Path.Combine(_store, "alpha", "SKILL.md")));
    }

    [Fact]
    public async Task ABumpedVersion_IsDownloadedAndInstalled()
    {
        var gateway = new FakeGateway();
        gateway.Serve("alpha", version: 3, hash: "h3", body: "old body");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");
        await refresh.RefreshAsync();
        Assert.Equal(1, gateway.VersionRequests);

        gateway.Serve("alpha", version: 4, hash: "h4", body: "new body");
        await refresh.RefreshAsync();

        Assert.Equal(2, gateway.VersionRequests);
        Assert.Contains("new body", File.ReadAllText(Path.Combine(_store, "alpha", "SKILL.md")));
        Assert.Equal(new[] { "alpha", "4", "h4" }, File.ReadAllLines(Path.Combine(_store, "alpha", SkillDirectoryInstaller.MarkerFileName)));
    }

    [Fact]
    public async Task TheSameVersionWithADifferentContentHash_IsDownloadedAgain()
    {
        var gateway = new FakeGateway();
        gateway.Serve("alpha", version: 3, hash: "h3", body: "body one");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");
        await refresh.RefreshAsync();

        gateway.Serve("alpha", version: 3, hash: "h3-corrected", body: "body two");
        await refresh.RefreshAsync();

        Assert.Equal(2, gateway.VersionRequests);
        Assert.Contains("body two", File.ReadAllText(Path.Combine(_store, "alpha", "SKILL.md")));
    }

    [Fact]
    public async Task ARegisterWithoutContentHashes_IsDecidedByVersionAlone()
    {
        var gateway = new FakeGateway { IncludeHashInRegister = false };
        gateway.Serve("alpha", version: 3, hash: "h3", body: "alpha body");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");
        await refresh.RefreshAsync();
        await refresh.RefreshAsync();

        Assert.Equal(1, gateway.VersionRequests);
    }

    [Fact]
    public async Task ASkillWhoseDirectoryWasRemovedByHand_IsDownloadedAgain()
    {
        var gateway = new FakeGateway();
        gateway.Serve("alpha", version: 3, hash: "h3", body: "alpha body");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");
        await refresh.RefreshAsync();

        Directory.Delete(Path.Combine(_store, "alpha"), recursive: true);
        await refresh.RefreshAsync();

        Assert.Equal(2, gateway.VersionRequests);
        Assert.True(File.Exists(Path.Combine(_store, "alpha", "SKILL.md")));
    }

    [Fact]
    public async Task ASkillTheRegisterNoLongerServes_IsStillDroppedFromTheStore()
    {
        var gateway = new FakeGateway();
        gateway.Serve("alpha", version: 3, hash: "h3", body: "alpha body");
        gateway.Serve("beta", version: 1, hash: "h1", body: "beta body");
        var refresh = new SkillStoreRefresh(_store, gateway.Client, "http://gateway.test", "token");
        await refresh.RefreshAsync();
        Assert.True(Directory.Exists(Path.Combine(_store, "beta")));

        gateway.Withdraw("beta");
        Assert.Equal(1, await refresh.RefreshAsync());

        Assert.False(Directory.Exists(Path.Combine(_store, "beta")));
        Assert.True(Directory.Exists(Path.Combine(_store, "alpha")));
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>An in-memory Gateway serving the two skill endpoints the refresh reads, counting each.</summary>
    private sealed class FakeGateway
    {
        private readonly Dictionary<string, (int Version, string Hash, string Body)> _skills = new(StringComparer.OrdinalIgnoreCase);
        public int RegisterRequests;
        public int VersionRequests;
        public bool IncludeHashInRegister { get; init; } = true;
        public HttpClient Client { get; }

        public FakeGateway()
        {
            Client = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://gateway.test") };
        }

        public void Serve(string id, int version, string hash, string body) => _skills[id] = (version, hash, body);
        public void Withdraw(string id) => _skills.Remove(id);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/gateway/skills")
            {
                Interlocked.Increment(ref RegisterRequests);
                var rows = _skills.Select(kv => IncludeHashInRegister
                    ? (object)new { id = kv.Key, version = kv.Value.Version, enabled = true, contentHash = kv.Value.Hash }
                    : new { id = kv.Key, version = kv.Value.Version, enabled = true });
                return Json(new { skills = rows });
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // gateway / skills / {id} / versions / {version}
            if (parts.Length == 5 && parts[0] == "gateway" && parts[1] == "skills" && parts[3] == "versions")
            {
                Interlocked.Increment(ref VersionRequests);
                var id = Uri.UnescapeDataString(parts[2]);
                if (_skills.TryGetValue(id, out var skill) && skill.Version.ToString() == parts[4])
                {
                    return Json(new
                    {
                        version = skill.Version,
                        summary = $"{id} summary",
                        triggers = new[] { id },
                        bodyMarkdown = skill.Body,
                        files = Array.Empty<object>(),
                        contentHash = skill.Hash,
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FakeGateway _owner;
            public Handler(FakeGateway owner) => _owner = owner;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(_owner.Respond(request));
        }
    }
}
