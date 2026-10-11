using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1923 - CROSS-TENANT STREAM INJECTION. The Director-facing StreamUp method proved WHO the caller was
/// (the Hello binding) but never that the caller OWNED the stream it named, and the stream registry recorded
/// no owner at all: its entries were keyed on the bare stream id. Any authenticated account that learned or
/// guessed another account's live stream id could therefore WRITE frames into that account's terminal, file,
/// or screenshot sink, claim the stream before the real Director, or tear it down. Note the direction: this is
/// INJECTION (a write into what another customer sees), not disclosure.
///
/// The fix authorizes rather than denies: the stream is tenant-attributable, so the entry now carries its
/// owner (the tenant whose request opened it, plus the Director the open command was sent to) and every
/// StreamUp is checked against it and REFUSED on a mismatch.
///
/// These tests are built to the proof bar, and the bar's two hardest items are the reason for their shape:
///  - "Absence proved twice, presence never" is a reject. A refusal assertion is worthless on its own - a
///    no-op stream would satisfy it perfectly. So every refusal here is BRACKETED by a permitted write on the
///    SAME registry that genuinely lands frames on the sink: a positive control BEFORE the attempt (the
///    mechanism works) and a destructibility control AFTER it (the very sink that "received nothing" was
///    alive and writable the whole time - the guard is what stopped the write, not a dead stream).
///  - The hosted/self-host mode is SET here and asserted (<see cref="HostedTenantBoundary.IsHosted"/>), never
///    inherited from whatever the test runner happened to default to.
/// </summary>
public sealed class StreamOwnershipTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"strown-{Guid.NewGuid():N}");

    public StreamOwnershipTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ---------------------------------------------------------------- THE cross-tenant proof ----

    [Fact]
    public async Task Hosted_TenantB_StreamingIntoTenantAsStream_IsRefused_AndAsSinkReceivesNothing()
    {
        // HOSTED MODE IS SET HERE, NOT INHERITED: the ambient (async-local) tenant context is what makes the
        // boundary hosted, so construct it explicitly and assert the mode actually took effect before relying
        // on anything below. A boundary that silently came up self-host would make every account Local and
        // this whole test would prove nothing.
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: false);
        Assert.True(boundary.IsHosted);

        // Two accounts, two tenants, two authenticated device keys - exactly the production enrollment shape.
        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());
        Assert.NotEqual(tenantA.Value, tenantB.Value);
        var keyA = devices.Register("dev-a", "MA").DeviceKey;
        var keyB = devices.Register("dev-b", "MB").DeviceKey;
        devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        // ONE Gateway, ONE stream registry (the production singleton), two Directors bound to different
        // tenants. Both Hellos must bind, or the "refusal" below could just be an unbound connection.
        var store = new PushedSessionStore(() => DateTime.UtcNow);
        var registry = new GatewayStreamRegistry();
        var hubA = NewHub("conn-a", keyA, boundary, store, registry);
        var hubB = NewHub("conn-b", keyB, boundary, store, registry);
        hubA.Hello(new DirectorStreamHello { DirectorId = "dir-a", Version = "t" });
        hubB.Hello(new DirectorStreamHello { DirectorId = "dir-b", Version = "t" });
        Assert.Equal(new[] { "sess-a" }, PushAndRead(hubA, store, tenantA, "sess-a"));
        Assert.Equal(new[] { "sess-b" }, PushAndRead(hubB, store, tenantB, "sess-b"));

        // ---- POSITIVE CONTROL, BEFORE the attempt: the permitted path genuinely delivers frames. ----
        // Without this, a refusal assertion could pass on a registry that never delivers anything at all.
        var controlSink = new RecordingSink();
        registry.Register("stream-a-control", new StreamOwner(tenantA, "dir-a"), controlSink);
        await hubA.StreamUp("stream-a-control", Frames("stream-a-control", 0xA1));
        AssertFramesArrived(controlSink, "stream-a-control", 0xA1);

        // ---- The attack: tenant B streams up under a stream id minted for tenant A. ----
        var victimSink = new RecordingSink();
        registry.Register("stream-a", new StreamOwner(tenantA, "dir-a"), victimSink);

        var denied = await Assert.ThrowsAsync<HubException>(
            () => hubB.StreamUp("stream-a", Frames("stream-a", 0xBB)));
        // A REFUSAL, not a silent drop: the caller is told, so this can never be mistaken in a log for the
        // legitimate "the browser already left" no-op.
        Assert.Contains("stream-a", denied.Message);

        // Nothing of B's reached A's sink...
        Assert.Empty(victimSink.Frames);
        // ...and B could not deny A service either: the stream was neither claimed nor torn down.
        Assert.False(victimSink.Completed);
        Assert.Equal(1, registry.LiveStreamCount);

        // ---- DESTRUCTIBILITY CONTROL, AFTER the attempt: that same sink IS writable. ----
        // This is what makes "victimSink.Frames is empty" mean something. The owner now streams into the very
        // stream B was refused, and the frames land - so the emptiness above was the ownership check, not a
        // stream that was dead, closed, or incapable of receiving anything.
        await hubA.StreamUp("stream-a", Frames("stream-a", 0xA2));
        AssertFramesArrived(victimSink, "stream-a", 0xA2);
        // And still nothing of B's is in there - the refused frames were never buffered and replayed.
        Assert.DoesNotContain(victimSink.Frames, f => f.Data is { Length: > 0 } d && d[0] == 0xBB);
    }

    [Fact]
    public async Task Hosted_TwoTenantsUsingTheSameDirectorId_CrossTenantStreamUp_IsRefused()
    {
        // THIS is the test that makes the TENANT half of the owner load-bearing, and it exists because the
        // test above cannot do that job. There the owner was (tenantA, "dir-a") and the attacker was
        // (tenantB, "dir-b") - BOTH halves differ, so the Director comparison alone refuses it and the test
        // stays green even if tenant equality is deleted outright. The property that JUSTIFIES the composite
        // key was the one property with no failing test.
        //
        // So: both accounts run a Director calling itself THE SAME THING. A Director id is chosen by the
        // client in its Hello payload, so an attacker picks it freely - this is not a coincidence to engineer,
        // it is the obvious move. With the ids equal, tenant equality is the ONLY thing left that can refuse,
        // which is exactly what separates this design from the single-key one it replaced.
        const string sharedDirectorId = "dir-shared";

        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: false);
        Assert.True(boundary.IsHosted);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());
        Assert.NotEqual(tenantA.Value, tenantB.Value);
        var keyA = devices.Register("dev-a", "MA").DeviceKey;
        var keyB = devices.Register("dev-b", "MB").DeviceKey;
        devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        var store = new PushedSessionStore(() => DateTime.UtcNow);
        var registry = new GatewayStreamRegistry();
        var hubA = NewHub("conn-a", keyA, boundary, store, registry);
        var hubB = NewHub("conn-b", keyB, boundary, store, registry);

        // Both Hellos declare the SAME Director id and BOTH must bind - the id is not unique across accounts,
        // and if B's bind were rejected the refusal below would prove nothing about ownership.
        hubA.Hello(new DirectorStreamHello { DirectorId = sharedDirectorId, Version = "t" });
        hubB.Hello(new DirectorStreamHello { DirectorId = sharedDirectorId, Version = "t" });
        Assert.Equal(new[] { "sess-a" }, PushAndRead(hubA, store, tenantA, "sess-a"));
        Assert.Equal(new[] { "sess-b" }, PushAndRead(hubB, store, tenantB, "sess-b"));

        // ---- POSITIVE CONTROL: tenant A's own write to its own stream lands. ----
        var sink = new RecordingSink();
        registry.Register("shared-id-stream", new StreamOwner(tenantA, sharedDirectorId), sink);
        await hubA.StreamUp("shared-id-stream", Frames("shared-id-stream", 0xC1));
        AssertFramesArrived(sink, "shared-id-stream", 0xC1);

        // ---- THE ATTACK: a DIFFERENT ACCOUNT whose Director carries the SAME id. ----
        // The Director halves are equal here, so only tenant equality can refuse this.
        var victimSink = new RecordingSink();
        registry.Register("shared-id-victim", new StreamOwner(tenantA, sharedDirectorId), victimSink);

        var denied = await Assert.ThrowsAsync<HubException>(
            () => hubB.StreamUp("shared-id-victim", Frames("shared-id-victim", 0xCB)));
        Assert.Contains("shared-id-victim", denied.Message);
        Assert.Empty(victimSink.Frames);
        Assert.False(victimSink.Completed);
        Assert.Equal(1, registry.LiveStreamCount);

        // ---- DESTRUCTIBILITY CONTROL: the owner writes to THAT SAME sink and it lands. ----
        await hubA.StreamUp("shared-id-victim", Frames("shared-id-victim", 0xC2));
        AssertFramesArrived(victimSink, "shared-id-victim", 0xC2);
        Assert.DoesNotContain(victimSink.Frames, f => f.Data is { Length: > 0 } d && d[0] == 0xCB);
    }

    // ------------------------------------------------------------------- self-host is unchanged ----

    [Fact]
    public async Task SelfHost_TheSingleTenantsDirector_StreamsUpUnchanged()
    {
        // SELF-HOST MODE IS SET HERE, NOT INHERITED: the single-tenant context makes the boundary inert.
        // Assert the mode took effect - this is the control that says the fix costs the self-host install
        // nothing, and it is worthless if the boundary silently came up hosted.
        var devices = _harness.OpenDevices();
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), devices, hosted: false);
        Assert.False(boundary.IsHosted);

        var store = new PushedSessionStore(() => DateTime.UtcNow);
        var registry = new GatewayStreamRegistry();
        var hub = NewHub("conn-local", deviceKey: "any-key-self-host-does-not-check", boundary, store, registry);
        hub.Hello(new DirectorStreamHello { DirectorId = "dir-1", Version = "t" });

        var sink = new RecordingSink();
        registry.Register("s-local", new StreamOwner(TenantId.Local, "dir-1"), sink);
        await hub.StreamUp("s-local", Frames("s-local", 0x5A));

        AssertFramesArrived(sink, "s-local", 0x5A);
        Assert.Equal(0, registry.LiveStreamCount); // torn down on the Closed frame, exactly as before
    }

    // ------------------------------------------------------- registry-level ownership behaviour ----

    [Fact]
    public async Task SameTenant_DifferentDirector_IsAlsoRefused()
    {
        // The owner is the PAIR. One account can run many Directors, and one of its Directors has no business
        // writing into a stream opened on another - the id is still not a capability.
        //
        // This is the DIRECTOR-HALF detector, the twin of Hosted_TwoTenantsUsingTheSameDirectorId_...: the
        // tenants are equal here, so only the Director comparison can refuse. Between the two, each half of the
        // composite key has a test that fails when THAT half alone is deleted, which is what lets three
        // separate one-primitive mutation runs attribute a red to the clause that caused it.
        var tenant = new TenantId(Guid.NewGuid().ToString());
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("s", new StreamOwner(tenant, "dir-owner"), sink);

        await Assert.ThrowsAsync<StreamOwnershipDeniedException>(
            () => registry.ConsumeAsync("s", new StreamOwner(tenant, "dir-other"), Frames("s", 0x11), CancellationToken.None));
        Assert.Empty(sink.Frames);

        // Destructibility control: the rightful Director's write DOES land on that same sink.
        await registry.ConsumeAsync("s", new StreamOwner(tenant, "dir-owner"), Frames("s", 0x22), CancellationToken.None);
        AssertFramesArrived(sink, "s", 0x22);
    }

    [Fact]
    public async Task SameOwner_DirectorIdCaseDiffers_IsStillTheOwner()
    {
        // Director ids are compared case-insensitively everywhere else on this connection (Hello's re-claim
        // check), so a case difference must not turn the rightful owner into an intruder.
        var tenant = new TenantId(Guid.NewGuid().ToString());
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("s", new StreamOwner(tenant, "Dir-Owner"), sink);

        await registry.ConsumeAsync("s", new StreamOwner(tenant, "dir-owner"), Frames("s", 0x33), CancellationToken.None);
        AssertFramesArrived(sink, "s", 0x33);
    }

    [Fact]
    public async Task UnknownStreamId_IsStillTheSilentNoOp_NotARefusal()
    {
        // The two outcomes MUST stay distinguishable. An unknown id is the legitimate lifecycle race (the
        // browser left before the frames arrived) and stays a quiet no-op; a KNOWN id with the wrong owner is
        // an authorization failure and throws. Collapsing them is exactly what would hide an injection attempt.
        var registry = new GatewayStreamRegistry();
        var owner = new StreamOwner(TenantId.Local, "dir-1");
        var sink = new RecordingSink();
        registry.Register("gone", owner, sink);
        registry.Close("gone");

        await registry.ConsumeAsync("gone", owner, Frames("gone", 0x44), CancellationToken.None);
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void Register_WithoutAnOwner_FailsLoud()
    {
        // A stream whose owner is unknown must never be opened - recording a blank owner would authorize
        // nobody at best and anybody at worst.
        var registry = new GatewayStreamRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register("s", default, new RecordingSink()));
        Assert.Throws<ArgumentException>(() => registry.Register("s", new StreamOwner(TenantId.Local, ""), new RecordingSink()));
        Assert.Equal(0, registry.LiveStreamCount);
    }

    // ------------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// Assert the frames actually ARRIVED, checking the FORMAT FACTS before reading anything out of them: the
    /// count, then each frame's kind, then the marker byte. A bare "the list is not empty" would pass on
    /// frames that carried none of this payload.
    /// </summary>
    private static void AssertFramesArrived(RecordingSink sink, string streamId, byte marker)
    {
        Assert.Equal(2, sink.Frames.Count);
        Assert.Equal(DirectorStreamFrameType.Binary, sink.Frames[0].Kind);
        Assert.Equal(streamId, sink.Frames[0].StreamId);
        Assert.NotNull(sink.Frames[0].Data);
        Assert.Single(sink.Frames[0].Data!);
        Assert.Equal(marker, sink.Frames[0].Data![0]);
        Assert.Equal(DirectorStreamFrameType.Closed, sink.Frames[1].Kind);
        Assert.True(sink.Completed);
    }

    private static async IAsyncEnumerable<DirectorStreamFrame> Frames(
        string streamId, byte marker, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Binary, Data = new[] { marker } };
        await Task.Yield();
        yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Closed, Reason = "eof" };
    }

    private static string[] PushAndRead(DirectorHub hub, PushedSessionStore store, TenantId tenant, string sessionId)
    {
        hub.PushDelta(1, new SessionDto { SessionId = sessionId });
        return store.SnapshotFresh(tenant, TimeSpan.FromMinutes(5)).Select(x => x.Session.SessionId).OrderBy(s => s).ToArray();
    }

    private DirectorHub NewHub(string connId, string deviceKey, HostedTenantBoundary boundary,
        PushedSessionStore store, GatewayStreamRegistry streams)
    {
        var http = new DefaultHttpContext();
        var tenant = boundary.ResolveForDeviceKey(deviceKey);
        if (tenant is not null)
        {
            http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
                "test-device",
                tenant.Value.Value,
                DeviceRegistry.DefaultDeviceType,
                DeviceRegistry.StatusActive);
        }
        var directors = new DirectorRegistry(_tempDir);
        var inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, $"stats-{Guid.NewGuid():N}.db"));
        return new DirectorHub(store, directors, InputStatsHandle.Available(inputStats), streams, tenantBoundary: boundary)
        {
            Context = new FakeHubCtx(connId, http),
        };
    }

    private sealed class RecordingSink : IStreamSink
    {
        public List<DirectorStreamFrame> Frames { get; } = new();
        public bool Completed { get; private set; }

        public Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string? reason)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubCtx : HubCallerContext
    {
        public FakeHubCtx(string connectionId, HttpContext http)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeatureImpl { HttpContext = http });
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
