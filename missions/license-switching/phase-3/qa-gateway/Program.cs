using System.Reflection;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests;

// A REAL GATEWAY WITH SEEDED LICENSE DATA, FOR THE LICENSES PAGE TO BE RUN AGAINST (License Switching, phase 3).
//
// It starts the product's own GatewayHost in hosted mode on a loopback port the operating system picks, with its
// whole storage under a throwaway folder (CC_DIRECTOR_ROOT), so nothing of the machine's real Gateway is read or
// written. Four accounts are enrolled the way the Gateway's own host tests enroll them, each with a browser key:
//   - "owner":   two machines whose usage reports go in through the Gateway's own ingest, a switch log, and one
//                sign-in whose last renewal the machine reported as failed. It also has a WORKSTATION key - the
//                credential of a Director, which may read the page and is refused both writes;
//   - "refused": the same two machines, with no log. On the command `fill-b` its machines report account B at its
//                limit, so a Switch now the page already offered is refused by the Gateway;
//   - "barred":  the same two machines, with account B barred from the automatic choice of a target;
//   - "empty":   enrolled, and no machine of it ever reports;
//   - "signins": two machines on account A, each with a spare for account B. "the-windows-machine" has a Director on
//                the Gateway's real tunnel, so a sign-in can be started on it; "the-mac" reports and has none. It
//                also has a WORKSTATION key, which is refused the four sign-in writes.
// Every answer the page then reads, and every one of its writes, go over HTTP through the real authentication, the
// real routes and the real display fold.
//
// THE DIRECTOR ON THE TUNNEL IS A STAND-IN, AND WHAT IT STANDS IN FOR IS SAID HERE. It is the one the Gateway's own
// host tests use (FakeTunnelDirector): a real connection to the real hub, which says Hello, sends usage reports and
// is sent commands. It runs no Claude command, opens no browser and holds no sign-in. A sign-in command the Gateway
// sends it is HELD until the command `finish ok` or `finish failed` arrives on standard input, and is then answered
// the way a Director answers one that worked, or one that failed for want of an approver. So what is real is the
// Gateway's whole side of a sign-in - the route, the row of the log, the command over the tunnel, the settling of
// the row by the answer - and what is not is the sign-in itself.
//
// No sign-in key exists anywhere in it: a report carries emails, numbers and times only, and these are made up.
// No Director of the product is started and no Claude sign-in of this machine is read or touched.
// It prints one line of JSON (the port and the keys), then answers each command line on standard input with one
// line, and stops on an empty line.
//
// Three properties and one method of the host are internal and are reached by reflection, so that the product gains
// no InternalsVisibleTo for a QA program: LicenseUsage, LicenseStore, LicenseReads and SeedEntitlementForTest.

static T Internal<T>(GatewayHost host, string property)
    => (T)(typeof(GatewayHost).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
           ?? throw new InvalidOperationException($"GatewayHost has no property {property}")).GetValue(host)!;

var root = Path.Combine(Path.GetTempPath(), "cc-license-qa-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "license-qa-token", authEnabled: true,
    instancesDirectory: Path.Combine(root, "instances"),
    workListsPath: Path.Combine(root, "worklists", "worklists.json"),
    streamMode: true,
    options: new GatewayHostOptions { Hosted = true });
await gateway.StartAsync();

var seedEntitlement = typeof(GatewayHost).GetMethod("SeedEntitlementForTest", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException("GatewayHost has no SeedEntitlementForTest");
var ingest = Internal<LicenseUsageIngest>(gateway, "LicenseUsage");
var store = Internal<LicenseStore>(gateway, "LicenseStore");
var reads = Internal<LicenseReadModel>(gateway, "LicenseReads");

(TenantId Tenant, string Subject, string BrowserKey) Enroll(string name)
{
    var subject = "sub-license-qa-" + name;
    var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, name + "@qa.example.com");
    seedEntitlement.Invoke(gateway, new object?[] { subject, "active", null, true, "hosted" });
    var key = gateway.Devices.RegisterForTenant(tenant, subject, "dev-license-qa-" + name, "QA-BROWSER", deviceType: "browser").DeviceKey;
    return (tenant, subject, key);
}

const string A = "account-a@example.com", B = "account-b@example.com", C = "account-c@example.com", D = "account-d@example.com";
const string E = "account-e@example.com";
var now = DateTime.UtcNow;
var earlier = now.AddMinutes(-20);

LicenseHolding Read(string email, bool active, double fiveHour, double week, DateTime readAt, double signInDaysLeft) => new()
{
    AccountEmail = email,
    IsActive = active,
    Plan = "max",
    RateLimitTier = "default_claude_max_20x",
    FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = now.AddHours(2).AddMinutes(10) },
    SevenDay = new LicenseWindow { Utilization = week, ResetsAtUtc = now.AddDays(4).AddHours(5) },
    ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
    ReadAtUtc = readAt,
    SignedInAtUtc = now.AddDays(signInDaysLeft - 30),
    SignInExpiresAtUtc = now.AddDays(signInDaysLeft),
};

// The same reading, from a machine whose last renewal of that sign-in failed three hours ago.
LicenseHolding RenewalFailed(LicenseHolding holding)
{
    holding.RenewalFailedAtUtc = now.AddHours(-3);
    holding.RenewalError = LicenseRenewalErrors.NoApprover;
    return holding;
}

// A sign-in the machine holds and could not read: no number at all, and the reason.
LicenseHolding NotRead(string email) => new()
{
    AccountEmail = email,
    IsActive = false,
    Plan = "max",
    ReadError = LicenseReadErrors.Network,
    ReadErrorDetail = "The usage read did not get an answer.",
    SignedInAtUtc = now.AddDays(-3),
    SignInExpiresAtUtc = now.AddDays(27),
};

void Report(TenantId tenant, string directorId, long sequence, string machine, DateTime sentAt, params LicenseHolding[] holdings)
{
    var report = new LicenseUsageReport
    {
        MachineName = machine, OsUser = "owner", SentAtUtc = sentAt, Trigger = LicenseReportTriggers.Interval, Holdings = holdings.ToList(),
    };
    var outcome = ingest.Ingest(tenant, directorId, "conn-" + directorId, sequence, report, sentAt);
    if (outcome != LicenseIngestOutcome.Accepted)
        throw new InvalidOperationException($"the report of {machine} (sequence {sequence}) was not accepted: {outcome}");
}

// Two machines, both working on account A, twenty minutes apart so the forecast has a rate. Both hold a spare for
// account B; the Windows machine also holds one for account C (at its limit) and one for account D (never read).
void SeedFleet(TenantId tenant, string name, bool withFailedRenewal)
{
    string windows = $"director-qa-{name}-windows", mac = $"director-qa-{name}-mac";
    ingest.RegisterConnection(tenant, windows, "conn-" + windows);
    ingest.RegisterConnection(tenant, mac, "conn-" + mac);
    LicenseHolding SpareB(DateTime at) => withFailedRenewal ? RenewalFailed(Read(B, false, 4, 22, at, 2.2)) : Read(B, false, 4, 22, at, 2.2);
    Report(tenant, windows, 1, "the-windows-machine", earlier, Read(A, true, 58, 40, earlier, 9), SpareB(earlier), Read(C, false, 99, 40, earlier, 29), NotRead(D));
    Report(tenant, mac, 1, "the-mac", earlier, Read(A, true, 58, 40, earlier, 10), Read(B, false, 4, 22, earlier, 25));
    Report(tenant, windows, 2, "the-windows-machine", now, Read(A, true, 64, 41, now, 9), SpareB(now), Read(C, false, 99, 40, now, 29), NotRead(D));
    Report(tenant, mac, 2, "the-mac", now, Read(A, true, 64, 41, now, 10), Read(B, false, 4, 22, now, 25));
}

var owner = Enroll("owner");
var refused = Enroll("refused");
var barred = Enroll("barred");
var empty = Enroll("empty");
// The credential of a Director of the owner's account: it reads the page and is refused both writes.
var workstationKey = gateway.Devices.RegisterForTenant(owner.Tenant, owner.Subject, "dev-license-qa-owner-workstation", "QA-WORKSTATION", deviceType: "workstation").DeviceKey;

SeedFleet(owner.Tenant, "owner", withFailedRenewal: true);
SeedFleet(refused.Tenant, "refused", withFailedRenewal: false);
SeedFleet(barred.Tenant, "barred", withFailedRenewal: false);
// Account B triggered a switch and its 5-hour window has not reset: it is left out of the automatic choice.
store.Bar(barred.Tenant, B, LicenseWindowNames.FiveHour, now.AddHours(2).AddMinutes(10), now);

// The owner's switch log: one switch that worked, at the threshold, and one asked for by hand that FAILED on the machine.
var windowsMachine = reads.Read(owner.Tenant, now).Machines.Single(m => m.MachineName == "the-windows-machine").MachineId;
void Switch(string from, string to, string why, DateTime at, string outcome, string? reasonCode, string message, bool verified)
{
    var row = store.CreatePendingSwitch(owner.Tenant, windowsMachine, from, to, why, at)
              ?? throw new InvalidOperationException("the switch row was not written: the machine is not known");
    store.RecordSend(owner.Tenant, row.SwitchId!, "director-qa-owner-windows", at.AddSeconds(1));
    var settled = store.TrySettle(owner.Tenant, new LicenseSwitchResult
    {
        SwitchId = row.SwitchId!, Outcome = outcome, ReasonCode = reasonCode, Message = message,
        PreviousActiveAccountEmail = from, ActiveAccountEmailAfter = outcome == LicenseSwitchOutcomes.Switched ? to : from,
        VerifiedByUsageRead = verified, StartedAtUtc = at.AddSeconds(1), FinishedAtUtc = at.AddSeconds(3), ExchangeMilliseconds = verified ? 41 : 0,
    }, at.AddSeconds(3));
    if (!settled) throw new InvalidOperationException($"the switch {from} -> {to} was not settled");
}
Switch(C, A, LicenseSwitchReasons.Threshold, now.AddHours(-5), LicenseSwitchOutcomes.Switched, null, $"Switched from {C} to {A}.", true);
Switch(A, D, LicenseSwitchReasons.Manual, now.AddHours(-2), LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.SpareNotUsable,
    $"The spare for {D} could not be used.", false);

// ---- "signins": the account the sign-in routes are run against ----
var signIns = Enroll("signins");
var signInsWorkstationKey = gateway.Devices.RegisterForTenant(signIns.Tenant, signIns.Subject, "dev-license-qa-signins-workstation", "QA-WORKSTATION", deviceType: "workstation").DeviceKey;

// the-mac reports through the ingest and has no Director on the tunnel.
const string signInsMac = "director-qa-signins-mac";
ingest.RegisterConnection(signIns.Tenant, signInsMac, "conn-" + signInsMac);
Report(signIns.Tenant, signInsMac, 1, "the-mac", now, Read(A, true, 30, 20, now, 25), Read(B, false, 4, 22, now, 20));

// the-windows-machine has a Director on the real tunnel, enrolled with a key of its own as a Director is.
var signInsDirectorKey = gateway.Devices.RegisterForTenant(signIns.Tenant, signIns.Subject, "dev-license-qa-signins-director", "the-windows-machine").DeviceKey;
var windowsDirector = await FakeTunnelDirector.StartAsync(gateway, signInsDirectorKey, "director-qa-signins-windows", "the-windows-machine", osUser: "owner");

Task WindowsReports(params LicenseHolding[] holdings) => windowsDirector.PushLicenseUsageAsync(new LicenseUsageReport
{
    MachineName = "the-windows-machine", OsUser = "owner", SentAtUtc = DateTime.UtcNow, Trigger = LicenseReportTriggers.Interval, Holdings = holdings.ToList(),
});
await WindowsReports(Read(A, true, 30, 20, DateTime.UtcNow, 9), Read(B, false, 4, 22, DateTime.UtcNow, 2.2));

// A sign-in command is held until the run says how it ends; every other command is refused, as a Director that
// does not know a verb refuses it.
using var finish = new SemaphoreSlim(0);
var finishAs = "ok";
windowsDirector.OnCommand(command =>
{
    if (command.Verb != LicenseContract.SignInVerb)
        return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}");
    var request = JsonSerializer.Deserialize<LicenseSignInRequest>(command.PayloadJson, FakeTunnelDirector.WebJson)
                  ?? throw new InvalidOperationException("the sign-in command carried no payload");
    if (!finish.Wait(TimeSpan.FromMinutes(4)))
        throw new InvalidOperationException("the run never said how the sign-in ends");
    var at = DateTime.UtcNow;
    return FakeTunnelDirector.Ok(finishAs == "failed"
        ? new LicenseSignInResult
        {
            SignInId = request.SignInId, Outcome = LicenseSignInOutcomes.Failed, ReasonCode = LicenseRenewalErrors.NoApprover,
            Message = $"No browser profile is set to approve {request.AccountEmail}, here or on another machine.", FinishedAtUtc = at,
        }
        : new LicenseSignInResult
        {
            SignInId = request.SignInId,
            Outcome = request.Reason == LicenseSignInReasons.Renewal ? LicenseSignInOutcomes.Renewed : LicenseSignInOutcomes.Added,
            Message = request.Reason == LicenseSignInReasons.Renewal
                ? $"Signed in to {request.AccountEmail} again."
                : $"Signed in to {request.AccountEmail} and stored as a spare.",
            SignedInAtUtc = at, SignInExpiresAtUtc = at.AddDays(29), FinishedAtUtc = at,
        });
});

Console.WriteLine(JsonSerializer.Serialize(new
{
    port = gateway.Port, ownerKey = owner.BrowserKey, workstationKey, refusedKey = refused.BrowserKey, barredKey = barred.BrowserKey,
    emptyKey = empty.BrowserKey, signInsKey = signIns.BrowserKey, signInsWorkstationKey, root,
}));
Console.Out.Flush();

for (var line = Console.In.ReadLine(); !string.IsNullOrWhiteSpace(line); line = Console.In.ReadLine())
{
    if (line.Trim() is "finish ok" or "finish failed")
    {
        // The Director of "signins" answers the sign-in it is holding.
        finishAs = line.Trim() == "finish failed" ? "failed" : "ok";
        finish.Release();
        Console.WriteLine("ok " + line.Trim());
    }
    else if (line.Trim() == "windows-holds-e")
    {
        // The machine's next usage report: it now holds a spare for account E as well.
        await WindowsReports(Read(A, true, 30, 20, DateTime.UtcNow, 9), Read(B, false, 4, 22, DateTime.UtcNow, 29), Read(E, false, 0, 3, DateTime.UtcNow, 29));
        Console.WriteLine("ok windows-holds-e");
    }
    else if (line.Trim() == "drop-windows")
    {
        // The Director of the-windows-machine loses its tunnel, as a machine that is switched off does.
        await windowsDirector.StopTunnelAsync();
        Console.WriteLine("ok drop-windows");
    }
    else if (line.Trim() == "fill-b")
    {
        // The machines of "refused" report again: account B is now at its limit on both.
        var at = DateTime.UtcNow;
        Report(refused.Tenant, "director-qa-refused-windows", 3, "the-windows-machine", at,
            Read(A, true, 65, 41, at, 9), Read(B, false, 99, 22, at, 2.2), Read(C, false, 99, 40, at, 29), NotRead(D));
        Report(refused.Tenant, "director-qa-refused-mac", 3, "the-mac", at, Read(A, true, 65, 41, at, 10), Read(B, false, 99, 22, at, 25));
        Console.WriteLine("ok fill-b");
    }
    else
    {
        Console.WriteLine("unknown command: " + line);
    }
    Console.Out.Flush();
}
await windowsDirector.DisposeAsync();
await gateway.StopAsync();
try { Directory.Delete(root, true); } catch (IOException) { /* the folder is under the temporary folder either way */ }
