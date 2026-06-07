using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director-side home of the handshake truth (issues #223/#224): green is earned by a
/// completed nonce handshake only; NotConfigured is a legitimate gray, never red; a stale
/// LastVerifiedAt survives later failures so the UI can say "was verified until HH:mm".
/// </summary>
public sealed class GatewayConnectionMonitorTests
{
    [Fact]
    public void FreshMonitor_IsNotConfigured()
    {
        var m = new GatewayConnectionMonitor();
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);
        Assert.Null(m.LastVerifiedAt);
        Assert.Null(m.FailureSummary);
    }

    [Fact]
    public void Reset_Configured_GoesConnecting()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        Assert.Equal(GatewayConnectionStatus.Connecting, m.Status);
    }

    [Fact]
    public void Reset_NotConfigured_GoesGray_AndClearsHistory()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        var n = m.BeginHandshake();
        m.CompleteHandshake(n, new DirectorVerifyResultDto { Verified = true }, null);

        m.Reset(gatewayConfigured: false);
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);
        Assert.Null(m.LastVerifiedAt); // a verification against the old gateway says nothing now
        Assert.Null(m.LastResult);
    }

    [Fact]
    public void RecordCallback_KnownNonce_True_UnknownNonce_False()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        var nonce = m.BeginHandshake();

        Assert.True(m.RecordCallback(nonce));
        Assert.True(m.CallbackReceived(nonce));
        Assert.False(m.RecordCallback("never-issued"));
        Assert.False(m.RecordCallback(""));
    }

    [Fact]
    public void CompleteHandshake_Pass_GoesVerified_AndStamps()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        var nonce = m.BeginHandshake();
        var result = new DirectorVerifyResultDto { Verified = true, Nonce = nonce };

        m.CompleteHandshake(nonce, result, failureSummary: null);

        Assert.Equal(GatewayConnectionStatus.Verified, m.Status);
        Assert.NotNull(m.LastVerifiedAt);
        Assert.Null(m.FailureSummary);
        Assert.Same(result, m.LastResult);
        Assert.False(m.CallbackReceived(nonce)); // nonce retired
    }

    [Fact]
    public void CompleteHandshake_Fail_GoesFailed_KeepsOlderVerifiedStamp()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);

        var n1 = m.BeginHandshake();
        m.CompleteHandshake(n1, new DirectorVerifyResultDto { Verified = true }, null);
        var verifiedAt = m.LastVerifiedAt;
        Assert.NotNull(verifiedAt);

        var n2 = m.BeginHandshake();
        m.CompleteHandshake(n2, new DirectorVerifyResultDto { Verified = false }, "The Gateway cannot call this Director back: TCP timeout");

        Assert.Equal(GatewayConnectionStatus.Failed, m.Status);
        Assert.Contains("cannot call this Director back", m.FailureSummary);
        Assert.Equal(verifiedAt, m.LastVerifiedAt); // "was verified until HH:mm" survives
    }

    [Fact]
    public void ReportRegistrationFailure_GoesFailed_ButNeverFromNotConfigured()
    {
        var m = new GatewayConnectionMonitor();

        // Gray is sticky: a local-only Director must never show red.
        m.ReportRegistrationFailure("anything");
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);

        m.Reset(gatewayConfigured: true);
        m.ReportRegistrationFailure("Cannot reach the Gateway at http://gw:7878: connection refused");
        Assert.Equal(GatewayConnectionStatus.Failed, m.Status);
        Assert.Contains("connection refused", m.FailureSummary);
    }

    [Fact]
    public void Changed_FiresOnTransitions_NotOnRepeatedIdenticalFailure()
    {
        var m = new GatewayConnectionMonitor();
        var fired = 0;
        m.Changed += () => fired++;

        m.Reset(gatewayConfigured: true);              // 1
        m.ReportRegistrationFailure("reason A");        // 2
        m.ReportRegistrationFailure("reason A");        // suppressed (no churn)
        m.ReportRegistrationFailure("reason B");        // 3
        var n = m.BeginHandshake();                     // no event (no state change yet)
        m.CompleteHandshake(n, null, null);             // 4

        Assert.Equal(4, fired);
    }

    [Fact]
    public void AbandonHandshake_RetiresNonce_WithoutStateFlip()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        var nonce = m.BeginHandshake();

        m.AbandonHandshake(nonce);

        Assert.Equal(GatewayConnectionStatus.Connecting, m.Status);
        Assert.False(m.RecordCallback(nonce)); // no longer pending
    }
}
