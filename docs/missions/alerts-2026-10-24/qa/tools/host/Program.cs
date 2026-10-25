// A LOCAL Gateway for the Alerts QA runs (Phase 4, and Phase 5's phone screen). Committed with the QA run as the record of how the screenshots were
// made; it is not part of the solution and nothing builds it but `dotnet run` from this folder. It starts the real GatewayHost in this
// process on loopback, self-host mode, with its own throwaway data folder, seeds alerts straight into the Gateway's own
// stores (the way the Gateway tests do), registers one browser device, and serves the built Cockpit and phone app from
// wwwroot next to this program. It then reads commands from stdin:
//   reopen   - put the allowed message request back to waiting (to prove a clear is refused on a needs-answer alert)
//   down     - stop the Gateway (to prove the "unreachable" line with the last-known list)
//   quit     - stop and exit
using System.Reflection;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Secrets;

var scenario = args.Length > 0 ? args[0] : "mixed";
var port = args.Length > 1 ? int.Parse(args[1]) : 7993;
var root = Path.Combine(Path.GetTempPath(), "alertsqa-root-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

var gateway = new GatewayHost(
    port: port,
    token: "qa-" + Guid.NewGuid().ToString("N"),
    authEnabled: true,
    instancesDirectory: Path.Combine(root, "instances"),
    workListsPath: Path.Combine(root, "worklists", "worklists.json"),
    snoozePath: Path.Combine(root, "snooze", "snooze.json"),
    promptLogPath: Path.Combine(root, "prompt-log"),
    inputStatsPath: Path.Combine(root, "stats", "input-stats.db"),
    options: new GatewayHostOptions { Hosted = false });
await gateway.StartAsync();

var tenant = TenantId.Local;
var key = gateway.Devices.Register("qa-browser", "QA-DESK", "web", "browser").DeviceKey;
// The phone app signs in as a phone, as a real phone does: the Gateway decides from the device's type where an answer
// was given, and refuses a secret transfer answered "on the phone" from a desktop browser's key.
var phoneKey = gateway.Devices.Register("qa-phone", "QA-PHONE", "android", "phone").DeviceKey;
var now = DateTime.UtcNow;

const string DirectorId = "qa-director-1";
const string Machine = "QA-DESK";
string Sid(int n) => $"aaaaaaaa-0000-0000-0000-{n:D12}";
var sessions = new (int Number, string Name, string State, string Color)[]
{
    (121, "Release - Manager", "WaitingForInput", "red"),
    (125, "Release - Release Manager", "Working", "blue"),
    (130, "Docs - Writer", "Idle", "grey"),
    (142, "Rig - Developer", "Working", "blue"),
};
gateway.Registry.RegisterFromStream(DirectorId, Machine, "qa", "2.31.0", pid: Environment.ProcessId, startedAt: now, tenant: tenant);
gateway.PushedSessions.RegisterConnection(tenant, DirectorId, "qa-conn");
gateway.PushedSessions.ApplySnapshot(tenant, DirectorId, "qa-conn", 1, sessions.Select(s => new SessionDto
{
    SessionId = Sid(s.Number),
    Number = s.Number,
    Name = s.Name,
    ActivityState = s.State,
    StatusColor = s.Color,
    LastActivityAt = now,
    RepoPath = @"D:\repos\example-app",
    MachineName = Machine,
    DirectorId = DirectorId,
}).ToList());

var secrets = (SecretTransferStore)typeof(GatewayHost).GetProperty("SecretTransfers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gateway)!;
var links = (FleetMessageLinkRequestStore)typeof(GatewayHost).GetProperty("MessageLinkRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gateway)!;
var restarts = gateway.DirectorRestartRequests;

string Restart(string machine, string reason, string? launcherVersion = null, string? capabilityReason = null, string? guarded = null, string? asker = null)
{
    var r = new DirectorRestartRequestDto
    {
        Machine = machine,
        DirectorId = "d-" + machine,
        DirectorName = machine,
        RequestedBySessionId = Sid(142),
        // The requester as DirectorRestartRequestService.RequesterName stores it: the session's name and its short id.
        RequestedBySessionName = asker ?? "Rig - Developer (aaaaaaaa)",
        Reason = reason,
        LiveSessionCount = 2,
        LiveSessionsSentence = "That Director holds 2 live sessions right now; each will be asked to write a handover and close, leaf-first.",
        Capability = new MachineRestartCapabilityDto
        {
            Machine = machine,
            LauncherVersion = launcherVersion ?? "2.31.0",
            Verdict = RestartVerdict.CanRestart,
            Reason = capabilityReason ?? "The launcher on this machine answers and can restart the Director.",
            GuardedRestart = CapabilityState.Available,
            GuardedRestartReason = guarded ?? "",
        },
        Title = $"Restart the Director on {machine}?",
        AskedBySentence = $"{asker ?? "Rig - Developer (aaaaaaaa)"} asks: {reason}",
        AcceptSentence = "Accept once, and nothing more is asked of you: the Director asks its 2 live sessions to write a handover and close, checks the machine again, and reports here.",
    };
    if (!restarts.TryCreate(tenant, r, out _)) throw new InvalidOperationException("restart seed refused for " + machine);
    return r.Id;
}

string Secret(string entry, string to, int asker, string reason)
{
    // "Asked by" as SecretTransferEndpoints.AskedBy stores it for a session: its number and its name.
    var name = sessions.First(s => s.Number == asker).Name;
    var row = secrets.Create(tenant, new SecretTransferAsk(entry, entry, Machine, to, false, Sid(asker), $"Session {asker:000} \"{name}\"", reason), null, now)
        ?? throw new InvalidOperationException("secret seed refused for " + entry);
    return row.TransferId;
}

string Link(int from, int to, string reason, string amount = FleetMessageLinkAmounts.OnceWithReply)
{
    var ask = links.Ask(tenant, Sid(from), Sid(to), reason, now, amount);
    if (ask.Request is null) throw new InvalidOperationException($"link seed refused {from}->{to}");
    return ask.Request.RequestId;
}

string? allowedLink = null;
switch (scenario)
{
    case "zero":
        break;
    case "one":
        Restart("mac-mini", "the launcher has a staged update to v2.31.0");
        break;
    case "twelve":
        foreach (var m in new[] { "mac-mini", "linux-rig", "build-box", "win-tester", "studio-pc", "lab-02" })
            Restart(m, "the launcher has a staged update to v2.31.0");
        Secret("vercel-bypass", "mac-mini", 142, "The preview deploy needs it");
        Secret("posthog-key", "linux-rig", 130, "The analytics page reads it");
        Secret("supabase-url", "build-box", 125, "The release job reads it");
        Link(121, 125, "I need to hand the merge over");
        Link(130, 125, "The docs need the release notes");
        Link(142, 121, "The rig run is done and the manager should know");
        break;
    case "mixed":
    {
        // Needs you: one of each kind.
        Restart("mac-mini", "the launcher has a staged update to v2.31.0");
        Secret("vercel-bypass", "mac-mini", 142, "The preview deploy needs it");
        Link(121, 125, "I need to hand the merge over");
        // Earlier: a declined restart, an accepted restart still running, a denied transfer, an allowed request.
        var declined = Restart("build-box", "a new launcher is waiting");
        restarts.Decline(tenant, declined, "declined by the owner: not during the release", out _);
        var running = Restart("linux-rig", "the Director is two versions behind");
        restarts.Accept(tenant, running, out _);
        restarts.Report(tenant, running, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Accepted, Progress = "the Director has taken the cycle: it is draining itself and will report here" }, out _);
        var denied = Secret("posthog-key", "linux-rig", 130, "The analytics page reads it");
        secrets.TryAnswer(tenant, denied, new SecretTransferAnswer(false, SecretTransferPlaces.Cockpit, "the owner", null, null), now);
        allowedLink = Link(130, 142, "The docs need the rig results");
        links.TryAnswer(tenant, allowedLink, FleetMessageLinkRequestStatuses.Allowed, "the owner", now, FleetMessageLinkAmounts.OnceWithReply);
        break;
    }
    case "overflow":
    {
        var longVersion = "2.31.0-preview.20261024.214130+sha.3f9c0a1b7e2";
        var longReason = string.Concat(Enumerable.Repeat("the-launcher-reported-a-staged-update-without-any-break-", 6))[..300];
        Restart("devthrottle-mac-mini-build-02", longReason, longVersion,
            capabilityReason: $"The launcher {longVersion} answers on this machine and can restart the Director; {longReason}",
            guarded: "GuardedRestartUnavailableBecauseTheLauncherDeclaredNoGuardedRestartCommandInItsCapabilitiesList",
            asker: "Rig - Developer-with-a-very-long-session-name-that-has-no-spaces-anywhere-at-all (aaaaaaaa)");
        Secret("a-very-long-entry-name-for-a-secret-that-has-no-break-points-anywhere", "devthrottle-mac-mini-build-02", 142, longReason);
        break;
    }
    default:
        throw new ArgumentException("unknown scenario " + scenario);
}

Console.WriteLine($"READY port={gateway.Port} key={key} phonekey={phoneKey} scenario={scenario}");
Console.Out.Flush();

string? line;
while ((line = Console.ReadLine()) is not null)
{
    line = line.Trim();
    if (line == "quit") break;
    if (line == "reopen" && allowedLink is not null)
    {
        Console.WriteLine($"REOPENED {links.Reopen(tenant, allowedLink)}");
    }
    else if (line == "down")
    {
        // The browser holds connections open; stopping waits for them. The listener closes first, so after a short wait
        // the Gateway no longer answers, which is all this step needs.
        var stopping = gateway.StopAsync();
        var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(8))) == stopping;
        Console.WriteLine($"DOWN finished={finished}");
    }
    else Console.WriteLine("UNKNOWN " + line);
    Console.Out.Flush();
}

var final = gateway.StopAsync();
await Task.WhenAny(final, Task.Delay(TimeSpan.FromSeconds(10)));
Console.WriteLine("BYE");
Environment.Exit(0);
