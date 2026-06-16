using System.Diagnostics;
using CcDirector.Gateway.Cockpit;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Live supervision test: with a published Cockpit exe, the supervisor launches it, restarts it
/// after a crash, and stops it on dispose. Skipped when the exe is not present (e.g. CI without a
/// publish). Uses a dedicated port so it does not collide with a running Cockpit.
/// </summary>
public sealed class CockpitSupervisorTests
{
    private static readonly string CockpitExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "CC Director", "cockpit", "devthrottle-cockpit.exe");
    private const int TestPort = 7472;

    [Fact]
    public async Task Supervisor_launches_restarts_and_stops_the_cockpit()
    {
        if (!File.Exists(CockpitExe)) return; // published-build only

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        using (var sup = new CockpitSupervisor(enabled: true, exePath: CockpitExe, port: TestPort))
        {
            sup.Start();
            Assert.True(await WaitFor(http, TestPort, up: true, seconds: 45), "Cockpit should be launched");

            // Simulate a crash; the supervisor must bring it back.
            KillCockpits();
            Assert.True(await WaitFor(http, TestPort, up: true, seconds: 45), "Cockpit should be relaunched after a crash");
        } // Dispose stops the managed child.

        Assert.True(await WaitFor(http, TestPort, up: false, seconds: 15), "Cockpit should stop on dispose");
    }

    private static void KillCockpits()
    {
        foreach (var p in Process.GetProcessesByName("devthrottle-cockpit"))
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
            finally { p.Dispose(); }
        }
    }

    private static async Task<bool> WaitFor(HttpClient http, int port, bool up, int seconds)
    {
        var url = $"http://127.0.0.1:{port}/";
        for (var i = 0; i < seconds; i++)
        {
            bool reachable;
            try { _ = await http.GetAsync(url); reachable = true; }
            catch { reachable = false; }
            if (reachable == up) return true;
            await Task.Delay(1000);
        }
        return false;
    }
}
