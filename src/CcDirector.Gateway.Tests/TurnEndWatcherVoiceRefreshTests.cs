using CcDirector.Gateway.Briefing;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// Issue #549: the always-on turn-brief stamping machine (GatewayTurnBriefAgent) is retired.
// The TurnEndWatcher stays and runs unconditionally; its only job now is firing voice
// auto-refresh for VOICE sessions on turn-end and clearing the stale voice/text cache on the
// Working transition - WITHOUT any brief agent in the loop.
//
// GatewayHost wires the watcher like this (the lambdas under test here mirror it exactly):
//   onTurnEnd:       if voiceService.IsVoiceSession(sid)  -> voiceService.GenerateAsync(sid, ep)
//   onSessionWorking: voiceService.OnSessionWorking(sid)  (clear the stale cache)
//
// These tests prove that wiring with a recording fake voice surface, so the regression "voice
// auto-refresh dies when the brief pipeline is removed naively" can never come back.
// ============================================================================
public sealed class TurnEndWatcherVoiceRefreshTests
{
    /// <summary>A recording stand-in for the voice surface the host wires onto the watcher -
    /// the exact two calls GatewayHost makes (IsVoiceSession / GenerateAsync / OnSessionWorking).
    /// No brain, no brief agent, no HTTP - this is the wiring contract, isolated.</summary>
    private sealed class RecordingVoice
    {
        private readonly HashSet<string> _voiceSessions;
        public readonly List<(string Sid, string Endpoint)> Generated = new();
        public readonly List<string> Cleared = new();

        public RecordingVoice(params string[] voiceSessions) => _voiceSessions = new(voiceSessions);

        public bool IsVoiceSession(string sid) => _voiceSessions.Contains(sid);
        public void GenerateAsync(string sid, string endpoint) => Generated.Add((sid, endpoint));
        public void OnSessionWorking(string sid) => Cleared.Add(sid);
    }

    private static TurnEndWatcher BuildWatcher(RecordingVoice voice)
    {
        var registry = new CcDirector.Gateway.Discovery.DirectorRegistry(
            Path.Combine(Path.GetTempPath(), "tew-voice-tests", Guid.NewGuid().ToString("N")));
        var client = new CcDirector.Gateway.Discovery.DirectorEndpointClient("test-token");
        // The exact wiring GatewayHost installs - voice auto-refresh only, no brief agent.
        return new TurnEndWatcher(
            registry, client,
            onTurnEnd: signal =>
            {
                if (voice.IsVoiceSession(signal.SessionId))
                    voice.GenerateAsync(signal.SessionId, signal.DirectorEndpoint);
            },
            onSessionWorking: sid => voice.OnSessionWorking(sid));
    }

    [Fact]
    public void HandsOffTurnEnd_OnAVoiceSession_FiresVoiceAutoRefresh_WithNoBriefAgent()
    {
        var voice = new RecordingVoice("voice-sid");
        using var watcher = BuildWatcher(voice);

        // A turn runs and then finishes on its own (Working -> WaitingForInput) - the hands-off
        // boundary. The watcher must call voice GenerateAsync for the session, with the owning
        // Director endpoint, and nothing else.
        watcher.Observe("voice-sid", "Working", "http://d1");
        watcher.Observe("voice-sid", "WaitingForInput", "http://d1");

        var generated = Assert.Single(voice.Generated);
        Assert.Equal("voice-sid", generated.Sid);
        Assert.Equal("http://d1", generated.Endpoint);
    }

    [Fact]
    public void TurnEnd_OnANonVoiceSession_DoesNotFireVoiceAutoRefresh()
    {
        // The watcher is voice-only: a normal (non-voice) session finishing a turn must not
        // trigger any voice generation. "Needs you" reverts to the Director's raw signal (Option A).
        var voice = new RecordingVoice(/* no voice sessions */);
        using var watcher = BuildWatcher(voice);

        watcher.Observe("plain-sid", "Working", "http://d1");
        watcher.Observe("plain-sid", "WaitingForInput", "http://d1");

        Assert.Empty(voice.Generated);
    }

    [Fact]
    public void SessionReEntersWorking_ClearsTheStaleVoiceCache()
    {
        // The Working transition must clear the stale voice/text cache (issue #531 behavior, kept).
        var voice = new RecordingVoice("voice-sid");
        using var watcher = BuildWatcher(voice);

        watcher.Observe("voice-sid", "Working", "http://d1");
        watcher.Observe("voice-sid", "WaitingForInput", "http://d1");  // turn end -> generate
        watcher.Observe("voice-sid", "Working", "http://d1");          // user replied -> clear cache

        Assert.Single(voice.Generated);
        Assert.Contains("voice-sid", voice.Cleared);
    }
}
