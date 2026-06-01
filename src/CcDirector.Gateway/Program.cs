using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

FileLog.Start();
FileLog.Write($"[Program] CC Director Gateway starting, log: {FileLog.CurrentLogPath}");

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
