using System.Reflection;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;

// A REAL GATEWAY WITH SEEDED LICENSE DATA, FOR THE LICENSES PAGE TO BE RUN AGAINST (License Switching, phase 3).
//
// It starts the product's own GatewayHost in hosted mode on a loopback port the operating system picks, with its
// whole storage under a throwaway folder (CC_DIRECTOR_ROOT), so nothing of the machine's real Gateway is read or
// written. Two accounts are enrolled the way the Gateway's own host tests enroll them, each with a browser key:
//   - "owner": two machines whose usage reports go in through the Gateway's own ingest, and a switch log;
//   - "empty": enrolled, and no machine of it ever reports.
// Every answer the page then reads comes over HTTP through the real authentication and the real display fold.
//
// No sign-in key exists anywhere in it: a report carries emails, numbers and times only, and these are made up.
// It prints one line of JSON (the port and the two browser keys) and runs until a line arrives on standard input.
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

(TenantId Tenant, string BrowserKey) Enroll(string name)
{
    var subject = "sub-license-qa-" + name;
    var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, name + "@qa.example.com");
    seedEntitlement.Invoke(gateway, new object?[] { subject, "active", null, true, "hosted" });
    var key = gateway.Devices.RegisterForTenant(tenant, subject, "dev-license-qa-" + name, "QA-BROWSER", deviceType: "browser").DeviceKey;
    return (tenant, key);
}

var owner = Enroll("owner");
var empty = Enroll("empty");

const string A = "account-a@example.com", B = "account-b@example.com", C = "account-c@example.com", D = "account-d@example.com";
var now = DateTime.UtcNow;

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

void Report(string directorId, long sequence, string machine, DateTime sentAt, params LicenseHolding[] holdings)
{
    var report = new LicenseUsageReport
    {
        MachineName = machine, OsUser = "owner", SentAtUtc = sentAt, Trigger = LicenseReportTriggers.Interval, Holdings = holdings.ToList(),
    };
    var outcome = ingest.Ingest(owner.Tenant, directorId, "conn-" + directorId, sequence, report, sentAt);
    if (outcome != LicenseIngestOutcome.Accepted)
        throw new InvalidOperationException($"the report of {machine} (sequence {sequence}) was not accepted: {outcome}");
}

ingest.RegisterConnection(owner.Tenant, "director-qa-windows", "conn-director-qa-windows");
ingest.RegisterConnection(owner.Tenant, "director-qa-mac", "conn-director-qa-mac");
var earlier = now.AddMinutes(-20);
// Twenty minutes apart, so the forecast has a rate: account A rises on its 5-hour window.
Report("director-qa-windows", 1, "the-windows-machine", earlier,
    Read(A, true, 58, 40, earlier, 9), Read(B, false, 4, 22, earlier, 2.2), Read(C, false, 99, 40, earlier, 29), NotRead(D));
Report("director-qa-mac", 1, "the-mac", earlier, Read(A, true, 58, 40, earlier, 10), Read(B, false, 4, 22, earlier, 25));
Report("director-qa-windows", 2, "the-windows-machine", now,
    Read(A, true, 64, 41, now, 9), Read(B, false, 4, 22, now, 2.2), Read(C, false, 99, 40, now, 29), NotRead(D));
Report("director-qa-mac", 2, "the-mac", now, Read(A, true, 64, 41, now, 10), Read(B, false, 4, 22, now, 25));

// The switch log: one switch that worked, at the threshold, and one asked for by hand that FAILED on the machine.
var windows = reads.Read(owner.Tenant, now).Machines.Single(m => m.MachineName == "the-windows-machine").MachineId;
void Switch(string from, string to, string why, DateTime at, string outcome, string? reasonCode, string message, bool verified)
{
    var row = store.CreatePendingSwitch(owner.Tenant, windows, from, to, why, at)
              ?? throw new InvalidOperationException("the switch row was not written: the machine is not known");
    store.RecordSend(owner.Tenant, row.SwitchId!, "director-qa-windows", at.AddSeconds(1));
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

Console.WriteLine(JsonSerializer.Serialize(new { port = gateway.Port, ownerKey = owner.BrowserKey, emptyKey = empty.BrowserKey, root }));
Console.Out.Flush();
Console.In.ReadLine();
await gateway.StopAsync();
try { Directory.Delete(root, true); } catch (IOException) { /* the folder is under the temporary folder either way */ }
