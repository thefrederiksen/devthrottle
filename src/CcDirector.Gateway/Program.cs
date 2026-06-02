using System.Diagnostics;
using System.Net.Http;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Setup.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

FileLog.Start();
FileLog.Write($"[Program] CC Director Gateway starting, log: {FileLog.CurrentLogPath}");

// Detached self-update helper mode: this process is a STAGED copy of the new Gateway exe. It swaps
// itself into the installed location and verifies the new build is healthy, rolling back to the .old
// build (and pinning the bad version) if not. NEVER the normal startup path - it stops/starts the
// service and exits. Launched by GatewayUpdater.LaunchDetachedUpdater. --service defaults to the live
// service; tests pass a throwaway name so the live cc-gateway-service is never touched.
if (Array.IndexOf(args, "--apply-service-update") >= 0)
{
    string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
    var suTarget = Arg("--target");
    var suVersion = Arg("--new-version");
    var suService = Arg("--service");
    if (suService.Length == 0) suService = GatewayServiceCommands.ServiceName;
    var suPort = int.TryParse(Arg("--port"), out var pp) ? pp : GatewayHost.DefaultPort;
    var stagedSelf = Environment.ProcessPath ?? "";
    FileLog.Write($"[Program] --apply-service-update: version={suVersion}, target={suTarget}, service={suService}, port={suPort}");

    using var suHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var su = await new GatewaySelfUpdate().ApplyAsync(
        suTarget, stagedSelf, suVersion,
        () => RunSc($"stop {suService}"),
        () => RunSc($"start {suService}"),
        async c => { try { return (await suHttp.GetAsync($"http://127.0.0.1:{suPort}/healthz", c)).IsSuccessStatusCode; } catch { return false; } },
        TimeSpan.FromSeconds(30));

    FileLog.Write($"[Program] self-update outcome={su.Outcome}: {su.Message}");
    foreach (var step in su.Steps) FileLog.Write($"[Program]   {step}");
    FileLog.Stop();
    return su.Outcome == SelfUpdateOutcome.Updated ? 0 : 1;

    static bool RunSc(string scArgs)
    {
        var psi = new ProcessStartInfo("sc.exe", scArgs)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit();
        return true; // best-effort; the health probe is the real verdict
    }
}

int port = GatewayHost.DefaultPort;

// Trivial CLI parsing: --port N
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
    {
        port = p;
        i++;
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        Console.WriteLine("CC Director Gateway");
        Console.WriteLine();
        Console.WriteLine("Usage: cc-director-gateway [--port N]");
        Console.WriteLine();
        Console.WriteLine($"  --port N    Listen on 0.0.0.0:N (default {GatewayHost.DefaultPort})");
        Console.WriteLine();
        Console.WriteLine("Runs as the 'cc-gateway-service' Windows service when launched by the");
        Console.WriteLine("Service Control Manager; runs as a console app otherwise (Ctrl+C to stop).");
        Console.WriteLine();
        Console.WriteLine("Endpoints:");
        Console.WriteLine("  GET    /healthz");
        Console.WriteLine("  GET    /directors");
        Console.WriteLine("  POST   /directors          (auth)");
        Console.WriteLine("  DELETE /directors/{id}     (auth)");
        Console.WriteLine("  GET    /sessions");
        Console.WriteLine("  GET    /sessions/{sid}");
        Console.WriteLine("  GET    /sessions/{sid}/buffer");
        Console.WriteLine("  POST   /sessions/{sid}/prompt    (auth)");
        Console.WriteLine("  POST   /sessions/{sid}/interrupt (auth)");
        return 0;
    }
}

// Generic host so the process talks to the Windows Service Control Manager when run as a service.
// AddWindowsService is a no-op outside a service context, so dev `dotnet run` is unchanged and the
// console lifetime still handles Ctrl+C.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "cc-gateway-service");
builder.Services.AddSingleton(new GatewayWorker(port));
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayWorker>());

if (WindowsServiceHelpers.IsWindowsService())
    FileLog.Write("[Program] running as Windows service 'cc-gateway-service'");

try
{
    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Gateway failed: {ex.Message}");
    FileLog.Write($"[Program] FAILED: {ex.Message}");
    FileLog.Stop();
    return 1;
}

FileLog.Stop();
return 0;
