using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// The Director's polled readers through their held answers, in the DEFAULT test run: a stub Gateway that
/// tags its answers with a hash of the bytes and says 304 to a matching If-None-Match, as the real one does.
/// The same chain is proved against the real Gateway in Gateway.Tests (DirectorPolledReadsNotModifiedHostTests),
/// which is parked; these keep the property guarded at commit time.
///
/// The skill store is the one that matters most: it deletes every skill the register does not name, so a
/// 304 - or a register with no skill list - that read as "no skills" would wipe the machine's skills.
/// </summary>
public sealed class DirectorPolledReadsHeldTests : IDisposable
{
    private const string Url = "http://gateway.test";
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "ccd-polled-held-" + Guid.NewGuid().ToString("N"));

    public DirectorPolledReadsHeldTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SkillStore_UnchangedRegisterIs304_AndEverySkillIsKept()
    {
        var gateway = new TaggingGateway();
        var store = Path.Combine(_scratch, "store");
        var refresh = new SkillStoreRefresh(store, gateway.Client, Url, "token", new HeldGatewayAnswers());

        Assert.Equal(1, await refresh.RefreshAsync());
        Assert.Equal(1, await refresh.RefreshAsync());

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, gateway.StatusesFor("/gateway/skills"));
        Assert.True(File.Exists(Path.Combine(store, "alpha", "SKILL.md")));
    }

    [Fact]
    public async Task SkillStore_RegisterWithNoSkillList_KeepsEverySkill()
    {
        var gateway = new TaggingGateway();
        var store = Path.Combine(_scratch, "store");
        var refresh = new SkillStoreRefresh(store, gateway.Client, Url, "token", new HeldGatewayAnswers());
        Assert.Equal(1, await refresh.RefreshAsync());

        gateway.Register = "{}";
        Assert.Equal(-1, await refresh.RefreshAsync());

        Assert.True(File.Exists(Path.Combine(store, "alpha", "SKILL.md")));
    }

    [Fact]
    public async Task SkillIndex_UnchangedRegisterIs304_AndTheIndexIsKept()
    {
        var gateway = new TaggingGateway();
        var index = new SkillIndexStore(Path.Combine(_scratch, "index.json"), gateway.Client, Url, "token",
            new HeldGatewayAnswers());

        await index.RefreshAsync();
        var first = index.ReadCache()!;
        await index.RefreshAsync();

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, gateway.StatusesFor("/gateway/skills"));
        Assert.Contains("- alpha:", index.ReadCache()!.Index);
        Assert.Equal(first.Index, index.ReadCache()!.Index);
    }

    [Fact]
    public async Task InjectedText_UnchangedIs304_AndAnEditArrives()
    {
        var gateway = new TaggingGateway();
        var text = new InjectedTextStore(Path.Combine(_scratch, "injected.json"), gateway.Client, Url, "token",
            new HeldGatewayAnswers());

        await text.RefreshAsync();
        await text.RefreshAsync();
        Assert.Equal(InjectedTextSource.Ours, text.ActiveSource());

        gateway.InjectedText = "{\"use_yours\":true,\"yours\":\"Mine.\"}";
        await text.RefreshAsync();

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified, HttpStatusCode.OK },
            gateway.StatusesFor("/gateway/injected-text"));
        Assert.Equal(InjectedTextSource.Yours, text.ActiveSource());
    }

    [Fact]
    public async Task WorkflowIndex_UnchangedCatalogIs304_AndTheIndexIsKept()
    {
        var gateway = new TaggingGateway();
        var index = new WorkflowIndexStore(Path.Combine(_scratch, "workflows.json"), gateway.Client, Url, "token",
            new HeldGatewayAnswers());

        await index.RefreshAsync();
        var first = index.ReadCache()!.Index;
        await index.RefreshAsync();

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, gateway.StatusesFor("/gateway/workflows"));
        Assert.Contains("mission", first);
        Assert.Equal(first, index.ReadCache()!.Index);
    }

    /// <summary>A stub Gateway that tags each answer with a hash of its bytes, as ConditionalJson does.</summary>
    private sealed class TaggingGateway
    {
        private readonly List<(string Path, HttpStatusCode Status)> _seen = new();

        public string Register { get; set; } =
            "{\"skills\":[{\"id\":\"alpha\",\"summary\":\"Alpha.\",\"version\":1,\"enabled\":true,\"contentHash\":\"h1\"}]}";
        public string InjectedText { get; set; } = "{\"use_yours\":false,\"yours\":null}";
        public string Workflows { get; set; } = "{\"workflows\":[{\"id\":\"mission\",\"summary\":\"The conduct.\",\"enabled\":true}]}";

        public HttpClient Client { get; }

        public TaggingGateway() => Client = new HttpClient(new Handler(this));

        public HttpStatusCode[] StatusesFor(string path) =>
            _seen.Where(s => s.Path == path).Select(s => s.Status).ToArray();

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? json = path switch
            {
                "/gateway/skills" => Register,
                "/gateway/injected-text" => InjectedText,
                "/gateway/workflows" => Workflows,
                "/gateway/skills/alpha/versions/1" => JsonSerializer.Serialize(new
                {
                    version = 1, summary = "Alpha.", triggers = new[] { "alpha" }, bodyMarkdown = "alpha body",
                    files = Array.Empty<object>(), contentHash = "h1",
                }),
                _ => null,
            };
            if (json is null)
                return Record(path, new HttpResponseMessage(HttpStatusCode.NotFound));

            var bytes = Encoding.UTF8.GetBytes(json);
            var tag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "\"";
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == tag))
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = EntityTagHeaderValue.Parse(tag);
                return Record(path, notModified);
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            ok.Headers.ETag = EntityTagHeaderValue.Parse(tag);
            return Record(path, ok);
        }

        private HttpResponseMessage Record(string path, HttpResponseMessage response)
        {
            lock (_seen) _seen.Add((path, response.StatusCode));
            return response;
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly TaggingGateway _owner;
            public Handler(TaggingGateway owner) => _owner = owner;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(_owner.Respond(request));
        }
    }
}
