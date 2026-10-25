using System.Text;
using System.Text.Json;
using Avalonia.Input;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.DeliveryQualification;

/// <summary>Measures the new remote key and paste dispatches against the shipped desktop terminal control.</summary>
internal static class RemoteRouteEcho
{
    public static async Task<int> RunAsync(SessionManager manager, Options options)
    {
        var repo = Rig.EnsureRepo(Path.Combine(options.RepoRoot, "remote-route-echo"));
        var log = Path.Combine(options.Out, "remote-route-echo.jsonl");
        var (executable, arguments) = EchoLayer.EchoCommand(log);
        var session = manager.CreateSession(repo, new RawCliAgent(executable, arguments), null, SessionBackendType.ConPty, null);
        using var desktop = new DesktopHost();
        try
        {
            await EchoLayer.WaitForScreenAsync(() => session.SnapshotScreenRows(), "echo-agent ready", TimeSpan.FromSeconds(30));
            await desktop.AttachAsync(session);

            var desktopKey = await CaptureAsync(log, async () => await desktop.KeyAsync(Key.Down));
            var remoteKey = await CaptureAsync(log, async () => await DispatchAsync(manager, session, "terminal-keys",
                new TerminalKeyRequest { Key = "Down" }));
            var pasteText = "remote route paste " + EchoLayer.Token();
            var desktopPaste = await CaptureAsync(log, async () => await desktop.PasteAsync(pasteText));
            var remotePaste = await CaptureAsync(log, async () => await DispatchAsync(manager, session, "terminal-paste",
                new TerminalPasteRequest { Text = pasteText }));

            var proof = new RemoteRouteEchoProof(ToHex(desktopKey), ToHex(remoteKey), ToHex(desktopPaste), ToHex(remotePaste));
            var path = Path.Combine(options.Out, "remote-route-echo-proof.json");
            File.WriteAllText(path, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine($"[remote-route-echo] proof: {path}");
            Console.WriteLine($"[remote-route-echo] keys: {proof.DesktopKeys}");
            Console.WriteLine($"[remote-route-echo] paste: {proof.DesktopPaste}");

            if (proof.DesktopKeys != proof.RemoteKeys || proof.DesktopPaste != proof.RemotePaste)
            {
                Console.Error.WriteLine("[remote-route-echo] FAIL: desktop and remote bytes differ; see the proof file.");
                return 1;
            }
            Console.WriteLine("[remote-route-echo] PASS: desktop and remote bytes are identical.");
            return 0;
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[remote-route-echo] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    private static async Task DispatchAsync(SessionManager manager, Session session, string verb, object payload)
    {
        var result = await SessionCommandExecutor.DispatchAsync(manager, "delivery-qa-rig", new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"), Verb = verb, SessionId = session.Id.ToString(), PayloadJson = JsonSerializer.Serialize(payload),
        });
        if (!result.Ok) throw new InvalidOperationException($"{verb} was refused: {result.Error}");
    }

    private static async Task<IReadOnlyList<EchoRead>> CaptureAsync(string log, Func<Task> send)
    {
        var before = EchoAgent.ReadLog(log).Count;
        await send();
        return await EchoLayer.QuietReadsAsync(log, before);
    }

    private static string ToHex(IEnumerable<EchoRead> reads) => Convert.ToHexString(reads.SelectMany(read => read.Bytes).ToArray());

    private sealed record RemoteRouteEchoProof(string DesktopKeys, string RemoteKeys, string DesktopPaste, string RemotePaste);
}
