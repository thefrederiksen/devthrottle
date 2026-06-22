// THROWAWAY SPIKE PROOF-OF-CONCEPT - issue #627.
//
// Purpose: launch `copilot --acp` (GitHub Copilot CLI's Agent Client Protocol
// server) and drive a full prompt turn over JSON-RPC 2.0 on stdio, recording the
// REAL wire messages observed. This is evaluation scaffolding only - it is NOT
// production code, is NOT in cc-director.sln, and must never be referenced by a
// production project. Every byte it logs comes from the live copilot binary.
//
// What it exercises (the issue's Scope/IN, end to end):
//   1. initialize            (client -> agent)  lifecycle + capability exchange
//   2. session/new           (client -> agent)  session creation
//   3. session/prompt        (client -> agent)  one trivial prompt ("what is 2+2")
//   4. session/update        (agent -> client)  streamed assistant/agent updates
//   5. session/request_permission (agent -> client) tool-call approval round-trip
//   6. fs/* and terminal/*   (agent -> client)  client-burden methods, answered here
//   7. session/cancel        (client -> agent)  cancellation of a second turn
//
// Every line read from / written to the agent is echoed to stdout with a [RECV]
// / [SEND] tag AND appended verbatim to the transcript file passed as arg 1, so
// the RECOMMENDATION.md transcript is a copy of real traffic, not spec prose.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcpPoc;

internal static class Program
{
    private static StreamWriter _log = StreamWriter.Null;
    private static readonly object _logLock = new();
    private static Process _agent = null!;
    private static int _nextId = 1;

    // Pending client-originated requests keyed by JSON-RPC id, completed when the
    // agent's matching response arrives.
    private static readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    // A throwaway sandbox working directory the agent is pointed at. NEVER the repo.
    private static string _sandbox = "";

    // Set true once we have auto-approved one permission request (so we only do it once
    // for the proof, then can observe normal flow).
    private static bool _approvedOne;

    private static async Task<int> Main(string[] args)
    {
        var transcriptPath = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), $"acp-transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        _sandbox = Path.Combine(Path.GetTempPath(), "acp-poc-sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandbox);

        _log = new StreamWriter(transcriptPath, append: false) { AutoFlush = true };
        Log($"# ACP PoC transcript - {DateTime.Now:O}");
        Log($"# copilot --acp driven over JSON-RPC/stdio. Sandbox cwd: {_sandbox}");
        Log("# Lines tagged [SEND] are client->agent; [RECV] are agent->client. Verbatim JSON.");
        Log("");

        // The token must be in the environment already (COPILOT_GITHUB_TOKEN / GH_TOKEN /
        // GITHUB_TOKEN). We do NOT read or print it - we only require it to be present.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GH_TOKEN"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            Console.Error.WriteLine("ERROR: no COPILOT_GITHUB_TOKEN / GH_TOKEN / GITHUB_TOKEN in environment. Cannot auth copilot --acp.");
            return 2;
        }

        // Resolve copilot's node entry point. On Windows `copilot` is a .cmd shim;
        // launching node directly against npm-loader.js avoids the shim entirely.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var loader = Path.Combine(appData, "npm", "node_modules", "@github", "copilot", "npm-loader.js");
        if (!File.Exists(loader))
        {
            Console.Error.WriteLine($"ERROR: copilot npm-loader.js not found at {loader}");
            return 2;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            WorkingDirectory = _sandbox,
        };
        psi.ArgumentList.Add(loader);
        psi.ArgumentList.Add("--acp");

        _agent = new Process { StartInfo = psi };
        _agent.Start();

        // Drain stderr (copilot prints diagnostics there); record it but do not parse it.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _agent.StandardError.ReadLineAsync()) != null)
                Log($"[STDERR] {line}");
        });

        // The reader loop: every line from the agent is a JSON-RPC message.
        var readerLoop = Task.Run(ReadLoopAsync);

        try
        {
            // 1) initialize ---------------------------------------------------
            var initResult = await CallAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject
                    {
                        ["readTextFile"] = true,
                        ["writeTextFile"] = true,
                    },
                    ["terminal"] = true,
                },
            });
            Console.Error.WriteLine("initialize OK");

            // 2) session/new --------------------------------------------------
            var newResult = await CallAsync("session/new", new JsonObject
            {
                ["cwd"] = _sandbox,
                ["mcpServers"] = new JsonArray(),
            });
            var sessionId = newResult?["sessionId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("session/new returned no sessionId");
            Console.Error.WriteLine($"session/new OK: {sessionId}");

            // 3) session/prompt - one trivial prompt --------------------------
            // Trivial on purpose so the agent does not make real file edits. We still
            // get streamed session/update notifications back.
            Log("");
            Log("# ---- TURN 1: trivial prompt 'what is 2+2' ----");
            var promptResult = await CallAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "What is 2+2? Reply with just the number.",
                    },
                },
            });
            Console.Error.WriteLine($"session/prompt (turn 1) stopReason: {promptResult?["stopReason"]}");

            // Scenario selector (env ACP_POC_SCENARIO): "permission" runs a tool-using
            // turn to completion to observe a real session/request_permission round-trip
            // and tool_call updates; "cancel" (default) provokes a turn then cancels it.
            var scenario = Environment.GetEnvironmentVariable("ACP_POC_SCENARIO") ?? "cancel";

            if (scenario == "permission")
            {
                // 4a) A turn that forces a tool call so we observe the agent asking the
                //     client for permission (session/request_permission), the client
                //     answering it programmatically, and the resulting fs/terminal calls.
                Log("");
                Log("# ---- TURN 2 (permission scenario): force a tool call, run to completion ----");
                var turn2 = await CallAsync("session/prompt", new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Create a file named notes.txt in the current directory containing exactly "
                                     + "the single line: hello from acp. Use your file-writing tool to do it.",
                        },
                    },
                });
                Console.Error.WriteLine($"session/prompt (permission turn) stopReason: {turn2?["stopReason"]}");
            }
            else
            {
                // 4b) Cancellation scenario: provoke a turn, then cancel it mid-flight.
                Log("");
                Log("# ---- TURN 2 (cancel scenario): start a longer turn then cancel it ----");
                var turn2 = CallAsync("session/prompt", new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Count slowly from 1 to 50, writing a short sentence about each number.",
                        },
                    },
                });

                await Task.Delay(5000);
                Log("");
                Log("# ---- sending session/cancel for the in-flight turn ----");
                await NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId });

                var settled = await Task.WhenAny(turn2, Task.Delay(15000));
                if (settled == turn2)
                {
                    var r2 = await turn2;
                    Console.Error.WriteLine($"session/prompt (cancel turn) stopReason after cancel: {r2?["stopReason"]}");
                }
                else
                {
                    Console.Error.WriteLine("session/prompt (cancel turn) did not resolve within 15s after cancel");
                }
            }

            Log("");
            Log("# ---- PoC complete. Shutting down agent. ----");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PoC ERROR: {ex}");
            Log($"# PoC ERROR: {ex}");
        }
        finally
        {
            try { if (!_agent.HasExited) _agent.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await Task.WhenAny(readerLoop, Task.Delay(2000));
            _log.Flush();
            _log.Dispose();
            Console.Error.WriteLine($"Transcript written to: {transcriptPath}");
        }

        return 0;
    }

    // Reads one JSON-RPC message per line and dispatches it.
    private static async Task ReadLoopAsync()
    {
        string? line;
        while ((line = await _agent.StandardOutput.ReadLineAsync()) != null)
        {
            if (line.Length == 0) continue;
            Log($"[RECV] {line}");

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { Log($"# (unparseable line above)"); continue; }
            if (msg is null) continue;

            var hasId = msg["id"] is not null;
            var hasMethod = msg["method"] is not null;

            if (hasMethod && hasId)
            {
                // Agent -> client REQUEST (expects a response).
                await HandleAgentRequestAsync(msg);
            }
            else if (hasMethod)
            {
                // Agent -> client NOTIFICATION (session/update etc.) - no response.
                // We just record it (already logged above).
            }
            else if (hasId)
            {
                // Response to one of OUR requests.
                var id = msg["id"]!.GetValue<int>();
                if (_pending.TryGetValue(id, out var tcs))
                {
                    _pending.Remove(id);
                    if (msg["error"] is JsonNode err)
                        tcs.TrySetException(new InvalidOperationException($"JSON-RPC error: {err.ToJsonString()}"));
                    else
                        tcs.TrySetResult(msg["result"]);
                }
            }
        }
    }

    // Implements the client side of the protocol: answer the agent's requests.
    private static async Task HandleAgentRequestAsync(JsonNode msg)
    {
        var id = msg["id"]!.DeepClone();
        var method = msg["method"]!.GetValue<string>();
        var p = msg["params"];

        JsonNode? result = null;

        switch (method)
        {
            case "session/request_permission":
            {
                // Auto-approve the FIRST permission request (proof of a programmatic
                // allow), then keep approving so the turn can proceed to the cancel.
                // We pick the option whose kind is allow/allow_once if present,
                // else the first option.
                var options = p?["options"]?.AsArray();
                string? optionId = null;
                if (options is not null)
                {
                    foreach (var opt in options)
                    {
                        var kind = opt?["kind"]?.GetValue<string>();
                        if (kind is "allow_once" or "allow_always" or "allow")
                        {
                            optionId = opt?["optionId"]?.GetValue<string>();
                            break;
                        }
                    }
                    optionId ??= options.Count > 0 ? options[0]?["optionId"]?.GetValue<string>() : null;
                }
                if (!_approvedOne)
                {
                    _approvedOne = true;
                    Log($"# (client auto-approving first permission request, optionId={optionId})");
                }
                result = new JsonObject
                {
                    ["outcome"] = new JsonObject
                    {
                        ["outcome"] = "selected",
                        ["optionId"] = optionId,
                    },
                };
                break;
            }

            case "fs/read_text_file":
            {
                var path = p?["path"]?.GetValue<string>() ?? "";
                string content = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
                result = new JsonObject { ["content"] = content };
                break;
            }

            case "fs/write_text_file":
            {
                var path = p?["path"]?.GetValue<string>() ?? "";
                var content = p?["content"]?.GetValue<string>() ?? "";
                await File.WriteAllTextAsync(path, content);
                result = new JsonObject();
                break;
            }

            // terminal/* methods are part of the client burden. For the spike we
            // acknowledge them minimally so the agent does not stall; we are not
            // building a full terminal host here.
            case "terminal/create":
                result = new JsonObject { ["terminalId"] = "poc-term-1" };
                break;
            case "terminal/output":
                result = new JsonObject { ["output"] = "", ["truncated"] = false };
                break;
            case "terminal/wait_for_exit":
                result = new JsonObject { ["exitCode"] = 0 };
                break;
            case "terminal/kill":
            case "terminal/release":
                result = new JsonObject();
                break;

            default:
                // Unknown agent->client request: respond with a method-not-found error
                // so we can SEE it in the transcript rather than silently hang.
                SendRaw(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"PoC client does not implement {method}",
                    },
                });
                return;
        }

        SendRaw(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        });
    }

    // Sends a client-originated request and awaits the agent's response.
    private static Task<JsonNode?> CallAsync(string method, JsonNode @params)
    {
        var id = _nextId++;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        SendRaw(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params,
        });
        return tcs.Task;
    }

    // Sends a client-originated notification (no response expected).
    private static Task NotifyAsync(string method, JsonNode @params)
    {
        SendRaw(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        });
        return Task.CompletedTask;
    }

    private static void SendRaw(JsonNode msg)
    {
        var json = msg.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        Log($"[SEND] {json}");
        lock (_logLock)
        {
            _agent.StandardInput.Write(json);
            _agent.StandardInput.Write('\n');
            _agent.StandardInput.Flush();
        }
    }

    private static void Log(string s)
    {
        lock (_logLock)
        {
            Console.WriteLine(s);
            _log.WriteLine(s);
        }
    }
}
