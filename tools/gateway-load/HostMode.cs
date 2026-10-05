using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;

namespace CcDirector.GatewayLoad;

/// <summary>
/// "host": run a private HOSTED Gateway on a throwaway folder, enroll simulated accounts, and sample this process's
/// memory every few seconds into memory.csv until stop.txt appears in the run folder. Only the Gateway lives in this
/// process, so what is sampled is the Gateway's memory and nothing of the load generator's.
/// </summary>
internal static class HostMode
{
    public const string AccountsFile = "accounts.json";
    public const string MemoryFile = "memory.csv";
    public const string StopFile = "stop.txt";
    public const string CollectFile = "collect.txt";
    public const string CollectedFile = "collected.csv";

    /// <summary>Settings that would connect the Gateway to something real (the database, the stats store, the public
    /// address and the database provider). Kept in step with run-load.ps1.</summary>
    public static readonly string[] RefusedSettings =
    [
        "CC_GATEWAY_DB_CONNECTION", "CC_GATEWAY_STATS_DB_CONNECTION", "CC_GATEWAY_PUBLIC_URL", "CC_GATEWAY_EF_PROVIDER",
    ];

    public static async Task<int> RunAsync(string runDir, int accounts, int directorsPerAccount, TimeSpan sampleEvery)
    {
        // The data root must be THIS run's, set before the process started, so nothing reads or writes the machine's
        // real DevThrottle folder. Checked, never assumed.
        var root = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var expectedRoot = Path.GetFullPath(Path.Combine(runDir, "root"));
        if (string.IsNullOrWhiteSpace(root) || !string.Equals(Path.GetFullPath(root), expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"ERROR: CC_DIRECTOR_ROOT must be set to {expectedRoot} before starting the host. " +
                                    "Use run-load.ps1, which sets it.");
            return 2;
        }
        if (Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED") != "1")
        {
            Console.Error.WriteLine("ERROR: CC_GATEWAY_HOSTED must be 1, so the Gateway runs as the hosted service does. Use run-load.ps1.");
            return 2;
        }
        // These would point the Gateway at a real database or a real address. None belongs in a load run, so their
        // presence is refused rather than ignored (run-load.ps1 clears them for the host it starts).
        foreach (var name in RefusedSettings)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Console.Error.WriteLine($"ERROR: {name} is set. A load run must not reach a real database or address; " +
                                        "clear it, or use run-load.ps1, which clears it for the host.");
                return 2;
            }
        }
        Directory.CreateDirectory(root);
        // The Gateway's own log goes into the run folder, never the machine's real log folder.
        FileLog.UseLogDirectory(Path.Combine(runDir, "logs"), "gateway");
        FileLog.Start();

        var instances = Path.Combine(runDir, "instances");
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: token, authEnabled: true,
            instancesDirectory: instances,
            workListsPath: Path.Combine(instances, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instances, "snooze", "snooze.json"),
            streamMode: true);
        await gateway.StartAsync();
        Console.WriteLine($"[host] Gateway listening on port {gateway.Port}");

        var enrolled = new List<LoadAccount>();
        for (var i = 0; i < accounts; i++)
        {
            var subject = $"load-user-{i:D3}";
            var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, $"{subject}@load.invalid");
            var directors = new List<string>();
            for (var d = 0; d < directorsPerAccount; d++)
                directors.Add(gateway.Devices.RegisterForTenant(tenant, subject, $"{subject}-dir-{d}", $"LOAD{i:D3}D{d}",
                    deviceType: "workstation").DeviceKey);
            var phone = gateway.Devices.RegisterForTenant(tenant, subject, $"{subject}-phone", $"LOAD{i:D3}P",
                deviceType: "phone").DeviceKey;
            enrolled.Add(new LoadAccount(subject, tenant.Value, directors, phone));
        }
        File.WriteAllText(Path.Combine(runDir, AccountsFile),
            JsonSerializer.Serialize(new LoadAccounts(gateway.Port, enrolled), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[host] enrolled {accounts} account(s), {directorsPerAccount} Director(s) and a phone each");

        var csv = Path.Combine(runDir, MemoryFile);
        File.WriteAllText(csv, "utc,working_set_mb,private_mb,gc_heap_mb,gc_committed_mb\n");
        var stop = Path.Combine(runDir, StopFile);
        var collect = Path.Combine(runDir, CollectFile);
        while (!File.Exists(stop))
        {
            File.AppendAllText(csv, Sample() + "\n");
            if (File.Exists(collect))
            {
                // Asked for by the driver at the end of its hold, while the accounts are still connected: one sample
                // after a full, compacting collection, so the figure is LIVE memory with no garbage in it. The
                // regular samples force nothing, so they are an upper bound.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                File.WriteAllText(Path.Combine(runDir, CollectedFile),
                    "utc,working_set_mb,private_mb,gc_heap_mb,gc_committed_mb\n" + Sample() + "\n");
                File.Delete(collect);
            }
            await Task.Delay(sampleEvery);
        }
        Console.WriteLine("[host] stop requested");
        await gateway.StopAsync();
        FileLog.Stop();
        return 0;
    }

    /// <summary>One memory sample of this process. The heap figure is what .NET reports as of its most recent
    /// collection of any generation, so between full collections it includes garbage.</summary>
    private static string Sample()
    {
        using var self = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(',', DateTime.UtcNow.ToString("o"), Mb(self.WorkingSet64), Mb(self.PrivateMemorySize64),
            Mb(info.HeapSizeBytes), Mb(info.TotalCommittedBytes));
    }
}

/// <summary>One simulated account: its subject, tenant, one device key per Director, and its phone's key.</summary>
internal sealed record LoadAccount(string Subject, string Tenant, List<string> DirectorKeys, string PhoneKey);

/// <summary>What the host hands the driver.</summary>
internal sealed record LoadAccounts(int Port, List<LoadAccount> Accounts);
