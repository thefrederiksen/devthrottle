using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2022 part 2: the per-account owner-settings routes were UN-DENIED and now SERVE on the hosted
/// Gateway, each resolving the CALLER's tenant. This is the hostile A/B proof the deny retirement owes -
/// the inverse of <see cref="HostedOwnerSettingsDenyTests"/>, which proves the routes that STAY refused.
///
/// Three things are proved on a real HOSTED GatewayHost with TWO fully enrolled tenants and one unbound
/// device:
///   1. SERVE - every newly-hosted route answers 200 (not the 404 refusal) for an enrolled tenant.
///   2. FAIL CLOSED - the SAME routes answer 403 for a device whose key resolves to NO tenant, never the
///      Local partition. This is the unresolved-tenant matrix: on shared infrastructure an unattributable
///      request must be refused, not served a wrong tenant's data.
///   3. ISOLATED - a write by tenant A is invisible to tenant B's read of the same setting. One account's
///      snooze length, time zone, voice, models and car-mode phrase never reach another's, endpoint to
///      endpoint. (The endpoint-to-RUNTIME half - the consumers reading per tenant - is
///      TenantSettingsRuntimeThreadingTests, from the integrated consumer commit.)
///
/// Self-host is unchanged and is the control in <see cref="HostedOwnerSettingsSelfHostControlTests"/>.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedPerAccountSettingsServeTests : IAsyncLifetime
{
    private const string Token = "test-token-peraccount";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-peraccount-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;   // fully enrolled tenant A
    private HttpClient _httpB = null!;   // fully enrolled tenant B
    private HttpClient _httpUnbound = null!; // enrolled device, NO tenant binding

    public HostedPerAccountSettingsServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-peraccount-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com");
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com");

        // An enrolled device key that is NOT bound to any account: the strongest UNRESOLVED caller. It must
        // be refused, never served the Local partition. MTR-14B: under the DB-authoritative device registry
        // an unbound device on hosted is an invalid credential (invalidHostedBinding -> Revoked), so it is
        // refused at the auth gate with 401 - it never authenticates far enough to reach the per-account
        // route's own tenant-boundary 403. The isolation property is unchanged (no bound tenant -> no
        // access, no Local/cross-tenant read); only the denial layer moved (tenant gate -> credential gate).
        var unboundKey = _gateway.Devices.Register("dev-unbound", "MA").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
    }

    private HttpClient Enrolled(string deviceId, string subject, string email)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        _httpUnbound.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The per-account routes that now SERVE on hosted, with a well-formed body for the writes. Reused by the
    /// serve theory and the unresolved-tenant 403 theory so both drive the identical set.
    /// </summary>
    public static readonly (string Verb, string Path, string? Body)[] ServedRoutes =
    {
        ("GET", "gateway/settings",                 null),
        ("GET", "gateway/snooze-default",           null),
        ("PUT", "gateway/snooze-default",           "{\"minutes\":45}"),
        ("GET", "gateway/snooze-presets",           null),
        ("PUT", "gateway/snooze-presets",           "{\"presets\":[15,30,60],\"defaultMinutes\":30}"),
        ("GET", "gateway/time-zone",                null),
        ("PUT", "gateway/time-zone",                "{\"timeZone\":\"America/New_York\"}"),
        ("GET", "gateway/ai-provider",              null),
        ("PUT", "gateway/ai-provider",              "{\"provider\":\"devthrottle\"}"),
        ("GET", "gateway/tts-voice",                null),
        ("PUT", "gateway/tts-voice",                "{\"voice\":\"shimmer\"}"),
        ("GET", "gateway/daily-report",             null),
        ("PUT", "gateway/daily-report",             "{\"cadence\":\"off\"}"),
        ("GET", "gateway/mentor-report",            null),
        ("PUT", "gateway/mentor-report",            "{\"enabled\":false}"),
        ("GET", "gateway/fleet-manager",            null),
        ("PUT", "gateway/fleet-manager",            "{\"sessionId\":\"77777777-7777-7777-7777-777777777777\"}"),
        ("GET", "gateway/injected-text",            null),
        ("PUT", "gateway/injected-text",            "{\"use_yours\":true,\"yours\":\"words for one account only\"}"),
        // Issue #1360: these three setters REFUSE any id that is not a devthrottle/ included
        // model, and that refusal is the feature - so the ids here must be real included ones.
        // Taken from the product constants rather than written out, so a rename moves the test
        // with it instead of leaving another stale literal behind.
        ("PUT", "gateway/ai/wingman-model",         "{\"model\":\"" + TranscriptionEndpointResolver.DevThrottleWingmanModel + "\"}"),
        ("PUT", "gateway/ai/wingman-fast-model",    "{\"model\":\"" + TranscriptionEndpointResolver.DevThrottleWingmanFastModel + "\"}"),
        ("PUT", "gateway/ai/car-mode-model",        "{\"model\":\"" + TranscriptionEndpointResolver.DevThrottleWingmanModel + "\"}"),
        ("PUT", "gateway/ai/car-mode-end-phrase",   "{\"phrase\":\"alpha out\"}"),
        ("PUT", "gateway/ai/tts-model",             "{\"model\":\"m-speech\"}"),
    };

    public static TheoryData<string, string, string> AllServed
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (verb, path, body) in ServedRoutes) data.Add(verb, path, body ?? "");
            return data;
        }
    }

    /// <summary>
    /// SERVE: every per-account route answers 200 for an enrolled tenant on hosted - NOT the 404 refusal the
    /// deny used to give. This is the fact the whole deny retirement rests on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllServed))]
    public async Task Every_per_account_route_serves_an_enrolled_tenant_on_hosted(string verb, string path, string body)
    {
        var response = await OwnerSettingsRoutes.SendAsync(_httpA, verb, path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // And it is not the refusal envelope hiding behind a 200.
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not available on the hosted gateway", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAIL CLOSED: the SAME routes answer 403 for a device whose key resolves to NO tenant. Never a 200 with
    /// the Local partition's data, and never a 404 - a bound-but-unattributable caller is refused with the
    /// tenant-required 403, which is the whole reason these routes are safe to serve on shared infrastructure.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllServed))]
    public async Task Every_per_account_route_refuses_an_unresolved_tenant_with_401(string verb, string path, string body)
    {
        var response = await OwnerSettingsRoutes.SendAsync(_httpUnbound, verb, path, body);
        // MTR-14B: unbound-on-hosted is an invalid credential -> denied at the auth gate (401), before the
        // route's tenant-boundary 403. Refused either way; no Local/cross-tenant read (see setup comment).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// ISOLATED - the display time zone. Tenant A sets a zone; tenant B's read is UNCHANGED from its own
    /// baseline. Asserting B is unchanged (rather than "B != A's value") is machine-zone-independent: an
    /// unset tenant resolves to the Gateway machine's own zone, so a fixed literal could coincide with the
    /// runner's zone for a benign reason. The zone A writes is chosen to differ from B's baseline so A's
    /// write is a real change.
    /// </summary>
    [Fact]
    public async Task Time_zone_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        var bBaseline = await ReadString(_httpB, "gateway/time-zone", "timeZone");
        var aZone = bBaseline == "Asia/Tokyo" ? "Europe/Paris" : "Asia/Tokyo";

        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/time-zone", $"{{\"timeZone\":\"{aZone}\"}}"))
            .EnsureSuccessStatusCode();

        Assert.Equal(aZone, await ReadString(_httpA, "gateway/time-zone", "timeZone"));
        Assert.Equal(bBaseline, await ReadString(_httpB, "gateway/time-zone", "timeZone"));
    }

    /// <summary>ISOLATED - the snooze default. A writes 45; B still reads its own default.</summary>
    [Fact]
    public async Task Snooze_default_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/snooze-default", "{\"minutes\":45}"))
            .EnsureSuccessStatusCode();

        Assert.Equal(45, await ReadInt(_httpA, "gateway/snooze-default", "minutes"));
        Assert.NotEqual(45, await ReadInt(_httpB, "gateway/snooze-default", "minutes"));
    }

    /// <summary>
    /// ISOLATED - the daily report cadence (issue #1000). A turns its report off; B still gets one. This is
    /// the property that matters most on shared infrastructure: one account silencing its own mail must never
    /// silence anybody else's, and the account that never chose keeps the daily default.
    /// </summary>
    [Fact]
    public async Task Daily_report_turned_off_by_one_tenant_does_not_silence_another_on_hosted()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/daily-report", "{\"cadence\":\"off\"}"))
            .EnsureSuccessStatusCode();

        Assert.Equal("off", await ReadString(_httpA, "gateway/daily-report", "cadence"));
        Assert.Equal("daily", await ReadString(_httpB, "gateway/daily-report", "cadence"));
    }

    /// <summary>
    /// ISOLATED - the mentor report switch (devthrottle_internal#1661). A turns the mentor off; B still has
    /// it on. The same property that matters most for the daily report matters more here: what an account is
    /// turning off is a model reading that account's own prompts, so one person's opt-out leaking onto
    /// somebody else's row would silence a report nobody asked to stop - and leaking the other way would keep
    /// reading the prompts of somebody who asked us not to.
    /// </summary>
    [Fact]
    public async Task Mentor_report_turned_off_by_one_tenant_does_not_silence_another_on_hosted()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/mentor-report", "{\"enabled\":false}"))
            .EnsureSuccessStatusCode();

        Assert.False(await ReadBool(_httpA, "gateway/mentor-report", "enabled"));
        Assert.True(await ReadBool(_httpB, "gateway/mentor-report", "enabled"));
        // And the snapshot the Settings page actually renders agrees with the route it writes through.
        Assert.False(await ReadBool(_httpA, "gateway/settings", "mentorReportEnabled"));
        Assert.True(await ReadBool(_httpB, "gateway/settings", "mentorReportEnabled"));
    }

    /// <summary>
    /// A body with no "enabled" is REFUSED, not read as either answer. Guessing would answer "saved" for a
    /// choice nobody made, and one of the two guesses keeps reading the prompts of somebody who asked us to
    /// stop.
    /// </summary>
    [Fact]
    public async Task Mentor_report_write_without_a_value_is_refused_and_changes_nothing()
    {
        var before = await ReadBool(_httpA, "gateway/mentor-report", "enabled");

        var response = await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/mentor-report", "{}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ReadBool(_httpA, "gateway/mentor-report", "enabled"));
    }

    /// <summary>
    /// ISOLATED - the account's Fleet Manager mark. One account marks a session; the other still has none. The
    /// id goes in upper case and comes back in the canonical lower case every roster row carries, and a null id
    /// clears the mark.
    /// </summary>
    [Fact]
    public async Task Fleet_manager_marked_by_one_tenant_is_invisible_to_another_and_clears_with_null()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/fleet-manager",
            "{\"sessionId\":\"ABCDEFAB-1234-4321-ABCD-ABCDEFABCDEF\"}")).EnsureSuccessStatusCode();

        Assert.Equal("abcdefab-1234-4321-abcd-abcdefabcdef", await ReadString(_httpA, "gateway/fleet-manager", "sessionId"));
        Assert.Null(await ReadString(_httpB, "gateway/fleet-manager", "sessionId"));

        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/fleet-manager", "{\"sessionId\":null}"))
            .EnsureSuccessStatusCode();

        Assert.Null(await ReadString(_httpA, "gateway/fleet-manager", "sessionId"));
    }

    /// <summary>A body with no "sessionId", or one that is not a session id, is REFUSED and changes nothing -
    /// it is never read as a clear.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessionId\":\"fleet-manager\"}")]
    [InlineData("{\"sessionId\":42}")]
    public async Task Fleet_manager_write_without_a_session_id_is_refused_and_changes_nothing(string body)
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/fleet-manager",
            "{\"sessionId\":\"12121212-3434-5656-7878-909090909090\"}")).EnsureSuccessStatusCode();

        var response = await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/fleet-manager", body);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("12121212-3434-5656-7878-909090909090", await ReadString(_httpA, "gateway/fleet-manager", "sessionId"));
    }

    /// <summary>ISOLATED - the text-to-speech voice. A picks a distinctive voice; B is unaffected.</summary>
    [Fact]
    public async Task Tts_voice_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/tts-voice", "{\"voice\":\"alpha-only-voice\"}"))
            .EnsureSuccessStatusCode();

        Assert.Equal("alpha-only-voice", await ReadString(_httpA, "gateway/tts-voice", "voice"));
        Assert.NotEqual("alpha-only-voice", await ReadString(_httpB, "gateway/tts-voice", "voice"));
    }

    /// <summary>
    /// ISOLATED - the wingman thinking model, written through AiModelsEndpoint and read back through the
    /// ai-provider snapshot. Proves the un-denied model setter and the un-denied snapshot are both per tenant.
    /// </summary>
    [Fact]
    public async Task Wingman_model_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        // A real included id (issue #1360 - the setter refuses anything else), and deliberately NOT the
        // default one: tenant B is left on the default, so a value equal to it would make the isolation
        // assertion below pass no matter how leaky the store was.
        const string alphaModel = TranscriptionEndpointResolver.DevThrottleWingmanFastModel;

        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/ai/wingman-model", "{\"model\":\"" + alphaModel + "\"}"))
            .EnsureSuccessStatusCode();

        Assert.Equal(alphaModel, await ReadString(_httpA, "gateway/ai-provider", "wingmanModel"));
        Assert.NotEqual(alphaModel, await ReadString(_httpB, "gateway/ai-provider", "wingmanModel"));
    }

    /// <summary>ISOLATED - the Car Mode end phrase, written through AiModelsEndpoint, read via the snapshot.</summary>
    [Fact]
    public async Task Car_mode_end_phrase_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/ai/car-mode-end-phrase", "{\"phrase\":\"alpha signing off\"}"))
            .EnsureSuccessStatusCode();

        Assert.Equal("alpha signing off", await ReadString(_httpA, "gateway/ai-provider", "carModeEndPhrase"));
        Assert.NotEqual("alpha signing off", await ReadString(_httpB, "gateway/ai-provider", "carModeEndPhrase"));
    }

    /// <summary>
    /// ISOLATED - the injected agent-launch text (issue #2057). Tenant A turns on its own text; tenant B's
    /// read is unaffected - B still sees "use ours" and never A's words. This is the launch text each account's
    /// own Directors download from this same route, so per-tenant here is per-tenant at launch.
    /// </summary>
    [Fact]
    public async Task Injected_text_written_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        const string aWords = "alpha-only launch words zqxjv";
        (await OwnerSettingsRoutes.SendAsync(_httpA, "PUT", "gateway/injected-text",
            $"{{\"use_yours\":true,\"yours\":\"{aWords}\"}}")).EnsureSuccessStatusCode();

        using (var a = await ReadJson(_httpA, "gateway/injected-text"))
        {
            Assert.True(a.RootElement.GetProperty("use_yours").GetBoolean());
            Assert.Equal(aWords, a.RootElement.GetProperty("yours").GetString());
        }
        using (var b = await ReadJson(_httpB, "gateway/injected-text"))
        {
            Assert.False(b.RootElement.GetProperty("use_yours").GetBoolean());
            Assert.NotEqual(aWords, b.RootElement.GetProperty("yours").GetString());
        }
    }

    /// <summary>
    /// The ai-provider snapshot carries the Gateway-owned catalogAvailable flag, and on hosted it is FALSE -
    /// the AI tab reads this to disable model browsing/Test rather than call the denied catalog route.
    /// </summary>
    [Fact]
    public async Task Ai_provider_snapshot_marks_the_catalog_unavailable_on_hosted()
    {
        using var doc = await ReadJson(_httpA, "gateway/ai-provider");
        Assert.False(doc.RootElement.GetProperty("catalogAvailable").GetBoolean());
    }

    private static async Task<string?> ReadString(HttpClient http, string path, string property)
    {
        using var doc = await ReadJson(http, path);
        return doc.RootElement.GetProperty(property).GetString();
    }

    private static async Task<bool> ReadBool(HttpClient http, string path, string property)
    {
        using var doc = await ReadJson(http, path);
        return doc.RootElement.GetProperty(property).GetBoolean();
    }

    private static async Task<int> ReadInt(HttpClient http, string path, string property)
    {
        using var doc = await ReadJson(http, path);
        return doc.RootElement.GetProperty(property).GetInt32();
    }

    private static async Task<JsonDocument> ReadJson(HttpClient http, string path)
    {
        var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
