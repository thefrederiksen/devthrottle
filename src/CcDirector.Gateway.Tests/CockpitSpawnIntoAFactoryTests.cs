using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE COCKPIT SPAWN FORM'S FACTORY FIELD REACHES THE DIRECTOR (Factory Memory mission, phase 3b).
///
/// The Cockpit's New Session dialog is the only way a person starts a session into a factory by hand. It posts
/// to POST /directors/{id}/sessions with a browser's device key and a <c>factory</c> in the body. These tests
/// drive that exact door and read what LEAVES the Gateway - the create the Director is sent - because the phase 1
/// review's sharpest finding was that the rule could stop being applied at a door while every test of the rule
/// stayed green. A test of the helper alone would not notice this door dropping the field.
///
/// The Director is a capture, not a real one. The device type is stamped by a stand-in for the auth middleware,
/// the same way <see cref="FactoryMemoryRouteTests"/> does it, because what is under test is the door, not the
/// key registry.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class CockpitSpawnIntoAFactoryTests : IAsyncDisposable
{
    private const string DirectorId = "dir-spawn-3b";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-spawn-3b-" + Guid.NewGuid().ToString("N"));

    private WebApplication? _app;
    private HttpClient? _http;
    private DirectorRegistry? _registry;

    /// <summary>The create request the door dispatched, or null when nothing was dispatched.</summary>
    private NewSessionRequest? _sent;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        _registry?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    private async Task StartAsync(string? deviceType, Guid? sessionId = null, SessionFactoryLookup? callerFactory = null)
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (deviceType is not null) ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
            if (sessionId is { } sid)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(sid, TenantId.Local, DirectorId);
            await next();
        });

        _registry = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _registry.RegisterFromStream(DirectorId, "SPAWN-PC", "test", "0.0.0-test", pid: 1,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);

        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            if (command.Verb == "create")
            {
                _sent = JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web);
                var reply = new SessionDto { SessionId = Guid.NewGuid().ToString() };
                return Task.FromResult<DirectorCommandResult?>(
                    DirectorCommandResult.Success(JsonSerializer.Serialize(reply, Web)));
            }
            return Task.FromResult<DirectorCommandResult?>(null);
        };

        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry());
        var lookup = callerFactory ?? SessionFactoryLookup.InNoFactory;
        GatewayEndpoints.Map(app, _registry, version: "test", token: "test-token",
            tenantBoundary: boundary, sendCommand: send, sessionFactoryOf: _ => lookup);

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BoundPort.Of(app)}/") };
    }

    /// <summary>The body the Cockpit's createSession sends, with or without the factory field.</summary>
    private Task<HttpResponseMessage> CockpitSpawn(string? factory) =>
        _http!.PostAsJsonAsync($"directors/{DirectorId}/sessions", factory is null
            ? new { repoPath = @"C:\repo", agent = "ClaudeCode", wingmanEnabled = false }
            : (object)new { repoPath = @"C:\repo", agent = "ClaudeCode", wingmanEnabled = false, factory });

    [Fact]
    public async Task A_PERSON_IN_THE_COCKPIT_starts_a_session_into_the_factory_they_named()
    {
        await StartAsync(deviceType: "browser");

        var response = await CockpitSpawn("website-factory");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_sent);
        Assert.Equal("website-factory", _sent!.Factory);
    }

    [Fact]
    public async Task The_field_left_empty_starts_a_session_in_no_factory()
    {
        await StartAsync(deviceType: "browser");

        var response = await CockpitSpawn(factory: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_sent);
        Assert.Null(_sent!.Factory);
    }

    /// <summary>
    /// The contrast that shows the field is honoured because a PERSON sent it, not because it was in the body: the
    /// same body from a session in no factory is refused at the same door, and nothing reaches the Director.
    /// </summary>
    [Fact]
    public async Task The_same_body_from_a_session_in_no_factory_is_refused_and_nothing_is_dispatched()
    {
        await StartAsync(deviceType: null, sessionId: Guid.NewGuid(), callerFactory: SessionFactoryLookup.InNoFactory);

        // A session must also say who owns what it starts; that is declared here so the only thing left to refuse
        // is the factory.
        var response = await _http!.PostAsJsonAsync($"directors/{DirectorId}/sessions", new
        {
            repoPath = @"C:\repo", agent = "ClaudeCode", wingmanEnabled = false,
            factory = "website-factory", controllerSessionId = "none",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("is in no factory", await response.Content.ReadAsStringAsync());
        Assert.Null(_sent);
    }
}
