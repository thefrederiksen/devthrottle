using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole <c>/exes</c> developer/launcher surface is DENIED on the hosted Gateway - the host-process
/// roster read, the slot-executable delete, and the PowerShell build + process launch alike.
///
/// The surface is a machine-local control plane for the Gateway's OWN host box and carries NO tenant:
/// GET /exes/list substitutes <see cref="CcDirector.Core.Tenancy.TenantId.Local"/> and enumerates the OS
/// processes running on the shared host; DELETE /exes/slots/{n} deletes a process-global slot executable off
/// local disk; POST /exes/slots/{n}/build-start shells out to a PowerShell build and LAUNCHES a process on the
/// shared host. Behind only the host-wide authentication gate - which admits ANY enrolled device key from ANY
/// account - one authenticated tenant could read the host's process roster, delete a slot another tenant's
/// build expects, or launch a shared process on the host. OS-gating is not tenant isolation, so on a Windows
/// hosted deployment the whole surface was a tenant-blind host-control plane; that is why the whole group
/// closes and not only the value-returning read.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE. The group is denied through
/// <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family on this
/// Gateway adopts (the recording-ingest group in <see cref="RecordingEndpoints"/> is the reference adoption).
/// On hosted the handlers are NEVER MAPPED - one verb-less catch-all refusal claims everything under
/// <c>/exes</c> (plus a root refusal at the prefix itself), so every request shape meets the refusal: a valid
/// body, a malformed body, a wrong media type, a verb the group never mapped, and a route added LATER. Off
/// hosted the primitive maps the real handlers exactly as an unguarded builder would and creates no refusal.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
/// directly, never an optional argument that would fail OPEN the moment a caller omits it.
///
/// STATUS AND MEDIA TYPE ARE ASSERTED BEFORE ANY PARSE, so a revert reddens as a STATEMENT - "expected
/// NotFound, got OK/BadRequest" - rather than as a parser exception on a non-JSON body. The build-start probe
/// uses an OUT-OF-RANGE slot on purpose: on a revert it reaches the handler and returns a 400 "slot must be
/// 1-4" without ever shelling out to a real build or launching a process, so the reproduction is safe to run.
///
/// REVERT-PROOF - the recipe to RUN, not to describe. In <c>src/CcDirector.Gateway/Api/ExesEndpoints.cs</c>
/// change <c>HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial())</c> so the family maps its real handlers
/// on hosted too (map the routes on a plain <c>outer.MapGroup(Prefix)</c> instead). Rebuild, CONFIRM ZERO
/// ERRORS, then run this file: <see cref="Every_exes_route_is_refused_to_an_enrolled_tenant"/> flips to
/// "expected NotFound, got OK/BadRequest" as each handler answers, and
/// <see cref="The_refused_list_did_not_name_the_host"/> reddens because the host roster comes back.
/// </summary>
public sealed class HostedExesDenyTests : IDisposable
{
    internal const string RefusalMessage = ExesEndpoints.RefusalMessage;

    private readonly string? _priorHosted;
    private readonly string _instances =
        Path.Combine(Path.GetTempPath(), "cc-hosted-exes-" + Guid.NewGuid().ToString("N"));

    public HostedExesDenyTests()
    {
        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself and
        // proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every production route in the group, in one theory rather than one test each. Every verb is here,
    /// because the delete and the build-start are the host-control half of this defect and a deny that closed
    /// only the read would leave the damage path open. The build-start uses slot 9 (out of range) so a revert
    /// reaches the handler's 400 without kicking off a real build.
    /// </summary>
    [Theory]
    [InlineData("GET", "exes/list", null)]                              // host process roster (TenantId.Local)
    [InlineData("DELETE", "exes/slots/2", null)]                        // delete a process-global slot exe
    [InlineData("POST", "exes/slots/9/build-start", null)]             // build + launch a shared process
    [InlineData("GET", "exes/slots/9", null)]                          // a verb the route never mapped
    [InlineData("GET", "exes/anything-added-later", null)]            // a path that does not exist today
    public async Task Every_exes_route_is_refused_to_an_enrolled_tenant(string method, string path, string? body)
    {
        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: true);
        try
        {
            var resp = await Send(http, new HttpMethod(method), path, body);
            await AssertBodyIsNothingButTheRefusal(resp);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task The_refused_list_did_not_name_the_host()
    {
        // Refuse, never serve the host roster: GET /exes/list normally returns the box's machine name and its
        // running Directors. On hosted the exact-property assertion proves there is no machineName field at all.
        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: true);
        try
        {
            var resp = await Send(http, HttpMethod.Get, "exes/list");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.DoesNotContain("machineName", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await AssertBodyIsNothingButTheRefusal(resp);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_verb_the_group_never_mapped_is_also_refused_on_hosted()
    {
        // The primitive maps a VERB-LESS refusal, so a method the family never mapped meets the refusal too -
        // it does not leak the route's existence through a 405. /exes/list is a GET-only route; a DELETE on it
        // was never mapped by any verb, yet the catch-all refuses it.
        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: true);
        try
        {
            var resp = await Send(http, HttpMethod.Delete, "exes/list");
            await AssertBodyIsNothingButTheRefusal(resp);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and FORMAT FACTS BEFORE PARSING. Asserting the property set is EXACTLY
    /// one error field inverts a rotting deny-list: anything extra, anything new, anything that leaked reddens
    /// automatically. The status and media type are asserted FIRST so a revert reddens as a STATEMENT -
    /// "expected NotFound, got OK" - rather than as a parser exception on a non-JSON body.
    /// </summary>
    internal static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(req);
    }
}

/// <summary>
/// Boots ONLY the exe/slot group on an ephemeral port and hands the caller the denied group handle back so a
/// test can map a brand-new route through it. That is what makes the future-route proof possible: the group is
/// created inside <see cref="ExesEndpoints.Map"/>, so nothing outside that method could otherwise state a
/// property about routes added to it. Routes handed to <paramref name="mapIntoGroup"/> are RELATIVE to the
/// <c>/exes</c> prefix, the same way the production routes are.
/// </summary>
internal static class ExesGroupProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        string instancesDir,
        bool hosted,
        Action<HostedDenyGroup>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var registry = new DirectorRegistry(instancesDir);
        app.Lifetime.ApplicationStopped.Register(registry.Dispose);
        var group = ExesEndpoints.Map(app, hosted, registry, new PushedSessionStore());
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    public static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(HostedExesDenyTests.RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal covers routes that have not been written yet.
///
/// A guard line repeated in every handler passes exactly the same tests as an exclusive-prefix deny for the
/// routes that exist today - the difference only shows up on the route somebody adds NEXT, when it is open by
/// default and nothing fails. So this maps a BRAND-NEW route through the group and asserts it is already
/// refused with no deny of its own written anywhere. The mirror half - the same probe path SERVED with hosted
/// mode off - is <see cref="SelfHostExesGroupControlTests.A_route_added_to_the_group_still_serves_on_self_host"/>:
/// one direction alone cannot tell a working gate from a brick that refuses everything unconditionally.
/// </summary>
public sealed class HostedExesGroupFilterTests : IDisposable
{
    private readonly string? _priorHosted;
    private readonly string _instances =
        Path.Combine(Path.GetTempPath(), "cc-exes-group-" + Guid.NewGuid().ToString("N"));

    public HostedExesGroupFilterTests()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in
    /// <see cref="ExesEndpoints"/> mentions this path, and no guard is written for it here - the exclusive
    /// catch-all under <c>/exes</c> is the only thing standing between the caller and the handler. The future
    /// route is a FRAMEWORK body-bound POST (not a parameterless GET), because a parameterless probe is exactly
    /// the request shape the pre-binding defect is invisible through; the framework-400 and framework-415
    /// controls fire the same malformed body / wrong media type at an UNDENIED equivalent mapped outside
    /// <c>/exes</c>, proving the denied route short-circuits BEFORE binding and BEFORE endpoint selection.
    /// </summary>
    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own()
    {
        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: true,
            mapIntoGroup: group =>
                group.MapPost("/added-after-the-deny-was-written",
                    (ExesProbeBody body) => Results.Json(new { echoed = body.Text })),
            mapOutsideGroup: routes =>
                routes.MapPost("/undenied-equivalent",
                    (ExesProbeBody body) => Results.Json(new { echoed = body.Text })));
        try
        {
            foreach (var resp in new[]
                     {
                         await http.PostAsync("/exes/added-after-the-deny-was-written",
                             new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json")),
                         await http.PostAsync("/exes/added-after-the-deny-was-written",
                             new StringContent("{ not json", Encoding.UTF8, "application/json")),
                         await http.PostAsync("/exes/added-after-the-deny-was-written",
                             new StringContent("hello", Encoding.UTF8, "text/plain")),
                         await http.GetAsync("/exes/added-after-the-deny-was-written"),
                     })
            {
                await ExesGroupProbeHost.AssertBodyIsNothingButTheRefusal(resp);
                Assert.DoesNotContain("echoed", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // THE FRAMEWORK-400 CONTROL. The identical malformed body meets the framework's own 400 on the
            // undenied equivalent, while the denied route returns the refusal above - so the denied route
            // short-circuits BEFORE framework binding.
            var undeniedMalformed = await http.PostAsync("/undenied-equivalent",
                new StringContent("{ not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, undeniedMalformed.StatusCode);

            // THE FRAMEWORK-415 CONTROL. A wrong media type reaches the framework's own 415 on the undenied
            // equivalent - the shape a per-handler guard answering after model binding could never intercept.
            var undeniedWrongMedia = await http.PostAsync("/undenied-equivalent",
                new StringContent("hello", Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, undeniedWrongMedia.StatusCode);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// CONTROL: the deny is scoped to the <c>/exes</c> prefix, not a blanket refusal on the whole application.
    /// A route mapped OUTSIDE the group still serves on hosted, so the passing tests above are the deny doing
    /// its job and not the host refusing everything.
    /// </summary>
    [Fact]
    public async Task A_route_outside_the_group_still_serves_on_hosted()
    {
        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: true,
            mapOutsideGroup: routes => routes.MapGet("/not-an-exes-route", () => Results.Json(new { ok = true })));
        try
        {
            var resp = await http.GetAsync("/not-an-exes-route");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL ON THE GROUP, in BOTH non-hosted forms, with the effects proven.
///
/// Self-host is the control for this whole mission, so it is PROVEN rather than INHERITED. This class sets
/// <c>CC_GATEWAY_HOSTED</c> itself, to both non-hosted values that occur in practice - absent, and
/// present-but-not-"1" - and asserts the mode took before driving anything. It asserts REAL PAYLOADS, not the
/// absence of the refusal string: an empty-but-successful response would satisfy "the refusal is absent" while
/// still being a broken self-host. Every test here must stay GREEN through the revert described on
/// <see cref="HostedExesDenyTests"/>.
/// </summary>
public sealed class SelfHostExesGroupControlTests : IDisposable
{
    private readonly string? _priorHosted;
    private readonly string _instances =
        Path.Combine(Path.GetTempPath(), "cc-exes-selfhost-" + Guid.NewGuid().ToString("N"));

    public SelfHostExesGroupControlTests()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>Puts the process into a STATED non-hosted mode and proves it took.</summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    /// <summary>null = the variable is absent. "0" = present and explicitly not hosted. Both must serve.</summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    /// <summary>
    /// HANDLER-POSITIVE RECEIPT for the list route: the route really exists and really answers with the host's
    /// machine name. A 404 deny is indistinguishable from a route that was never mapped, so without a receipt
    /// like this the hosted 404 would prove nothing about a guard.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_still_reads_the_host_roster_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: false);
        try
        {
            var resp = await http.GetAsync("/exes/list");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(Environment.MachineName, doc.RootElement.GetProperty("machineName").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// HANDLER-POSITIVE RECEIPT for the slot delete route: the route really exists and really handles the
    /// request (a validation 400 for an out-of-range slot proves the handler ran, not the refusal). This is
    /// the route that DELETES a process-global executable on hosted, so its presence off hosted is the control.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_reaches_the_slot_delete_handler_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: false);
        try
        {
            var resp = await http.DeleteAsync("/exes/slots/9");
            // The handler ran and validated the slot range - NOT the hosted refusal.
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("slot must be 1-4", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// The self-host mirror of the future-route probe: the SAME framework body-bound POST route mapped through
    /// the group SERVES on self-host, in both non-hosted forms. Paired with
    /// <see cref="HostedExesGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own"/>,
    /// this proves the group is a working gate (refuse on hosted, serve off it) and not a brick that refuses
    /// everything unconditionally. It also proves the binding is REALLY the framework's: a valid body serves
    /// bound, and a malformed body off hosted hits the framework's own 400 - the capability the hosted refusal
    /// short-circuits before reaching.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await ExesGroupProbeHost.StartAsync(_instances, hosted: false,
            mapIntoGroup: group =>
                group.MapPost("/added-after-the-deny-was-written",
                    (ExesProbeBody body) => Results.Json(new { echoed = body.Text })));
        try
        {
            var served = await http.PostAsync("/exes/added-after-the-deny-was-written",
                new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            using (var doc = JsonDocument.Parse(await served.Content.ReadAsStringAsync()))
                Assert.Equal("hello", doc.RootElement.GetProperty("echoed").GetString());

            var malformed = await http.PostAsync("/exes/added-after-the-deny-was-written",
                new StringContent("{ not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// The future route's body record, bound by the FRAMEWORK on a minimal-API handler, exactly as the
/// recording family's <see cref="RecordingProbeBody"/> binds its body. This is what makes the future-route
/// canary a real framework-binding proof: off hosted a malformed body posted to this route reaches the
/// framework's JSON-binding 400, and on hosted the exclusive catch-all refuses BEFORE that binding.
/// </summary>
internal sealed record ExesProbeBody(string Text);
