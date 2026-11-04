using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// A fake of our download server (<see cref="DownloadServer.BaseUrl"/>): it serves exactly the files it
/// was given, answers 404 for anything else, and records every address asked for, in order. The channel
/// files and the manifest have the shape the release workflow writes.
/// </summary>
internal sealed class FakeDownloadServer : HttpMessageHandler
{
    public const string Base = DownloadServer.BaseUrl;

    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    /// <summary>Write a channel file ("stable.json" or "prerelease.json") naming <paramref name="tag"/>.</summary>
    public FakeDownloadServer Channel(string channel, string tag, bool prerelease = false)
    {
        var version = tag.TrimStart('v');
        var json = $$"""{"version":"{{version}}","tag":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"manifest":"{{tag}}/release-manifest.json","date":"2026-11-03T00:00:00Z"}""";
        _routes[$"{Base}/{channel}"] = () => Json(json);
        return this;
    }

    /// <summary>Write {tag}/release-manifest.json listing <paramref name="assets"/> (the manifest itself is always listed).</summary>
    public FakeDownloadServer Release(string tag, params string[] assets)
    {
        var version = tag.TrimStart('v');
        var names = assets.Append("release-manifest.json").Distinct();
        var entries = string.Join(",", names.Select(n =>
            $$"""
            "{{n}}":{"version":"{{version}}","sha256":"","platform":"any","size":0}
            """));
        var json = $$"""{"version":"{{version}}","tag":"{{tag}}","assets":{""" + entries + "}}";
        _routes[$"{Base}/{tag}/release-manifest.json"] = () => Json(json);
        return this;
    }

    /// <summary>Serve <paramref name="body"/> as the manifest of <paramref name="tag"/>, verbatim.</summary>
    public FakeDownloadServer RawManifest(string tag, string body)
    {
        _routes[$"{Base}/{tag}/release-manifest.json"] = () => Json(body);
        return this;
    }

    /// <summary>Serve <paramref name="body"/> at <paramref name="url"/>, verbatim.</summary>
    public FakeDownloadServer Raw(string url, string body)
    {
        _routes[url] = () => Json(body);
        return this;
    }

    public FakeDownloadServer Answer(string url, HttpStatusCode status)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent("") };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests) Requests.Add(url);
        return Task.FromResult(_routes.TryGetValue(url, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no file at {url}") });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
