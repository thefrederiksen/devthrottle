using CcDirector.GatewayLoad;

// gateway-load: Money Saver, item 3. Two processes, so the Gateway's memory is measured without the load generator's:
//   gateway-load host  --run <dir> --accounts <n> [--directors 3] [--sample-seconds 5]
//   gateway-load drive --run <dir> --profile fleet|light --count <n> --minutes <m> [--today]
// run-load.ps1 runs the whole step series. Never point either half at production: the host only ever starts its own
// private Gateway, and the driver only ever talks to the port that host wrote.

static string Arg(string[] args, string name, string? fallbackForOptional = null)
{
    var i = Array.IndexOf(args, name);
    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    return fallbackForOptional ?? throw new ArgumentException($"missing {name}");
}

if (args.Length == 0 || (args[0] != "host" && args[0] != "drive"))
{
    Console.Error.WriteLine("usage: gateway-load host --run <dir> --accounts <n> [--directors 3] [--sample-seconds 5]");
    Console.Error.WriteLine("       gateway-load drive --run <dir> --profile fleet|light --count <n> --minutes <m> [--today]");
    return 2;
}

try
{
    var run = Path.GetFullPath(Arg(args, "--run"));
    Directory.CreateDirectory(run);
    if (args[0] == "host")
        return await HostMode.RunAsync(run, int.Parse(Arg(args, "--accounts")), int.Parse(Arg(args, "--directors", "3")),
            TimeSpan.FromSeconds(int.Parse(Arg(args, "--sample-seconds", "5"))));

    var released = !args.Contains("--today");
    var profile = Arg(args, "--profile") switch
    {
        "fleet" => LoadProfile.Fleet(released),
        "light" => LoadProfile.Light(released),
        var other => throw new ArgumentException($"unknown profile '{other}' (fleet or light)"),
    };
    return await DriveMode.RunAsync(run, profile, int.Parse(Arg(args, "--count")),
        TimeSpan.FromMinutes(double.Parse(Arg(args, "--minutes"), System.Globalization.CultureInfo.InvariantCulture)));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}
