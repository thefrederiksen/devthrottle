using CcDirector.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CcDirector.Gateway;

/// <summary>
/// The single shared Gateway startup entry point, called by every executable that boots the headless
/// Gateway: the local dev console host (this project, CcDirector.Gateway) and the hosted container host
/// (CcDirector.Gateway.Host). The two executables differ ONLY in which assemblies they publish - the hosted
/// host additionally references the Postgres migrations assembly so <c>Database.Migrate()</c> can load it by
/// name when the hosted Gateway runs on Postgres - and they MUST run byte-identical startup logic. So that
/// logic lives here, once, and both entry points forward their process args to <see cref="Run"/>. Nothing
/// about the <c>--port</c>/<c>--help</c> parsing, the hosted platform-port resolution, or the worker wiring
/// is duplicated across the two hosts; a change here changes both.
/// </summary>
public static class GatewayEntryPoint
{
    /// <summary>
    /// Boot the Gateway and block until it exits: parse the args (<c>--port N</c>, <c>--help</c>), resolve
    /// the platform-assigned port when running hosted (Azure App Service <c>WEBSITES_PORT</c>/<c>PORT</c>),
    /// build the generic host with the <see cref="GatewayWorker"/>, and run it. Returns the process exit
    /// code. This is the exact logic the dev console host has always run - it is shared, not reimplemented,
    /// so the hosted container host cannot drift from it.
    /// </summary>
    public static int Run(string[] args)
    {
        // A hosted Gateway logs differently from a desktop one, and it must, because a deploy runs TWO
        // Gateway containers at once and they share one storage mount.
        //
        //  - Its own log file. The default discriminator is the process id, which is 1 in EVERY container,
        //    so both containers computed the same path on the Server Message Block mount and clobbered each
        //    other's records mid-line. The file now lives on the container's temporary disk as well: process
        //    logging is not durable workload state, and putting it on the shared mount turns every routine
        //    line into billed network storage traffic. Both settings must be applied before FileLog.Start().
        //  - Standard output as a second sink. The platform captures it per container, and it has no queue
        //    in front of it, so it survives a stalled file share - the failure that erased the startup
        //    record of three deploys and left "no listening ports were detected" with nothing to explain it.
        //
        // Keyed on the IMMUTABLE hosted image identity, not the CC_GATEWAY_HOSTED toggle: a slot swap or a
        // config restore can drop the environment variable, and the moment it does is exactly when two
        // containers are running and the logging matters most.
        if (GatewayHostedMode.IsHostedImage)
        {
            FileLog.UseUniqueInstanceId(ResolveHostedLogDirectory(Path.GetTempPath()));
            FileLog.MirrorToConsole = true;
        }

        FileLog.Start();
        FileLog.Write($"[Program] CC Director Gateway starting, log: {FileLog.CurrentLogPath}");
        // This is the real Gateway process, so its own failure lines go into the error store as well as this log, which
        // on hosted is lost on every deploy (the Error Logging mission, issue #3675).
        Api.GatewayErrorSink.AttachToProcessLog = true;
        if (GatewayHostedMode.IsHostedImage)
        {
            // Correlation, not identity: the platform names containers by their id, so record what this
            // process can see about which container it is. The log file name does not depend on any of it.
            FileLog.Write(
                $"[Program] hosted container: machine={Environment.MachineName} " +
                $"pid={Environment.ProcessId} instance={Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "<unset>"}");
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

        // Fail-CLOSED hosted identity (production-readiness item MH-3). BEFORE anything else boots, if this
        // executable is the HOSTED image (the immutable compiled-in marker, not the droppable
        // CC_GATEWAY_HOSTED toggle), it must prove the full hosted contract - hosted mode on, auth enabled,
        // HTTPS public URL, PostgreSQL provider - or REFUSE to run. A hosted deployment that lost a variable
        // must crash the boot, never silently downgrade to Local single-tenant / no-auth semantics. This is a
        // no-op on every non-hosted-image build (desktop tray, dev console host, tests), so self-host is
        // unaffected. A distinct non-zero exit code (2) marks a contract refusal apart from a generic fault.
        try
        {
            HostedStartupContract.AssertFromEnvironment();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            FileLog.Write($"[Program] HOSTED CONTRACT REFUSED: {ex.Message.Replace(Environment.NewLine, " | ")}");
            FileLog.Stop();
            return 2;
        }

        // Hosted (Azure App Service): honor the platform's assigned port (WEBSITES_PORT, else PORT), so the
        // front-end can route to the container. Falls through to the --port value when unset. Applied HERE,
        // before the port flows into the Gateway, so the ONE port value is consistent everywhere - the
        // all-interfaces listener bind AND every loopback self-call the Gateway makes to itself. The bind is
        // 0.0.0.0 in both modes; only the port source differs when hosted.
        if (GatewayHostedMode.IsHosted)
        {
            var hostedPort = GatewayHostedMode.ResolveHostedPort(port);
            if (hostedPort != port)
                FileLog.Write($"[Program] HOSTED mode: listening on the platform port {hostedPort} (was {port})");
            port = hostedPort;
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
    }

    /// <summary>
    /// Locate a hosted process log beneath the container's temporary root. Azure App Service keeps paths
    /// outside <c>/home</c> and explicitly mounted storage on local host disk, so this path cannot fall back
    /// into the Gateway's durable data share. The root is supplied to make that boundary directly testable.
    /// </summary>
    internal static string ResolveHostedLogDirectory(string temporaryRoot)
    {
        if (string.IsNullOrWhiteSpace(temporaryRoot))
            throw new ArgumentException("A temporary root is required.", nameof(temporaryRoot));

        return Path.Combine(Path.GetFullPath(temporaryRoot), "devthrottle", "logs", "director");
    }
}
