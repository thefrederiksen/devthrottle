using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Issue #476 PROOF GENERATOR. Not part of the normal suite - it only runs when
/// CCD_476_PROOF points at an output path. It drives the REAL production decision core
/// (<see cref="AutoResumeLoop"/>) with a fixed clock at the default config values
/// (first retry 60s, interval 300s, give-up 12 attempts / 120min) and writes a timestamped
/// trace that mirrors exactly the <c>[TransientErrorAutoResume]</c> log lines the Director
/// emits at runtime. This is the deterministic stand-in for a live Anthropic 500: the cadence
/// and bounds are produced by the same code that ships, just clocked instead of timed - so the
/// "timestamped retry attempts spaced one interval apart" proof is exact and flake-free.
/// </summary>
public sealed class AutoResumeProofTraceGenerator
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    private DateTime _now = new(2026, 6, 21, 13, 30, 11, DateTimeKind.Utc); // matches the field screenshot time
    private readonly StringBuilder _trace = new();

    [Fact]
    public void Generate_476_proof_trace()
    {
        var outPath = Environment.GetEnvironmentVariable("CCD_476_PROOF");
        if (string.IsNullOrWhiteSpace(outPath))
            return; // not a proof run - no-op so the normal suite stays green and fast

        Line("=== Issue #476 auto-resume cadence trace (driven by AutoResumeLoop, the shipped decision core) ===");
        Line("Config: enabled=true, first_retry=60s, interval=300s, max_attempts=12, max_elapsed=120min");
        Line("");

        ScenarioRecover();
        Line("");
        ScenarioGiveUp();
        Line("");
        ScenarioOff();

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        File.WriteAllText(outPath, _trace.ToString());
    }

    private void ScenarioRecover()
    {
        Line("--- SCENARIO A: transient 500, retries on cadence, then RECOVERS on attempt 3 ---");
        var loop = new AutoResumeLoop(() => Default(true), () => _now);

        // Detection: the verbatim 500 lands on screen.
        Log($"session={SessionId} terminal shows: \"API Error: 500 Internal server error. This is a server-side issue, usually temporary\"");
        Apply(loop.OnScreenScan(hasTransientError: true));

        Advance(TimeSpan.FromSeconds(60)); // first retry due
        Apply(loop.OnRetryDue(hasTransientError: true)); // attempt 1

        Advance(TimeSpan.FromSeconds(300));
        Apply(loop.OnRetryDue(hasTransientError: true)); // attempt 2

        Advance(TimeSpan.FromSeconds(300));
        // The server recovered between retries: the next scan sees no error.
        Log($"session={SessionId} terminal now shows normal output (server recovered)");
        Apply(loop.OnRetryDue(hasTransientError: false)); // recovery
    }

    private void ScenarioGiveUp()
    {
        Line("--- SCENARIO B: transient 500 that never clears -> GIVE UP at the 12-attempt bound ---");
        var loop = new AutoResumeLoop(() => Default(true), () => _now);

        Log($"session={SessionId} terminal shows the transient 500");
        Apply(loop.OnScreenScan(hasTransientError: true));

        Advance(TimeSpan.FromSeconds(60));
        Apply(loop.OnRetryDue(true)); // attempt 1
        for (int i = 2; i <= 12; i++)
        {
            Advance(TimeSpan.FromSeconds(300));
            Apply(loop.OnRetryDue(true)); // attempts 2..12
        }
        Advance(TimeSpan.FromSeconds(300));
        Apply(loop.OnRetryDue(true)); // 13th due: bound reached -> give up
    }

    private void ScenarioOff()
    {
        Line("--- SCENARIO C: setting OFF -> zero retries even with the 500 on screen ---");
        var loop = new AutoResumeLoop(() => Default(false), () => _now);

        Log($"session={SessionId} terminal shows the transient 500, but auto_resume.enabled=false");
        Apply(loop.OnScreenScan(hasTransientError: true)); // -> None
        Advance(TimeSpan.FromSeconds(60));
        Apply(loop.OnRetryDue(true)); // -> None
        Log($"session={SessionId} RESULT: zero auto-continue attempts (feature OFF)");
    }

    private static AutoResumeConfig Default(bool enabled)
        => AutoResumeConfig.Default with { Enabled = enabled };

    private void Advance(TimeSpan by) => _now += by;

    /// <summary>Render an <see cref="AutoResumeStep"/> as the runtime log line it maps to.</summary>
    private void Apply(AutoResumeStep step)
    {
        switch (step.Kind)
        {
            case AutoResumeKind.ArmFirstRetry:
                Log($"session={SessionId} state=TRANSIENT-ERROR detected; auto-resume armed, first continue in {step.Delay.TotalSeconds:F0}s");
                break;
            case AutoResumeKind.Continue:
                Log($"session={SessionId} AUTO-CONTINUE attempt={step.Attempt}/{step.MaxAttempts} performed=true status=ok (submitted \"Please continue.\")");
                break;
            case AutoResumeKind.Recovered:
                Log($"session={SessionId} state=RECOVERED after {step.Attempt} auto-continue attempt(s); stopping retry loop");
                break;
            case AutoResumeKind.GiveUp:
                Log($"session={SessionId} GAVE-UP after {step.Attempt} attempt(s), {step.Elapsed.TotalMinutes:F1}min; flagging session needs-you (red)");
                break;
            case AutoResumeKind.None:
                Log($"session={SessionId} (no action)");
                break;
        }
    }

    private void Log(string msg) => Line($"{_now:yyyy-MM-dd HH:mm:ss}Z [TransientErrorAutoResume] {msg}");
    private void Line(string s) => _trace.AppendLine(s);
}
