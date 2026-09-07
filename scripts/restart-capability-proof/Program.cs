using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;

namespace CcDirector.Proof.RestartCapability;

/// <summary>
/// THE END-TO-END PROOF for issue #2720: a machine says whether it can be restarted, before a drain.
///
/// It stands up a REAL <see cref="GatewayHost"/> and a REAL <see cref="LauncherStreamClient"/> - the
/// production classes at both ends, on this machine - and asks
/// <c>GET /machines/{machine}/restart-capability</c> for one known-GOOD input and two deliberately
/// re-created known-BAD ones.
///
/// A CAPABILITY CHECK THAT HAS NEVER RETURNED NO IS NOT A CHECK. The known-bad input that produced this
/// whole phase is gone from this machine - the launcher was updated by hand to 2.0.4 and then
/// self-updated to 2.0.6 during the first run - so the no cases here are RE-CREATED on purpose, and
/// each one says which shape it is re-creating.
///
/// WHAT IT DOES NOT TOUCH, AND WHY THAT MATTERS. It never starts, stops or reconfigures the installed
/// launcher on this machine. That launcher is the parent of the Director carrying every mission here,
/// and permission to interfere with it was asked for and never granted. Every root below is a
/// throwaway directory this program creates and deletes, and every lifecycle signal it arms is named
/// for one of those roots - which is exactly why signal names are keyed to a storage root, so a rig and
/// an installed launcher can never hear each other.
///
/// NO SIGNAL IS EVER RAISED. The rig ARMS a restart signal so the launcher has an honest thing to
/// declare, and its handler does nothing at all. Raising one would restart a Director.
/// </summary>
public static class Program
{
    private const string Token = "restart-capability-proof-token";

    private static int _failures;

    public static async Task<int> Main()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cc-2720-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        Console.WriteLine("=================================================================");
        Console.WriteLine(" ISSUE 2720 - CAN THIS MACHINE BE RESTARTED? END-TO-END PROOF");
        Console.WriteLine("=================================================================");
        Console.WriteLine($" machine        : {Environment.MachineName}");
        Console.WriteLine($" scratch root   : {scratch}");
        Console.WriteLine($" installed root : {CcStorageRootOfThisMachine()}  (NEVER TOUCHED)");
        Console.WriteLine();

        var gatewayRoot = Path.Combine(scratch, "gateway");
        Directory.CreateDirectory(gatewayRoot);

        var gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(gatewayRoot, "instances"),
            workListsPath: Path.Combine(gatewayRoot, "worklists", "worklists.json"),
            streamMode: true);
        await gateway.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        try
        {
            await CaseNoLauncherAsync(http);
            await CaseRegisteredButNoStreamAsync(http, gateway);
            await CaseHealthyLauncherAsync(http, gateway, Path.Combine(scratch, "good-root"));
            await CaseLauncherServingAnInstanceHomeAsync(
                http, gateway, Path.Combine(scratch, "bad-root", "instances", "default"));
        }
        finally
        {
            await gateway.StopAsync();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }

        Console.WriteLine("=================================================================");
        Console.WriteLine(_failures == 0
            ? " RESULT: PASS - the check answered YES once and NO three times, each with its own reason."
            : $" RESULT: FAIL - {_failures} expectation(s) not met. Read the answers above.");
        Console.WriteLine("=================================================================");
        return _failures == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------------------------
    // Case 1 - nothing registered at all
    // -----------------------------------------------------------------------------------------
    private static async Task CaseNoLauncherAsync(HttpClient http)
    {
        Banner("CASE 1 - NO LAUNCHER", "Nothing is registered for this machine on this account.");
        var answer = await AskAsync(http, Environment.MachineName);
        Expect(answer, verdict: "CannotRestart", reach: "NoLauncher",
            reasonContains: "Install and start cc-launcher");
    }

    // -----------------------------------------------------------------------------------------
    // Case 2 - THE 2026-09-06 SHAPE, RE-CREATED
    // -----------------------------------------------------------------------------------------
    private static async Task CaseRegisteredButNoStreamAsync(HttpClient http, GatewayHost gateway)
    {
        Banner("CASE 2 - REGISTERED AND HEARTBEATING, NO COMMAND STREAM",
            "The re-created known-bad input: a launcher registered and reaching the Gateway that holds "
            + "no command stream. This is the state of the machine on 2026-09-06 - launcher 1.9.8, "
            + "running, registered, heartbeating, and unable to receive a single command. Seventeen "
            + "sessions were drained before anybody found out the answer was no.");

        // The presence row a launcher's registration client posts. NO stream is opened, which is the
        // whole point: a launcher predating the command stream registers perfectly and opens none.
        gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = Environment.MachineName,
            Pid = 4242,
            Version = "1.9.8",
            StartedAt = DateTime.UtcNow.AddHours(-3),
        });

        var answer = await AskAsync(http, Environment.MachineName);
        Expect(answer, verdict: "CannotRestart", reach: "NotStreamCapable",
            reasonContains: "predates the command stream");
        ExpectAlso(answer, "reason", "network connection is not the problem");
        ExpectAlso(answer, "reason", "1.9.8");
    }

    // -----------------------------------------------------------------------------------------
    // Case 3 - THE YES
    // -----------------------------------------------------------------------------------------
    private static async Task CaseHealthyLauncherAsync(HttpClient http, GatewayHost gateway, string root)
    {
        Banner("CASE 3 - A REAL LAUNCHER ON AN ORDINARY ROOT",
            "A real LauncherStreamClient - the production class - joins the command stream and declares "
            + "itself, with its restart signal armed. THE LIMIT, STATED: this is this machine and this "
            + "machine's launcher code, on an ISOLATED storage root. It is NOT the installed launcher, "
            + "which is the parent of the Director carrying the fleet and which this rig is not "
            + "permitted to touch.");

        await using var launcher = await JoinAsRealLauncherAsync(gateway, root, "2.0.6");

        var answer = await AskAsync(http, Environment.MachineName);
        Expect(answer, verdict: "CanRestart", reach: "Connected", reasonContains: "can be restarted");
        ExpectAlso(answer, "declaration", "Declared");
        ExpectAlso(answer, "restartSignal", "Listening");
        ExpectAlso(answer, "guardedRestart", "Unavailable");   // this build honours no condition yet
    }

    // -----------------------------------------------------------------------------------------
    // Case 4 - THE OTHER KNOWN-BAD, AND THE INVISIBLE ONE
    // -----------------------------------------------------------------------------------------
    private static async Task CaseLauncherServingAnInstanceHomeAsync(
        HttpClient http, GatewayHost gateway, string instanceHomeRoot)
    {
        Banner("CASE 4 - A REAL LAUNCHER SERVING A DIRECTOR'S INSTANCE HOME",
            "The second re-created known-bad input, and the one that is invisible from every other "
            + "angle. On 2026-09-06 a launcher was started from a shell carrying a Director's "
            + "CC_DIRECTOR_ROOT, inherited it, and took that Director's instance home for the machine "
            + "root. It registered, heartbeated, opened its stream and armed both signals - all filed "
            + "under a root nothing else computes. Watch reach, declaration and restartSignal below: "
            + "every one of them reads healthy.");

        await using var launcher = await JoinAsRealLauncherAsync(gateway, instanceHomeRoot, "2.0.6");

        var answer = await AskAsync(http, Environment.MachineName);
        Expect(answer, verdict: "CannotRestart", reach: "Connected",
            reasonContains: "instance home");
        ExpectAlso(answer, "declaration", "Declared");
        ExpectAlso(answer, "restartSignal", "Listening");
        ExpectAlso(answer, "reason", "no CC_DIRECTOR_ROOT");
    }

    // -----------------------------------------------------------------------------------------
    // The rig
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Start a REAL launcher stream client against an isolated root and wait until the Gateway has
    /// bound it.
    ///
    /// THE ROOT IS SET BEFORE THE DECLARATION IS COMPUTED, and <see cref="InstanceContext.Initialize"/>
    /// re-captures it - which is the point of case 4. The declaration reports the root the launcher is
    /// ACTUALLY serving, so a rig that computed it from where the root was SUPPOSED to be would report
    /// the healthy answer for precisely the machine state that is broken.
    /// </summary>
    private static async Task<LauncherRig> JoinAsRealLauncherAsync(GatewayHost gateway, string root, string version)
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        InstanceContext.Initialize(slug: null, wasExplicit: false);

        // The presence row, so the answer can name a version. A real launcher posts this itself.
        gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = Environment.MachineName,
            Pid = Environment.ProcessId,
            Version = version,
            StartedAt = DateTime.UtcNow,
        });

        // ARMED, NEVER RAISED, and its handler does nothing. The launcher needs an honest thing to
        // declare about its restart signal; a handler that restarted anything would make asking the
        // question destructive, which is the one thing this whole phase exists to avoid.
        var signal = LifecycleSignal.Listen(LifecycleSignalNames.LauncherRestartDirector(), () => { });

        var client = new LauncherStreamClient(
            new GatewayConfig { Url = $"http://127.0.0.1:{gateway.Port}", Token = Token },
            version,
            new DirectorSupervisor(new InstallLayout(root)),
            new LaunchService());
        client.Start();

        // Wait for the bind rather than assume it. A fixed sleep would make a slow machine look like a
        // launcher that never joined, which is one of the answers under test.
        for (var i = 0; i < 200; i++)
        {
            if (gateway.LauncherConnections.IsStreamConnected(TenantId.Local, Environment.MachineName))
                return new LauncherRig(client, signal);
            await Task.Delay(50);
        }

        Console.WriteLine("  [rig] the launcher never joined the stream within 10s - the rig is broken, "
                          + "and this is NOT evidence about the machine.");
        _failures++;
        return new LauncherRig(client, signal);
    }

    private sealed record LauncherRig(LauncherStreamClient Client, ILifecycleSignalListener Signal) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Signal.Dispose();
        }
    }

    private static async Task<JsonElement> AskAsync(HttpClient http, string machine)
    {
        var response = await http.GetAsync($"machines/{machine}/restart-capability");
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"  GET /machines/{machine}/restart-capability -> {(int)response.StatusCode}");
        var answer = JsonDocument.Parse(body).RootElement.Clone();

        Console.WriteLine($"    verdict          : {Str(answer, "verdict")}");
        Console.WriteLine($"    reason           : {Str(answer, "reason")}");
        Console.WriteLine($"    reach            : {Str(answer, "reach")}");
        Console.WriteLine($"    declaration      : {Str(answer, "declaration")}");
        Console.WriteLine($"    restart signal   : {Str(answer, "restartSignal")}");
        Console.WriteLine($"    launcher version : {Str(answer, "launcherVersion")}");
        Console.WriteLine($"    serving root key : {Str(answer, "servingRootKey")}");
        Console.WriteLine($"    instance home?   : {Str(answer, "servingRootIsInstanceHome")}");
        Console.WriteLine($"    guarded restart  : {Str(answer, "guardedRestart")}");
        Console.WriteLine($"    guarded reason   : {Str(answer, "guardedRestartReason")}");
        Console.WriteLine();
        return answer;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "(absent)";

    private static void Expect(JsonElement answer, string verdict, string reach, string reasonContains)
    {
        Check("verdict", verdict, Str(answer, "verdict"));
        Check("reach", reach, Str(answer, "reach"));
        if (!Str(answer, "reason").Contains(reasonContains, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  MISMATCH reason: expected it to contain '{reasonContains}'");
            _failures++;
        }
    }

    private static void ExpectAlso(JsonElement answer, string field, string contains)
    {
        if (!Str(answer, field).Contains(contains, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  MISMATCH {field}: expected it to contain '{contains}', got '{Str(answer, field)}'");
            _failures++;
        }
    }

    private static void Check(string what, string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        Console.WriteLine($"  MISMATCH {what}: expected '{expected}', got '{actual}'");
        _failures++;
    }

    private static void Banner(string title, string why)
    {
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine(title);
        Console.WriteLine("-----------------------------------------------------------------");
        foreach (var line in Wrap(why, 78)) Console.WriteLine("  " + line);
        Console.WriteLine();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width) { yield return line; line = ""; }
            line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }

    /// <summary>The machine's real storage root, printed once so the report can show what was NOT
    /// touched. Read before any root is redirected.</summary>
    private static string CcStorageRootOfThisMachine()
        => Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cc-director");
}
