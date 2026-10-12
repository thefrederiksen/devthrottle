using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// TWO REAL PROCESSES AGREEING ON WHERE A SIGNAL LIVES - the test this mechanism shipped without, and
/// the reason a whole platform's lifecycle could be inert while every suite was green.
///
/// What the in-process tests in <see cref="LifecycleSignalTests"/> cannot see: a real signal always
/// crosses the launcher-Director boundary, and the two ends resolve storage differently. A Director
/// redirects <c>CC_DIRECTOR_ROOT</c> to its instance home at startup; the launcher never redirects.
/// When the Unix request-file path was derived from each process's own storage view, the Director
/// polled a directory the launcher never wrote - every stop became a 20-second stall and a force-kill,
/// and "install it now" reported success into a directory nobody watched. A single process cannot
/// reproduce that divergence honestly, so both ends here are child processes running the production
/// <see cref="LifecycleSignal"/> through a probe executable, each given exactly the environment its
/// production counterpart has. The parent test process mutates nothing of its own.
///
/// WHAT THIS PROVES PER PLATFORM - stated so nobody mistakes one platform's pass for the other's:
/// - On macOS and Linux these fail without the shared-root fix in
///   <see cref="LifecycleSignal.UnixRequestPath"/> and pass with it: they pin that both ends derive
///   the request-file path from the SHARED root, across the production redirect.
/// - On Windows the kernel delivers by NAME alone - no file path exists to agree on - so these cannot
///   detect a path regression there. They still prove genuine cross-process delivery through the
///   named event, which no other test does, but the path property is exercised on Unix ONLY. The
///   Windows-side detector for the path expression itself is
///   <see cref="LifecycleSignalRequestPathTests"/>, which asserts the derivation as a value and
///   therefore fails on every platform if the expression regresses.
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class LifecycleSignalCrossProcessTests : IDisposable
{
    private readonly string _sharedRoot = Path.Combine(AppContext.BaseDirectory,
        "cc-signal-xproc-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _timeline = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private void Mark(string what) => _timeline.Add($"{_clock.ElapsedMilliseconds}ms {what}");
    private string Timeline => string.Join(" | ", _timeline);

    public void Dispose()
    {
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The shutdown direction: the LAUNCHER (un-redirected, at the shared root) raises; a DIRECTOR
    /// (redirected to its instance home, exactly as Program.Main redirects) must hear it. This is the
    /// direction whose loss turned every macOS stop into a force-kill.
    /// </summary>
    [Fact]
    public void ARedirectedDirector_HearsAShutdownRaisedFromTheLauncherRoot()
    {
        var name = "cc-director-test-xproc-" + Guid.NewGuid().ToString("N");

        using var listener = StartProbe("listen-redirected", name);
        WaitForListening(listener);

        using var raiser = StartProbe("raise", name);
        AssertDelivered(raiser);

        AssertSignalled(listener);
    }

    /// <summary>
    /// The "install it now" direction: a DIRECTOR (redirected) raises; the LAUNCHER (un-redirected)
    /// must hear it. This is the direction whose loss made the update button report success while
    /// nothing would ever happen.
    /// </summary>
    [Fact]
    public void TheLauncher_HearsARestartRaisedFromInsideARedirectedDirector()
    {
        var name = "cc-director-test-xproc-" + Guid.NewGuid().ToString("N");

        using var listener = StartProbe("listen", name);
        WaitForListening(listener);

        using var raiser = StartProbe("raise-redirected", name);
        AssertDelivered(raiser);

        AssertSignalled(listener);
    }

    /// <summary>
    /// Start one end of the signal in a child process. Every child gets the SAME shared root - the
    /// production condition - and the "-redirected" verbs then redirect themselves the way a real
    /// Director does. The probe runs the production LifecycleSignal; it re-implements nothing.
    /// </summary>
    private Process StartProbe(string verb, string name)
    {
        var probeDll = Path.Combine(AppContext.BaseDirectory, "CcDirector.Core.Tests.SignalProbe.dll");
        Assert.True(File.Exists(probeDll),
            $"the signal probe was not built into the test output directory: {probeDll}");

        var psi = new ProcessStartInfo
        {
            FileName = DotnetMuxer(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(probeDll);
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add("20");
        psi.Environment["CC_DIRECTOR_ROOT"] = _sharedRoot;

        Mark($"starting probe {verb}");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the signal probe");
        Mark($"probe {verb} started pid={process.Id}");
        return process;
    }

    /// <summary>
    /// The dotnet host to run the probe with: the exact host running THIS test when that is the
    /// muxer, else the SDK's DOTNET_HOST_PATH, else "dotnet" from the search path. Running the probe
    /// through the muxer rather than its apphost means the child resolves the same runtime the test
    /// run already proved present.
    /// </summary>
    private static string DotnetMuxer()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return self;
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
            return hostPath;
        return "dotnet";
    }

    private void WaitForListening(Process listener)
    {
        var line = listener.StandardOutput.ReadLine();
        Mark($"listener said: {line ?? "(eof)"}");
        // The failure message is built ONLY on failure: it drains the listener's stderr, which blocks
        // until the process exits - evaluated eagerly (as an Assert.True argument) it silently held
        // this test for the listener's whole lifetime and made a working mechanism look undelivered.
        if (line != "LISTENING")
            Assert.Fail($"the listener probe did not arm; it said: {line ?? "(nothing)"} "
                        + $"{listener.StandardError.ReadToEnd()} Timeline: {Timeline}");
    }

    private void AssertDelivered(Process raiser)
    {
        var finished = raiser.WaitForExit(TimeSpan.FromSeconds(30));
        Mark($"raiser finished={finished} code={(finished ? raiser.ExitCode : -1)}");
        if (!finished)
            Assert.Fail($"the raiser probe did not finish. Timeline: {Timeline}");
        if (raiser.ExitCode != 0)
            Assert.Fail($"the raise was not delivered: {raiser.StandardOutput.ReadToEnd()} "
                        + $"{raiser.StandardError.ReadToEnd()} Timeline: {Timeline}");
    }

    private void AssertSignalled(Process listener)
    {
        if (!listener.WaitForExit(TimeSpan.FromSeconds(30)))
            Assert.Fail($"the listener probe never finished. Timeline: {Timeline}");
        Mark($"listener exit code={listener.ExitCode}");
        if (listener.ExitCode != 0)
        {
            var files = Directory.Exists(_sharedRoot)
                ? string.Join(", ", Directory.EnumerateFiles(_sharedRoot, "*", SearchOption.AllDirectories))
                : "(shared root does not exist)";
            Assert.Fail("the raise was delivered but the listener never heard it - the two processes "
                + $"do not agree on where the signal lives. Listener said: {listener.StandardOutput.ReadToEnd()} "
                + $"{listener.StandardError.ReadToEnd()} Shared root holds: {files} Timeline: {Timeline}");
        }
    }
}

/// <summary>
/// The request-path DERIVATION, pinned as a value. This is the only detector of the shared-root
/// property that runs meaningfully on WINDOWS: the Windows arm never touches this path (the kernel
/// object is addressed by name alone, which is why the original defect was invisible there), but the
/// expression itself computes everywhere, so asserting it here makes a regression fail every
/// platform's suite rather than only the platform that suffers it.
/// </summary>
[Collection("CcStorageRoot")] // serializes the classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class LifecycleSignalRequestPathTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _sharedRoot = Path.Combine(Path.GetTempPath(),
        "cc-signal-path-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        InstanceContext.Initialize(null, wasExplicit: false); // recapture the true root for later tests
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// After the production redirect - the Director pointing CC_DIRECTOR_ROOT at its instance home -
    /// the request path must STILL resolve under the shared root, because that is where the
    /// un-redirected launcher on the other end of every signal resolves it.
    /// </summary>
    [Fact]
    public void TheRequestPath_ResolvesUnderTheSharedRoot_EvenAfterTheDirectorsOwnRedirect()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _sharedRoot);
        InstanceContext.Initialize(null, wasExplicit: false);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", InstanceContext.InstanceHome);

        var path = LifecycleSignal.UnixRequestPath("cc-director-test-path");

        Assert.Equal(
            Path.Combine(_sharedRoot, "config", "lifecycle-signals", "cc-director-test-path.request"),
            path);
    }
}
