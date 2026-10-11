// A stand-in Director for the Factory Manager QA rig. It dials the rig Gateway's Director stream as a real Director
// does (Hello, then a session snapshot), answers the "create" command the Gateway sends when a talk is started, and
// writes the create's payload - the session name and the first prompt - to the output folder, so the QA report can
// show exactly what left the Gateway. For each session it also mints a session key and registers its hash, as a real
// Director does, and writes the raw key beside the create, so the rig can run `cc-devthrottle` AS that session.
// It never starts a real session.
//
// Usage: StandinDirector <gateway url> <machine name> <output folder> <device key>
// It runs until its standard input closes; the rig that started it stops it.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: StandinDirector <gateway url> <machine name> <output folder> <device key>");
    return 2;
}

var gateway = args[0].TrimEnd('/');
var machine = args[1];
var outDir = args[2];
var deviceKey = args[3];
Directory.CreateDirectory(outDir);
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
const string directorId = "qa-standin-director";
var sessions = new List<SessionDto>();
var keys = new List<SessionKeyRegistration>();
long sequence = 0;

// The workstation device key the rig enrolled for it, sent the way a real Director sends its own.
var connection = new HubConnectionBuilder()
    .WithUrl(gateway + "/director-stream", options => options.AccessTokenProvider = () => Task.FromResult<string?>(deviceKey))
    .Build();

connection.On<DirectorCommand, DirectorCommandResult>("Command", command =>
{
    Console.WriteLine($"[standin] command verb={command.Verb} id={command.CommandId}");
    // The Gateway also sends housekeeping (display state, roles) to a Director; the stand-in has nothing to apply.
    if (command.Verb != "create")
        return new DirectorCommandResult { CommandId = command.CommandId, Status = DirectorCommandStatus.Ok };

    var request = JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson, web)
                  ?? throw new InvalidOperationException("the create carried no payload");
    var n = Directory.GetFiles(outDir, "create-*.json").Length + 1;
    File.WriteAllText(Path.Combine(outDir, $"create-{n}.json"), JsonSerializer.Serialize(request, web));
    File.WriteAllText(Path.Combine(outDir, $"create-{n}-first-prompt.txt"), request.PrePrompt ?? "");

    var session = new SessionDto
    {
        SessionId = Guid.NewGuid().ToString(),
        DirectorId = directorId,
        Name = request.Name ?? "",
        RepoPath = request.RepoPath,
        Agent = request.Agent ?? "ClaudeCode",
        MachineName = machine,
        Factory = request.Factory,
        Status = "Running",
        ActivityState = "Working",
        CreatedAt = DateTime.UtcNow,
    };
    sessions.Add(session);

    // The session's own key: the raw value stays here (and in the rig's folder), the hash goes to the Gateway.
    var key = "qa-session-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    keys.Add(new SessionKeyRegistration
    {
        SessionId = session.SessionId,
        KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant(),
        ExpiresAtUtc = DateTime.UtcNow.AddHours(2),
    });
    File.WriteAllText(Path.Combine(outDir, $"create-{n}.session.txt"), session.SessionId + "\n" + key + "\n");
    Console.WriteLine($"[standin] created {session.SessionId} name='{session.Name}' (written to create-{n}.json)");
    return new DirectorCommandResult
    {
        CommandId = command.CommandId,
        Status = DirectorCommandStatus.Ok,
        BodyJson = JsonSerializer.Serialize(session, web),
    };
});

await connection.StartAsync();
await connection.InvokeAsync<object?>("Hello", new DirectorStreamHello
{
    DirectorId = directorId,
    Version = "9.9.9-qa",
    MachineName = machine,
    User = "qa",
    Pid = Environment.ProcessId,
});
await connection.InvokeAsync("PushSnapshot", ++sequence, sessions.ToArray());
Console.WriteLine($"[standin] connected to {gateway} as {directorId} on {machine}");

// Keep pushing what it holds, and registering each session's key, until stdin closes.
var registered = 0;
var stop = Task.Run(() => Console.In.ReadToEnd());
while (!stop.IsCompleted)
{
    await Task.WhenAny(stop, Task.Delay(1000));
    await connection.InvokeAsync("PushSnapshot", ++sequence, sessions.ToArray());
    while (registered < keys.Count)
    {
        await connection.InvokeAsync("RegisterSessionKey", keys[registered]);
        Console.WriteLine($"[standin] registered the key of {keys[registered].SessionId}");
        registered++;
    }
}
await connection.StopAsync();
Console.WriteLine("[standin] stopped");
return 0;
