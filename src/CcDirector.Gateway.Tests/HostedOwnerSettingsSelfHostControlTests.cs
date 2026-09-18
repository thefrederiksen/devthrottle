using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE SELF-HOST CONTROL FOR THE OWNER-SETTINGS DENY, STATED EXPLICITLY (issue #1863).
///
/// Self-host is the control for the whole hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. The existing settings tests do not prove it: they never mention <c>CC_GATEWAY_HOSTED</c> and
/// pass only because the runner happens to leave it unset. If that ambient default ever flipped - one
/// leaked environment variable, one continuous-integration image change, one test that forgot to restore
/// it - they would keep passing while self-host was completely broken, because they assert nothing about
/// which mode they are in.
///
/// So this class sets the variable ITSELF, to BOTH non-hosted forms that occur in practice - ABSENT, and
/// PRESENT-BUT-"0" - and asserts <see cref="GatewayHostedMode.IsHosted"/> really is false in each, so no
/// test below can silently be running in the mode it thinks it is not in.
///
/// AND IT ASSERTS A POSITIVE FACT PER ROUTE, NOT THE ABSENCE OF THE REFUSAL. "The refusal string is not
/// in the body" is satisfied by an empty 200, by a single-page-application shell, and by a route that was
/// deleted - it proves nothing about the route being alive. All twenty-four routes are proved by one of:
///   - its REAL PAYLOAD, field by field (the nine read routes);
///   - an INDEPENDENTLY RE-READ EFFECT - the value is written over the wire and then read back out of the
///     configuration store through the Core config class, not out of the response (eleven write routes);
///   - a SEEDED TRANSITION off a sentinel the effect cannot produce, where the effect is a RESET rather
///     than a stored value (the provider, one route);
///   - SERVED-AND-REACHES-A-HANDLER, the deliberately NARROWED claim for the one route that can carry no
///     stronger one: transcription mode is SINGLE-VALUED BY CONSTRUCTION, so no stored state differs from
///     what it writes and no seeding could distinguish it from a no-op. That is proved, not asserted, by
///     The_transcription_mode_setting_is_single_valued_so_no_write_is_observable - which also reddens the
///     day a second mode is added, so the narrowing cannot go stale (one route);
///   - a HANDLER-UNIQUE RECEIPT: a status and message only that one handler can produce, where the route
///     has no readable effect to assert - the two credential-resolution routes (two routes).
/// Nine plus eleven plus one plus one plus two is twenty-four. No route is left over, and none is proved
/// only by the absence of the refusal.
///
/// Issue #2022 removed the five machine-scoped routes this control once also covered - brain restart (the
/// injected-seam receipt), brain config and autostart (two of the handler-unique receipts), and the network
/// addressing GET+PUT (one read + one write) - because those settings left the web page. Their served-side
/// tests went with them; the arithmetic above is the post-removal accounting.
///
/// These tests must stay GREEN through the revert. A control that moves with the change under test is not
/// a control.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsSelfHostControlTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-self-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public HostedOwnerSettingsSelfHostControlTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-self-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// null = the variable is ABSENT. "0" = the variable is PRESENT and explicitly not hosted. Both are
    /// real self-host deployments and both must serve. Stating both is the point: a gate written against
    /// "the variable is set at all" would pass the first and fail the second.
    /// </summary>
    public static TheoryData<string?> NonHostedForms => new() { null, "0" };

    /// <summary>
    /// Puts the process into a STATED non-hosted mode and proves the statement took, so nothing below can
    /// silently be running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    public static TheoryData<string?, string> ReadRoutes
    {
        get
        {
            var data = new TheoryData<string?, string>();
            foreach (var hosted in new string?[] { null, "0" })
                foreach (var path in new[]
                         {
                             "gateway/settings",
                             "gateway/snooze-default",
                             "gateway/daily-report",
                             "gateway/mentor-report",
                             "gateway/injected-text",
                             "gateway/snooze-presets",
                             "gateway/time-zone",
                             "gateway/transcription-mode",
                             "gateway/ai-provider",
                             "gateway/tts-voice",
                         })
                    data.Add(hosted, path);
            return data;
        }
    }

    /// <summary>
    /// The nine read routes, each asserted by the REAL PAYLOAD it carries - the fields, and where a field
    /// has an independently knowable value, that value. Format facts (status, media type) are asserted
    /// BEFORE the body is parsed, on this side too: if a route were gone, the Gateway's single-page
    /// -application fallback would answer with HTML and parsing first would turn that finding into a crash.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task Every_read_route_serves_its_real_payload_on_self_host(string? hostedForm, string path)
    {
        DeclareSelfHost(hostedForm);

        var response = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // The refusal envelope is exactly one property named "error". Proving the real payload means
        // naming what this route actually carries, one field at a time.
        var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("error", properties);

        switch (path)
        {
            case "gateway/settings":
                // Issue #2022: the machine settings left the web page, so the snapshot carries only the
                // per-account settings the collapsed page renders. The process diagnostics (version, state,
                // port, uptime, directors, mode), the cockpit block, the brain block, the autostart state,
                // and the addressing mode are GONE from here - they moved to GET /gateway/about or were
                // dropped. An exact allow-list over the whole property set is what pins that removal: a
                // regression that re-added any machine field would redden here, not slip through.
                Assert.Equal(new[]
                {
                    "snoozeDefaultMinutes", "snoozePresets",
                    "snoozeMaxPresets", "timeZone", "timeZoneMachineDefault", "dailyReportCadence",
                    "mentorReportEnabled",
                    // The Wingman's two turn-judging switches (the Wingman-on-every-turn mission). They are
                    // per-account settings and they ride on this snapshot rather than on a GET of their own,
                    // because the card that draws them needs one fetch and this is the document every card on
                    // that page already reads. Adding them here is the acknowledgement this exact allow-list
                    // exists to demand - it went red on the day they were added, which is its whole job.
                    "turnVerdictJudgeEnabled", "turnVerdictColourEnabled",
                }, properties);
                Assert.True(root.GetProperty("mentorReportEnabled").GetBoolean());
                // Both default OFF, and that direction is asserted rather than assumed: ON for the colour
                // switch means a session painted calm, which is the failure nobody is woken by.
                Assert.False(root.GetProperty("turnVerdictJudgeEnabled").GetBoolean());
                Assert.False(root.GetProperty("turnVerdictColourEnabled").GetBoolean());
                Assert.Equal(ReportCadences.DailyName, root.GetProperty("dailyReportCadence").GetString());
                Assert.True(root.GetProperty("snoozePresets").GetArrayLength() > 0);
                Assert.Equal(SnoozePresetsConfig.MaxPresets, root.GetProperty("snoozeMaxPresets").GetInt32());
                break;

            case "gateway/snooze-default":
                Assert.Equal(new[] { "minutes" }, properties);
                Assert.Equal(SnoozeDefaultConfig.Get(), root.GetProperty("minutes").GetInt32());
                break;

            case "gateway/daily-report":
                // The account's report cadence (issue #1000). Its independently knowable value is the
                // documented default - daily - because a Gateway nobody has configured must still be
                // mailing everyone who has an address, exactly as it did before the setting existed.
                Assert.Equal(new[] { "cadence" }, properties);
                Assert.Equal(ReportCadences.DailyName, root.GetProperty("cadence").GetString());
                break;

            case "gateway/mentor-report":
                // Whether this account receives the mentor report (devthrottle_internal#1661). Its
                // independently knowable value is the documented default - ON - because a Gateway nobody has
                // configured must answer the way every account was treated before the setting existed.
                Assert.Equal(new[] { "enabled" }, properties);
                Assert.True(root.GetProperty("enabled").GetBoolean());
                break;

            case "gateway/injected-text":
                Assert.Equal(new[] { "use_yours", "yours", "ours", "placeholders" }, properties);
                // "ours" is the shipped agent-launch instruction text, and its presence here is exactly the
                // disclosure the hosted deny closes - so on self-host it must really be here, not empty.
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("ours").GetString()));
                Assert.True(root.GetProperty("placeholders").GetArrayLength() > 0);
                break;

            case "gateway/snooze-presets":
                Assert.Equal(new[] { "presets", "defaultMinutes", "maxPresets" }, properties);
                Assert.True(root.GetProperty("presets").GetArrayLength() > 0);
                Assert.Equal(SnoozePresetsConfig.MaxPresets, root.GetProperty("maxPresets").GetInt32());
                break;

            case "gateway/time-zone":
                Assert.Equal(new[] { "timeZone", "machineDefault" }, properties);
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("machineDefault").GetString()));
                break;

            case "gateway/transcription-mode":
                Assert.Equal(new[] { "mode" }, properties);
                Assert.Equal("devthrottle", root.GetProperty("mode").GetString());
                break;

            case "gateway/ai-provider":
                foreach (var expected in new[]
                         {
                             "provider", "wingmanModel", "wingmanFastModel", "transcriptionModel", "ttsModel",
                             "ttsVoice", "voices",
                         })
                    Assert.Contains(expected, properties);
                // The Assistant's fleet-brain model and the Car Mode end phrase left the product with the
                // Assistant (the Fleet Manager mission, step 9); the read must not carry them any more.
                Assert.DoesNotContain("carModeModel", properties);
                Assert.DoesNotContain("carModeEndPhrase", properties);
                Assert.Equal("devthrottle", root.GetProperty("provider").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("wingmanModel").GetString()));
                break;

            case "gateway/tts-voice":
                Assert.Equal(new[] { "voice", "voices" }, properties);
                Assert.True(root.GetProperty("voices").GetArrayLength() > 0);
                Assert.Equal(TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()),
                    root.GetProperty("voice").GetString());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "no payload assertion written for this route");
        }
    }

    public static TheoryData<string?, string, string, string, string, string> WriteRoutes
    {
        get
        {
            var data = new TheoryData<string?, string, string, string, string, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                data.Add(hosted, "PUT", "gateway/snooze-default", "{\"minutes\":45}", "snooze-default", "45");
                data.Add(hosted, "PUT", "gateway/injected-text", "{\"use_yours\":true,\"yours\":\"words from another tenant\"}", "injected-text", "words from another tenant");
                data.Add(hosted, "PUT", "gateway/snooze-presets", "{\"presets\":[15,30,60],\"defaultMinutes\":30}", "snooze-presets", "15,30,60");
                data.Add(hosted, "PUT", "gateway/time-zone", "{\"timeZone\":\"America/New_York\"}", "time-zone", "America/New_York");
                // "off" is the value that is distinguishable from the default, so the re-read proves a write
                // rather than a no-op.
                data.Add(hosted, "PUT", "gateway/daily-report", "{\"cadence\":\"off\"}", "daily-report", "off");
                // false is the value distinguishable from the default, so the re-read proves a write.
                data.Add(hosted, "PUT", "gateway/mentor-report", "{\"enabled\":false}", "mentor-report", "false");
                // transcription-mode is NOT here - it needs a seeded starting value to be distinguishable
                // from a no-op, so it has its own test below.
                data.Add(hosted, "PUT", "gateway/tts-voice", "{\"voice\":\"shimmer\"}", "tts-voice", "shimmer");
                // Model values are DevThrottle internal included ids (issue #1360: the resolver honors
                // nothing else), each chosen OPPOSITE to its role's default so the re-read still proves
                // a write rather than a no-op.
                data.Add(hosted, "PUT", "gateway/ai/wingman-model", "{\"model\":\"devthrottle/wingman-fast\"}", "wingman-model", "devthrottle/wingman-fast");
                data.Add(hosted, "PUT", "gateway/ai/wingman-fast-model", "{\"model\":\"devthrottle/wingman\"}", "wingman-fast-model", "devthrottle/wingman");
                data.Add(hosted, "PUT", "gateway/ai/tts-model", "{\"model\":\"hosted-speech\"}", "tts-model", "hosted-speech");
            }
            return data;
        }
    }

    /// <summary>
    /// Reads a setting back out of the STORE it actually persists to, NOT out of the response body. Reading
    /// the response would only prove the handler can echo; reading the store proves the write landed.
    ///
    /// The per-tenant settings (issue #2017) now persist to the tenant_settings store, not config.json, so
    /// they are re-read through this Gateway's own <see cref="GatewayHost.TenantSettingsResolver"/> for the
    /// self-host local tenant - the same store the handler wrote to, read directly, so this is still an
    /// independent store re-read and not a handler echo. The remaining machine-scoped settings still live in
    /// config.json and are re-read through their Core config class.
    /// </summary>
    private string ReadBack(string key) => key switch
    {
        "snooze-default" => _gateway.TenantSettingsResolver.SnoozeDefaultMinutes(TenantId.Local).ToString(),
        "injected-text" => _gateway.TenantSettingsResolver.InjectedText(TenantId.Local).Yours ?? "",
        "snooze-presets" => string.Join(",", _gateway.TenantSettingsResolver.SnoozePresets(TenantId.Local)),
        "time-zone" => _gateway.TenantSettingsResolver.TimeZone(TenantId.Local),
        "daily-report" => ReportCadences.Name(_gateway.TenantSettingsResolver.DailyReportCadence(TenantId.Local)),
        // Lower-cased on purpose: it is compared against the literal the theory row carries, and the
        // spelling the harness parses out of the store is "true"/"false".
        "mentor-report" => _gateway.TenantSettingsResolver.MentorReportEnabled(TenantId.Local) ? "true" : "false",
        "transcription-mode" => TranscriptionModeConfig.Get().ToConfigString(),
        "tts-voice" => _gateway.TenantSettingsResolver.TtsVoice(TenantId.Local, TranscriptionModeConfig.Get()),
        "wingman-model" => _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, TranscriptionModeConfig.Get(), WingmanModelRole.Thinking).Value,
        "wingman-fast-model" => _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, TranscriptionModeConfig.Get(), WingmanModelRole.Fast).Value,
        "tts-model" => _gateway.TenantSettingsResolver.TtsModel(TenantId.Local, TranscriptionModeConfig.Get()),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no read-back written for this setting"),
    };

    /// <summary>
    /// The write routes, each proved by an INDEPENDENTLY RE-READ EFFECT: the value goes over the wire
    /// and is then read back out of the configuration store through its Core config class. A 200 alone
    /// would not do - a handler that accepted the request and wrote nothing returns the same 200. (Issue
    /// #2022 removed the PUT <c>gateway/addressing-mode</c> row along with the setting.)
    ///
    /// <c>PUT /gateway/transcription-mode</c> is the one row whose re-read cannot distinguish a write from
    /// a no-op, because "devthrottle" is the only value the endpoint accepts and is also the default. Its
    /// 200 and its echoed payload are asserted here for what they are worth, and the row is called out in
    /// the pull request rather than left looking like the others.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Every_write_route_really_writes_and_the_write_reads_back_on_self_host(
        string? hostedForm, string verb, string path, string body, string readBackKey, string expected)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("error", document.RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Equal(expected, ReadBack(readBackKey));
    }

    /// <summary>
    /// WHY <c>PUT /gateway/transcription-mode</c> CANNOT HAVE AN OBSERVABLE WRITE EFFECT - proved, not
    /// asserted, so the narrower claim made for that route below rests on a checked fact.
    ///
    /// The reviewer's finding was that writing the sole accepted value, which is also the default, cannot
    /// distinguish a real write from a no-op, and offered two ways out: seed a persistent transition, or
    /// narrow the claim if the route has no distinguishable mutation. The first was ATTEMPTED FIRST and
    /// FAILED, which is what established the second is correct: seeding the store to the other enum value
    /// and reading it back returns DevThrottle, because the setting is SINGLE-VALUED BY CONSTRUCTION.
    ///
    /// Both halves of that are pinned here. Every value the type can serialize writes "devthrottle", and
    /// every value it will parse - including all the legacy provider strings - resolves to DevThrottle. So
    /// no reachable stored state differs from the one the route writes, and no seeding strategy could
    /// distinguish the handler from a no-op. That is a property of the setting, not a gap in the test.
    ///
    /// THIS TEST IS ALSO THE EXPIRY DATE ON THAT CAVEAT. If a second real transcription mode is ever added,
    /// this reddens immediately and tells whoever added it that the route now needs a genuine
    /// seeded-transition proof, rather than leaving a stale narrowing in place forever.
    /// </summary>
    [Fact]
    public void The_transcription_mode_setting_is_single_valued_so_no_write_is_observable()
    {
        foreach (var mode in Enum.GetValues<TranscriptionMode>())
            Assert.Equal("devthrottle", mode.ToConfigString());

        foreach (var stored in new string?[] { null, "", "  ", "devthrottle", "byo", "openai", "local" })
            Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeExtensions.Parse(stored));
    }

    /// <summary>
    /// <c>PUT /gateway/transcription-mode</c> - the NARROWED claim, stated as exactly what it proves.
    ///
    /// It proves the route is SERVED on self-host and reaches a handler that answers with the setting's
    /// value. It does NOT prove a store mutation, and the test above is why no test could. Calling this an
    /// independently re-read effect - as the previous revision's table did - would be claiming more than
    /// the evidence supports, which is the thing this whole review has been about.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedForms))]
    public async Task The_transcription_mode_route_is_served_on_self_host(string? hostedForm)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, "PUT", "gateway/transcription-mode",
            "{\"mode\":\"devthrottle\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "mode" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("devthrottle", document.RootElement.GetProperty("mode").GetString());

        // Not a transition - see the single-valued proof above. This states the post-condition that IS
        // checkable: the store holds the value the route reports.
        Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeConfig.Get());
    }

    /// <summary>
    /// <c>PUT /gateway/ai-provider</c> on its own, because its effect is a RESET rather than a value being
    /// stored: it repoints the wingman, speech and voice settings back to the provider defaults. Proving it
    /// therefore needs a before-state that the reset must move away from, which is what the sentinel write
    /// below is for. That is a real, independently re-read effect - the strongest served-side fact
    /// available for this route.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedForms))]
    public async Task The_provider_reset_really_resets_the_models_on_self_host(string? hostedForm)
    {
        DeclareSelfHost(hostedForm);

        // A value the reset cannot possibly produce, so "unchanged" and "reset" cannot be confused. The reset
        // now clears the TENANT's override (issue #2017), so the before-state is seeded as a tenant override -
        // exactly what the AI tab writes - not a global config value. The sentinel carries the devthrottle/
        // prefix because since issue #1360 the resolver honors only DevThrottle internal included ids - an
        // unprefixed sentinel would fall forward to the default and could not seed a before-state at all.
        const string Sentinel = "devthrottle/sentinel-model-no-reset-would-return";
        var mode = TranscriptionModeConfig.Get();
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantId.Local, WingmanModelRole.Thinking, Sentinel, DateTime.UtcNow);
        Assert.Equal(Sentinel, _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Thinking).Value);

        var response = await OwnerSettingsRoutes.SendAsync(_http, "PUT", "gateway/ai-provider",
            "{\"provider\":\"devthrottle\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("devthrottle", document.RootElement.GetProperty("provider").GetString());

        var afterwards = _gateway.TenantSettingsResolver.WingmanModel(TenantId.Local, mode, WingmanModelRole.Thinking).Value;
        Assert.NotEqual(Sentinel, afterwards);
        Assert.False(string.IsNullOrWhiteSpace(afterwards));
    }

    public static TheoryData<string?, string, string, string, int, string> ReceiptRoutes
    {
        get
        {
            var data = new TheoryData<string?, string, string, string, int, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                // The SPEECH catalog and test-chat routes both resolve the provider credential out of
                // the key vault first. With no credential stored they answer 503 with this exact
                // sentence - a handler-unique receipt that costs no network round-trip. (Issue #2022
                // removed the brain-config receipt route along with the endpoint; issue #1360 made the
                // CHAT kind a fixed local list that needs no credential, so the receipt row pins the
                // speech kind explicitly. The test-chat row sends an INCLUDED id because the handler now
                // refuses a non-included id with a 400 BEFORE resolving the credential - the refusal is
                // pinned by AiModelsEndpointTests; this row pins the credential receipt.)
                data.Add(hosted, "GET", "gateway/ai/models?kind=speech", "",
                    503, "not signed in to DevThrottle - sign in on the Account tab");
                data.Add(hosted, "POST", "gateway/ai/test-chat", "{\"model\":\"devthrottle/wingman\"}",
                    503, "not signed in to DevThrottle - sign in on the Account tab");
            }
            return data;
        }
    }

    /// <summary>
    /// Two routes with no readable effect to assert, each proved by a HANDLER-UNIQUE RECEIPT: a status
    /// and a sentence only that one handler can produce. A refusal is a 404 carrying one <c>error</c>
    /// property, so none of these can be confused with one - and, unlike "the refusal is absent", each of
    /// these is a positive statement about which code ran.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReceiptRoutes))]
    public async Task The_routes_without_a_readable_effect_answer_with_their_own_receipt_on_self_host(
        string? hostedForm, string verb, string path, string body, int expectedStatus, string expectedMessage)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("error").GetString());
    }

    // Issue #2022 removed the served-side proofs for the four machine-scoped routes that left the web page:
    // the two brain-restart tests (a handler receipt + its destructibility control), and the autostart
    // receipt. Those endpoints no longer exist, so there is nothing to serve. The brain-restart SEAM
    // (GatewayHost.BrainRestartAction) stays for the automatic recovery path, but it is no longer reachable
    // over HTTP.
    /// <summary>
    /// An unknown report cadence is REFUSED and CHANGES NOTHING (issue #1000). The second half is the half
    /// that matters: a handler that answered 400 after having already written would leave the account on a
    /// schedule it never asked for while telling it the request failed. The stored value is re-read out of
    /// the resolver, not out of the response.
    /// </summary>
    [Theory]
    [InlineData("{\"cadence\":\"weekly\"}")]
    [InlineData("{\"cadence\":\"\"}")]
    [InlineData("{\"cadence\":null}")]
    [InlineData("{}")]
    public async Task An_unknown_report_cadence_is_refused_and_leaves_the_setting_alone(string body)
    {
        DeclareSelfHost(null);

        // Start from a value that is NOT the default, so a write that wrongly went through would be visible
        // either way - as a change to daily, or as a change to the unknown value.
        (await _http.PutAsync("gateway/daily-report",
            new StringContent("{\"cadence\":\"off\"}", Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

        var refused = await _http.PutAsync("gateway/daily-report",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(ReportCadence.Off, _gateway.TenantSettingsResolver.DailyReportCadence(TenantId.Local));
    }
}

/// <summary>
/// THE SERVED HALF OF THE FUTURE-ROUTE PROOF, and the half most likely to be skipped because everything
/// looks green without it.
///
/// <see cref="HostedOwnerSettingsGroupFilterTests"/> maps a brand-new route through each denied handle and
/// finds it REFUSED on hosted. On its own that cannot tell a working gate apart from a brick: a primitive
/// that refused everything unconditionally would satisfy every one of those assertions while having silently
/// killed the routes for self-host too. This class drives the SAME probe paths with hosted mode explicitly
/// OFF, in BOTH non-hosted forms, and asserts they are SERVED with their real payload.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsSelfHostProbeTests : IAsyncLifetime
{
    private const string ProbePayload = "the-probe-route-really-served-on-self-host";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-selfprobe-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;

    public HostedOwnerSettingsSelfHostProbeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-selfprobe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "probe-token",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    public static TheoryData<string?, string, string> Probes
    {
        get
        {
            var data = new TheoryData<string?, string, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                data.Add(hosted, "settings", "/gateway/added-after-the-deny-was-written");
                data.Add(hosted, "models", "/gateway/ai/added-after-the-deny-was-written");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(
        string? hostedForm, string family, string probePath)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hostedForm);
        Assert.False(GatewayHostedMode.IsHosted);

        Func<IEndpointRouteBuilder, HostedDenyGroup> map = family switch
        {
            "settings" => routes => Api.SettingsEndpoints.Map(routes, _gateway),
            "models" => routes => Api.AiModelsEndpoint.Map(routes, new Core.KeyVault(Path.Combine(_root, "vault.json")), _gateway.TenantSettingsResolver, _gateway.TenantBoundary),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown owner-settings family"),
        };

        // The SAME body-bound POST canary the hosted side refuses (Tenancy/HostedRouteDenyTests shape),
        // driven with hosted mode OFF. A primitive that refused everything unconditionally would satisfy the
        // hosted class while silently killing this route for self-host too, so the served half must be proved.
        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            map,
            mapIntoGroup: group => group.MapPost(probePath,
                (CanaryBody body) => Results.Json(new { probe = ProbePayload, echoed = body.Text })));
        try
        {
            // A valid body binds through the framework and serves the sentinel with what it echoed.
            var served = await http.PostAsync(probePath,
                new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("application/json", served.Content.Headers.ContentType?.MediaType);

            using var document = JsonDocument.Parse(await served.Content.ReadAsStringAsync());
            Assert.Equal(ProbePayload, document.RootElement.GetProperty("probe").GetString());
            Assert.Equal("hello", document.RootElement.GetProperty("echoed").GetString());

            // The binding is the FRAMEWORK's, not a custom binder that ignores the body: a malformed body is
            // the framework's own 400 here. That is what makes the hosted "malformed body meets the refusal"
            // claim a real pre-emption of binding - if this route side-stepped the bytes, the hosted assertion
            // would prove nothing.
            var malformed = await http.PostAsync(probePath,
                new StringContent("{ not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

}
