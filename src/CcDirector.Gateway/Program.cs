using CcDirector.Core.Utilities;
using CcDirector.Gateway;

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
        Console.WriteLine($"  --port N    Listen on 127.0.0.1:N (default {GatewayHost.DefaultPort})");
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

await using var host = new GatewayHost(port);

try
{
    await host.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Gateway failed to start: {ex.Message}");
    FileLog.Write($"[Program] FAILED: {ex.Message}");
    return 1;
}

Console.WriteLine($"CC Director Gateway");
Console.WriteLine($"  URL:   http://127.0.0.1:{host.Port}/");
Console.WriteLine($"  Token: {host.Token}");
Console.WriteLine($"  Log:   {FileLog.CurrentLogPath}");
Console.WriteLine();
Console.WriteLine($"Press Ctrl+C to stop.");

// Block until Ctrl+C
var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.TrySetResult();

await stopped.Task;

Console.WriteLine("Stopping gateway...");
await host.StopAsync();
FileLog.Stop();
return 0;
