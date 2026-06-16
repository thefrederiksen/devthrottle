using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

FileLog.Start();
FileLog.Write($"[Program] CC Director Gateway (dev console host) starting, log: {FileLog.CurrentLogPath}");

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
        Console.WriteLine("CC Director Gateway (dev console host)");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run [--port N]");
        Console.WriteLine();
        Console.WriteLine($"  --port N    Listen on 0.0.0.0:N (default {GatewayHost.DefaultPort})");
        Console.WriteLine();
        Console.WriteLine("This console host is the DEV loop only (Ctrl+C to stop). The shipped");
        Console.WriteLine("Gateway is the tray app (CcDirector.GatewayApp -> devthrottle-gateway.exe),");
        Console.WriteLine("which starts at logon and runs in the user's session.");
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

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new GatewayWorker(port));
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayWorker>());

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
