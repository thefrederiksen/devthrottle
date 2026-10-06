// One Director's skill path, run for real: the network half (SkillStoreRefresh) then the launch half
// (SkillDirectoryInstaller.InstallFor), exactly as the Director runs them - each reading THIS process's own
// storage home (CC_DIRECTOR_ROOT): its gateway config, its team file, its skill store.
//
// The Gateway is a stub served BY THIS PROCESS on its configured local port, for as long as this run lasts:
// an HttpListener on http://localhost:<port>/ answering the two skill routes from <rig>/gateways.json, keyed
// by the bearer key the Director sends. The port is checked free first, the address is refused unless it is
// localhost, and the listener stops before the process exits - so nothing is left running.
//
// Usage: SkillsRig <label>      (environment set by run-live-proof.ps1; refused if it is not the rig's)
using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Skills;
using CcDirector.Core.Storage;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

var label = args.Length > 0 ? args[0] : throw new ArgumentException("usage: SkillsRig <label>");

// ---- 1. The environment, checked BEFORE anything reads a config or writes a file. ----
var rigRoot = Environment.GetEnvironmentVariable("RIG_ROOT");
if (string.IsNullOrWhiteSpace(rigRoot) || !Path.IsPathFullyQualified(rigRoot))
    return Refuse("RIG_ROOT is not set to an absolute path");
rigRoot = Path.GetFullPath(rigRoot).TrimEnd('\\') + "\\";

var checks = new List<(string What, string Value)>
{
    ("CC_DIRECTOR_ROOT", Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT") ?? ""),
    ("USERPROFILE", Environment.GetEnvironmentVariable("USERPROFILE") ?? ""),
    ("HOME", Environment.GetEnvironmentVariable("HOME") ?? ""),
    ("LOCALAPPDATA", Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ""),
    ("APPDATA", Environment.GetEnvironmentVariable("APPDATA") ?? ""),
    ("CcStorage.Root()", CcStorage.Root()),
    ("CcStorage.Config()", CcStorage.Config()),
    ("log folder", CcStorage.ToolLogs("director")),
};
var lines = new List<string> { $"[{label}] environment check, before anything else runs:" };
foreach (var (what, value) in checks)
{
    var inside = value.Length > 0 && Path.GetFullPath(value).StartsWith(rigRoot, StringComparison.OrdinalIgnoreCase);
    lines.Add($"  {(inside ? "OK  " : "FAIL")} {what} = {value}");
    if (!inside)
        return Refuse($"{what} is not inside the rig ({value})", lines);
}
// WINDOWS IGNORES USERPROFILE for the home folder: .NET asks the shell's known-folder API, so the path table
// SkillInstallTargets.For builds would name the OWNER's real folders whatever this process's environment says.
// Found by this check on the first run. So the two folders are passed explicitly - the same two paths that
// table builds for Claude Code, from the rig's home - and this is the ONE argument that differs from the
// Director's own call. Shown, so it is never mistaken for the real home.
var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var rigHome = Environment.GetEnvironmentVariable("USERPROFILE")!;
var paths = new SkillInstallPaths(Path.Combine(rigHome, ".agents", "skills"), Path.Combine(rigHome, ".claude", "skills"));
lines.Add($"  NOTE home as .NET resolves it = {realHome} (ignores USERPROFILE on Windows) - NOT used");
foreach (var (what, value) in new[] { ("skills folder (shared)", paths.SharedRoot), ("skills folder (Claude Code)", paths.LinkRoot!) })
{
    var inside = Path.GetFullPath(value).StartsWith(rigRoot, StringComparison.OrdinalIgnoreCase);
    lines.Add($"  {(inside ? "OK  " : "FAIL")} {what} = {value}");
    if (!inside)
        return Refuse($"{what} is not inside the rig ({value})", lines);
}
var ccNames = Environment.GetEnvironmentVariables().Keys.Cast<string>()
    .Where(k => k.StartsWith("CC_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
lines.Add($"  CC_* variables present: {string.Join(", ", ccNames)}");
if (ccNames.Any(k => !string.Equals(k, "CC_DIRECTOR_ROOT", StringComparison.OrdinalIgnoreCase)))
    return Refuse("a CC_* variable other than CC_DIRECTOR_ROOT is set", lines);
lines.Add("  CC_VAULT_PATH absent: OK");

// The same check is the first thing in this Director's own log.
FileLog.Start();
foreach (var line in lines)
{
    Console.WriteLine(line);
    FileLog.Write(line);
}

// ---- 2. Who this Director is: its own config and its own team file. ----
var config = GatewayConfig.Load();
var team = DirectorTeamStore.Load();
Console.WriteLine($"[{label}] gateway.url = {config.Url}; team = {(team is null ? "(no file: personal)" : team.IsPersonal ? "personal" : team.TeamId)}");

// ---- 3. The stub Gateway this Director's config names: local only, on a port checked free. ----
var gatewayUri = new Uri(config.Url);
if (!string.Equals(gatewayUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    return Refuse($"the configured Gateway {config.Url} is not localhost");
var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, gatewayUri.Port);
probe.Start();
probe.Stop();
Console.WriteLine($"[{label}] port {gatewayUri.Port} checked free on the loopback address");
var gateways = JsonSerializer.Deserialize<Dictionary<string, StubLibrary>>(
    File.ReadAllText(Path.Combine(rigRoot, "gateways.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
using var stub = new StubGateway(gatewayUri.Port, gateways);
Console.WriteLine($"[{label}] stub Gateway serving http://localhost:{gatewayUri.Port}/gateway/skills for this run only");

// ---- 4. The network half, called exactly as the Director calls it (ControlApiHost.cs:457). ----
var count = await new SkillStoreRefresh().RefreshAsync().ConfigureAwait(false);
Console.WriteLine($"[{label}] store refreshed: {count} skill(s) at {SkillDirectoryInstaller.StoreRoot()}");

// ---- 5. The launch half, as the Director calls it (SessionManager.cs:1056) for Claude Code, with ONE argument
// added: the rig's two skill folders in place of the path table's (see the check above). The store and the
// source are NOT overridden - both come from this Director's own storage home, as in the Director.
var placement = CcDirector.Core.Skills.SkillDirectoryInstaller.InstallFor(AgentKind.ClaudeCode, pathsOverride: paths);
Console.WriteLine($"[{label}] {placement.Describe()}");
foreach (var p in placement.Problems)
    Console.WriteLine($"[{label}]   problem: {p.SkillId} {p.Fault} in {p.Target}");
FileLog.Stop();
return 0;

static int Refuse(string why, List<string>? lines = null)
{
    foreach (var line in lines ?? new List<string>())
        Console.WriteLine(line);
    Console.WriteLine($"REFUSED: {why}. Nothing was read or written.");
    return 2;
}

/// <summary>What one Director key is served: skill id to the body it carries.</summary>
sealed class StubLibrary
{
    public Dictionary<string, string> Skills { get; set; } = new();
}

/// <summary>
/// The two skill routes, answered from the rig's gateways.json by the caller's bearer key, on localhost only,
/// for the life of this object.
/// </summary>
sealed class StubGateway : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, StubLibrary> _byKey;
    private readonly Task _loop;

    public StubGateway(int port, Dictionary<string, StubLibrary> byKey)
    {
        _byKey = byKey;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            Answer(context);
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        var auth = context.Request.Headers["Authorization"] ?? "";
        var key = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..] : "";
        Console.WriteLine($"    stub: GET {path} (key ...{(key.Length > 4 ? key[^4..] : key)})");
        object? body = null;
        if (_byKey.TryGetValue(key, out var library) && path.StartsWith("/gateway/skills", StringComparison.Ordinal))
        {
            var rest = path["/gateway/skills".Length..].Trim('/');
            if (rest.Length == 0)
            {
                body = new { skills = library.Skills.Select(s => new { id = s.Key, version = 1, enabled = true, contentHash = Hash(s.Value) }) };
            }
            else
            {
                var id = Uri.UnescapeDataString(rest.Split('/')[0]);
                if (library.Skills.TryGetValue(id, out var text))
                    body = new
                    {
                        version = 1, summary = $"Rig skill {id}.", triggers = new[] { id }, bodyMarkdown = $"# {id}\n\n{text}\n",
                        files = Array.Empty<object>(), contentHash = Hash(text),
                    };
            }
        }
        context.Response.StatusCode = body is null ? 404 : 200;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes);
        }
        context.Response.Close();
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
