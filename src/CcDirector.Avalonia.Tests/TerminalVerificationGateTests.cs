using System;
using System.Diagnostics;
using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The gate in front of the terminal verification check admits one run per interval and answers a
/// request inside the interval with the time left, so the caller runs one trailing check instead of
/// dropping the request. Pure logic - no window is constructed.
/// </summary>
public sealed class TerminalVerificationGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static long At(TimeSpan sinceStart) => 1_000_000 + (long)(sinceStart.TotalSeconds * Stopwatch.Frequency);

    [Fact]
    public void Admit_FirstRequest_RunsNow()
    {
        var gate = new TerminalVerificationGate(Interval);

        Assert.Equal(TimeSpan.Zero, gate.Admit(At(TimeSpan.Zero)));
    }

    [Fact]
    public void Admit_SecondRequestInsideTheInterval_AnswersTheTimeLeft()
    {
        var gate = new TerminalVerificationGate(Interval);
        gate.Admit(At(TimeSpan.Zero));

        var wait = gate.Admit(At(TimeSpan.FromMilliseconds(500)));

        Assert.True(wait > TimeSpan.Zero, "a request inside the interval must be told to wait");
        Assert.InRange(wait.TotalMilliseconds, 1490, 1510);
    }

    [Fact]
    public void Admit_BurstInsideTheInterval_AdmitsNoneUntilTheIntervalEnds()
    {
        var gate = new TerminalVerificationGate(Interval);
        gate.Admit(At(TimeSpan.Zero));

        for (var ms = 50; ms < 2000; ms += 50)
            Assert.True(gate.Admit(At(TimeSpan.FromMilliseconds(ms))) > TimeSpan.Zero, $"request at {ms} ms was admitted");

        Assert.Equal(TimeSpan.Zero, gate.Admit(At(TimeSpan.FromMilliseconds(2000))));
    }

    [Fact]
    public void Admit_TrailingRequestAtTheAnsweredWait_RunsThen()
    {
        var gate = new TerminalVerificationGate(Interval);
        gate.Admit(At(TimeSpan.Zero));
        var asked = TimeSpan.FromMilliseconds(1800);
        var wait = gate.Admit(At(asked));

        Assert.Equal(TimeSpan.Zero, gate.Admit(At(asked + wait)));
    }

    [Fact]
    public void Admit_AfterAnAdmittedRun_TheNextIntervalStartsFromThatRun()
    {
        var gate = new TerminalVerificationGate(Interval);
        gate.Admit(At(TimeSpan.Zero));
        gate.Admit(At(TimeSpan.FromSeconds(3)));

        Assert.True(gate.Admit(At(TimeSpan.FromSeconds(4))) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, gate.Admit(At(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Constructor_NonPositiveInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalVerificationGate(TimeSpan.Zero));
    }
}
