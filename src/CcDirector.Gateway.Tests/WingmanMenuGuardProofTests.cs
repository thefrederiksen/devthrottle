using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The wingman MENU GUARD (issue #2193), proven end-to-end against a REAL streamMode
/// <see cref="GatewayHost"/> over the tunnel. Two claims are pinned here, and both are about what actually
/// reaches the terminal - every dispatched command is recorded, so a test proves exactly what did and did
/// not get sent:
///
///   1. A prompt sent WITH the guard is REFUSED when a menu owns the live screen. Nothing is typed and no
///      Enter is pressed. This matters more than it sounds: a voice reply always carries AppendEnter, so a
///      reply typed at a picker would confirm whichever option happened to be highlighted - a selection the
///      person never made and is never told about.
///   2. The guard is OPT-IN and changes nothing else. A prompt sent WITHOUT it is forwarded even on a menu,
///      and the Gateway does not so much as read the screen - so the typed composer, the Chat send, and
///      every automation caller pay neither the behaviour change nor the extra tunnel read.
///
/// Phase 1 refuses ONLY on a confidently-recognized menu. An unreadable screen forwards, exactly as it did
/// before the guard existed - proven below, because getting that wrong would silently break ordinary voice
/// replies, which is far worse than the gap it would close.
///
/// No wingman brain is configured in this harness. The waiting-screen endpoint answering correctly is
/// therefore also the proof that it makes no model call - a brain call here would fail.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WingmanMenuGuardProofTests : IAsyncLifetime
{
    private const string Token = "test-token-menu-guard-proof";
    private const string DirectorId = "dir-menu-guard";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-mgproof-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Every command the Gateway dispatched over the tunnel, in order (thread-safe).</summary>
    private readonly ConcurrentQueue<DirectorCommand> _commands = new();

    /// <summary>The per-test verb dispatcher; set by each test before it drives the endpoint.</summary>
    private Func<DirectorCommand, DirectorCommandResult> _dispatch =
        cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

    public WingmanMenuGuardProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-mgproof-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // THE SESSION SUPERVISOR IS SWITCHED OFF. Pushing a session that has stopped is a turn end, and the
        // supervisor answers every turn end with one read of that session's live screen, looking for a dropped
        // connection to recover from. That read is the "screen-grid" verb every assertion here counts, so with
        // the supervisor on, "the guard never reads the screen" failed on a read the guard did not make, and
        // "the guard reads the screen" could pass on one it did not make either. Switched off, every screen
        // read left in this Gateway is the menu guard's own.
        _gateway.TenantSettingsResolver.SetSessionSupervisorEnabled(
            CcDirector.Core.Tenancy.TenantId.Local, false, DateTime.UtcNow);

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59926/", // nothing listens here
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", cmd =>
        {
            _commands.Enqueue(cmd);
            return _dispatch(cmd);
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    private static DirectorCommandResult Ok(object body) =>
        DirectorCommandResult.Success(JsonSerializer.Serialize(body, Web));

    private async Task<string> PushSessionAsync()
    {
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid,
            Name = "a voice session",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });
        return sid;
    }

    private List<PromptRequest> DispatchedPrompts()
    {
        var prompts = new List<PromptRequest>();
        foreach (var cmd in _commands)
            if (cmd.Verb == "prompt")
            {
                var req = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson ?? "{}", Web);
                if (req is not null) prompts.Add(req);
            }
        return prompts;
    }

    // A real Claude permission menu drawn with its selection marker. Like the real Ink picker, the hardware
    // cursor is HIDDEN and its cell is stale - the menu is owned by the drawn marker.
    private static ScreenGridResponse MenuGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = new List<string> { "Do you want to proceed?", "❯ 1. Yes", "  2. No" },
        CursorRow = 0,
        CursorCol = -1,
        CursorVisible = false,
        IsAlternateScreen = true,
        HasGrid = true,
    };

    // A Claude composer, VISIBLE cursor in the empty input box (row 2), framed by box borders and a footer.
    private static ScreenGridResponse ComposerGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = new List<string>
        {
            "I finished the change. What next?",
            "╭──────────────────────────────────────╮",
            "│ >                                     │",
            "╰──────────────────────────────────────╯",
            "  ? for shortcuts",
        },
        CursorRow = 2,
        CursorCol = 4,
        CursorVisible = true,
        IsAlternateScreen = false,
        HasGrid = true,
    };

    private static PromptResponse AcceptedPrompt() => new()
    {
        Accepted = true,
        SentAt = DateTime.UtcNow,
        BufferCursor = 0,
        ActivityState = "Working",
    };

    // ===================== The guard: a menu refuses, and NOTHING reaches the terminal =====================

    [Fact]
    public async Task Prompt_WithMenuGuard_MenuOnScreen_RefusesAndSendsNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(MenuGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/prompt",
            new { text = "yes go ahead", appendEnter = true, menuGuard = true });

        // A refusal is a normal 200 outcome, not a failure: the caller asked for exactly this behaviour and
        // there is nothing to retry.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["blockedByMenu"]?.GetValue<bool>());
        Assert.False(node?["accepted"]?.GetValue<bool>());
        // The refusal has to reach the EAR - voice mode is hands-free, so a screen-only notice is no notice.
        Assert.False(string.IsNullOrWhiteSpace(node?["blockedSpoken"]?.GetValue<string>()));

        // THE INVARIANT: nothing was typed and no Enter was pressed.
        Assert.Empty(DispatchedPrompts());
        Assert.Contains(_commands, c => c.Verb == "screen-grid");
    }

    // ===================== A composer is forwarded exactly as before =====================

    [Fact]
    public async Task Prompt_WithMenuGuard_ComposerOnScreen_ForwardsThePrompt()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(ComposerGrid(sid)),
            "prompt" => Ok(AcceptedPrompt()),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/prompt",
            new { text = "add a retry to the upload path", appendEnter = true, menuGuard = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["accepted"]?.GetValue<bool>());

        var sent = Assert.Single(DispatchedPrompts());
        Assert.Equal("add a retry to the upload path", sent.Text);
        Assert.True(sent.AppendEnter);
    }

    // ===================== Unreadable is NOT a menu: phase 1 changes nothing there =====================

    [Fact]
    public async Task Prompt_WithMenuGuard_UnreadableScreen_StillForwardsThePrompt()
    {
        // The owning Director cannot answer the screen read. That is UNCERTAINTY, not a menu. Phase 1
        // deliberately forwards here: refusing on uncertainty would silently break ordinary voice replies on
        // any screen the classifier cannot positively resolve, which is worse than the gap it would close.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "no grid"),
            "prompt" => Ok(AcceptedPrompt()),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/prompt",
            new { text = "carry on", appendEnter = true, menuGuard = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sent = Assert.Single(DispatchedPrompts());
        Assert.Equal("carry on", sent.Text);
    }

    // ===================== The guard is opt-in: without it, nothing changes at all =====================

    [Fact]
    public async Task Prompt_WithoutMenuGuard_MenuOnScreen_ForwardsAndNeverReadsTheScreen()
    {
        // Every existing caller - the typed composer, the Chat send, the fleet relay - sends without the
        // guard. They must be byte-for-byte unaffected: the prompt goes through even on a menu, and the
        // Gateway does not spend a tunnel read finding out what the screen is.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(MenuGrid(sid)),   // available, but must never be asked for
            "prompt" => Ok(AcceptedPrompt()),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/prompt",
            new { text = "1", appendEnter = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sent = Assert.Single(DispatchedPrompts());
        Assert.Equal("1", sent.Text);
        Assert.DoesNotContain(_commands, c => c.Verb == "screen-grid");
    }

    // ===================== The cheap read the voice paths ask before a background send =====================

    [Fact]
    public async Task WaitingScreen_MenuOnScreen_SaysMenuAndCannotType()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(MenuGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

        var resp = await _http.GetAsync($"sessions/{sid}/wingman/waiting-screen");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("menu", node?["kind"]?.GetValue<string>());
        Assert.False(node?["canType"]?.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(node?["spoken"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(node?["message"]?.GetValue<string>()));

        // Read-only: asking what the screen is must never type into the session.
        Assert.Empty(DispatchedPrompts());
    }

    [Fact]
    public async Task WaitingScreen_ComposerOnScreen_SaysTextAndCanType()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(ComposerGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

        var resp = await _http.GetAsync($"sessions/{sid}/wingman/waiting-screen");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("text", node?["kind"]?.GetValue<string>());
        Assert.True(node?["canType"]?.GetValue<bool>());
    }

    [Fact]
    public async Task WaitingScreen_UnreadableScreen_SaysBlockedButStillAllowsTyping()
    {
        // "blocked" is the honest report of an unreadable screen - but phase 1 still lets a reply through,
        // so canType stays true. Only a recognized menu refuses.
        var sid = await PushSessionAsync();
        _dispatch = cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "no grid");

        var resp = await _http.GetAsync($"sessions/{sid}/wingman/waiting-screen");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("blocked", node?["kind"]?.GetValue<string>());
        Assert.True(node?["canType"]?.GetValue<bool>());
        Assert.Equal("", node?["spoken"]?.GetValue<string>());
    }
}
