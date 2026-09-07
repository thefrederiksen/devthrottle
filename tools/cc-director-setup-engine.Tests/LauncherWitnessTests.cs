using System.Text.Json;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The witness has one job: refuse to call a launcher healthy on the strength of a process existing.
/// The machine this was written for was running a launcher that was registered, alive, heartbeating,
/// and unable to receive a single command - so every test here is written around that shape.
/// </summary>
public class LauncherWitnessTests : IDisposable
{
    private readonly string _root;
    private readonly string _registration;

    public LauncherWitnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-lwit-" + Guid.NewGuid().ToString("N"));
        _registration = LauncherWitness.RegistrationPathFor(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_registration)!);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private void WriteRegistration(int pid, string? version)
    {
        var payload = version is null
            ? JsonSerializer.Serialize(new { pid })
            : JsonSerializer.Serialize(new { pid, version });
        File.WriteAllText(_registration, payload);
    }

    private LauncherWitness Witness(bool? listener, Func<int, bool>? alive = null,
        IReadOnlyList<LauncherProcess>? running = null) =>
        new(_root)
        {
            RegistrationPath = _registration,
            ProcessIsAlive = alive ?? (_ => true),
            HasListener = _ => listener,
            ListLauncherProcesses = () => running ?? [],
        };

    [Fact]
    public void RegistrationPath_IsUnderTheRootItWasGiven()
    {
        // The whole reason the root is a parameter: inside a Director the default path resolves to the
        // instance home, where no launcher has ever written, and the answer is "not installed" for ever.
        Assert.Equal(Path.Combine(_root, "config", "launcher", "launcher.json"),
            LauncherWitness.RegistrationPathFor(_root));
    }

    [Fact]
    public void NoRegistration_IsNotWitnessed()
    {
        var reading = Witness(listener: true).Read();

        Assert.False(reading.Registered);
        Assert.False(reading.Witnessed);
        Assert.Null(new LauncherWitness(_root)
        {
            RegistrationPath = _registration,
            HasListener = _ => true,
        }.WitnessedVersion());
    }

    [Fact]
    public void AliveAndListening_IsWitnessed_AndReportsItsVersion()
    {
        WriteRegistration(4242, "2.0.4");

        var reading = Witness(listener: true).Read();

        Assert.True(reading.Witnessed);
        Assert.Equal("2.0.4", reading.Version);
        Assert.Equal(LauncherCommandSurface.Present, reading.CommandSurface);
        Assert.Equal("2.0.4", Witness(listener: true).WitnessedVersion());
    }

    [Fact]
    public void AliveButNothingListening_IsNOTWitnessed()
    {
        // The 2026-09-06 machine, exactly: a launcher that is up and cannot be told anything. Every
        // check that asks "did a process start" says yes about it, which is why this one may not.
        WriteRegistration(4242, "1.9.8");

        var reading = Witness(listener: false).Read();

        Assert.True(reading.Registered);
        Assert.True(reading.ProcessAlive);
        Assert.False(reading.Witnessed);
        Assert.Equal(LauncherCommandSurface.Absent, reading.CommandSurface);
        Assert.Contains("cannot be told anything", reading.Detail);
        Assert.Null(Witness(listener: false).WitnessedVersion());
    }

    [Fact]
    public void RegistrationNamingADeadProcess_IsNotWitnessed()
    {
        WriteRegistration(4242, "2.0.4");

        var reading = Witness(listener: true, alive: _ => false).Read();

        Assert.True(reading.Registered);
        Assert.False(reading.ProcessAlive);
        Assert.False(reading.Witnessed);
    }

    [Fact]
    public void WhereAListenerCannotBeObserved_ThatIsNOTAWITNESS()
    {
        // Unix: the signal is a request file that a listener polls, so there is nothing to consult and
        // a "no" would be invented. The reading says so in its own words rather than pretending - and
        // it does NOT count as witnessed.
        //
        // THIS TEST ASSERTED THE OPPOSITE. Accepting NotObservable was reasoned as "a listener genuinely
        // cannot be observed here, so refusing would refuse for ever" - but what it actually did was let
        // a live registration ALONE pass, which is precisely the liveness-only proof this class exists
        // to forbid, reintroduced on the one platform where nobody would see it. A witness that cannot
        // fail on a platform is not a witness on that platform. The refusal for ever is the honest
        // answer, and it is the caller's job to say so plainly rather than the boolean's job to hide it:
        // LauncherUpdateOwner declines to swap at all where this is the reading.
        WriteRegistration(4242, "2.0.4");

        var reading = Witness(listener: null).Read();

        Assert.Equal(LauncherCommandSurface.NotObservable, reading.CommandSurface);
        Assert.False(reading.Witnessed);
        Assert.Null(Witness(listener: null).WitnessedVersion());
        Assert.Contains("cannot be observed on this platform", reading.Detail);
    }

    [Fact]
    public void ALauncherRegisteredUNDERANOTHERROOT_IsNamedAsThat_NotAsNoLauncher()
    {
        // The 2026-09-06 second defect: a launcher started with a Director's CC_DIRECTOR_ROOT inherited
        // takes the instance home for the machine root, so it registers and listens under that root.
        // From here it looks like an empty machine, and "no launcher" would send the next person hunting
        // for a process that is plainly running.
        File.Delete(_registration);
        var running = new[] { new LauncherProcess(30736, Path.Combine(_root, "launcher", "cc-launcher.exe") + " --managed") };

        var reading = Witness(listener: false, running: running).Read();

        Assert.False(reading.Witnessed);
        Assert.Contains("30736", reading.Detail);
        Assert.Contains("different storage root", reading.Detail);
    }

    [Fact]
    public void WithNoLauncherRunningAtAll_TheReadingDoesNotInventOne()
    {
        File.Delete(_registration);

        var reading = Witness(listener: false).Read();

        Assert.False(reading.Witnessed);
        Assert.DoesNotContain("different storage root", reading.Detail);
    }

    [Fact]
    public void TheSignalItAsksAbout_IsTheRestartTheDirectorOne()
    {
        // Named for the root, so a test rig and the installed launcher never answer for each other.
        var name = new LauncherWitness(_root).CommandSignalName;

        Assert.Equal(CcDirector.Core.Lifecycle.LifecycleSignalNames.LauncherRestartDirector(_root), name);
        Assert.Contains("launcher-restart-director", name);
    }
}
