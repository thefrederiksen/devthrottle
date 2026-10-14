"""A scripted stand-in for the model endpoint, for phase 0 proofs (b) and (c).

Codex enforces its rules and hooks in the CLI, before a command runs, not in the model. So the
real Codex CLI is pointed at this local server (a custom model provider passed with -c) and the
server plays a fixed script: one shell call per turn, then a final message. Every request Codex
sends is logged whole, which is the evidence: the function_call_output Codex returns for each call
carries the refusal text, quoted, and the request's instructions and input show what the session
was given (its SessionStart hook context, its skills).

Usage: python scripted_model.py --port 0 --commands-json <file> --log <file> --port-file <file>
       python scripted_model.py summarize <log> [--needle TEXT ...]
The commands file is a JSON list of shell command strings. The chosen port is written to
--port-file once the server listens. Stops after --max-requests requests or on Ctrl+C.
"""
import argparse
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATE = {"requests": 0}


def sse(event_type, payload):
    payload = dict(payload)
    payload["type"] = event_type
    return f"event: {event_type}\ndata: {json.dumps(payload)}\n\n".encode("utf-8")


def pick_shell_tool(tools):
    names = []
    for t in tools or []:
        name = t.get("name") or (t.get("function") or {}).get("name")
        if name:
            names.append(name)
    for preferred in ("shell_command", "exec_command", "shell"):
        if preferred in names:
            return preferred, names
    raise RuntimeError(f"no shell tool in the request; tools offered: {names}")


def shell_arguments(tool, command):
    if tool == "shell":
        return {"command": ["powershell.exe", "-Command", command]}
    if tool == "exec_command":
        return {"cmd": command}
    return {"command": command}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _log(self, record):
        with open(self.server.log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(record) + "\n")

    def do_GET(self):
        self._log({"method": "GET", "path": self.path})
        body = json.dumps({"object": "list", "data": [], "models": []}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length)
        try:
            req = json.loads(raw.decode("utf-8"))
        except ValueError:
            req = {"unparsed": raw.decode("utf-8", "replace")}
        STATE["requests"] += 1
        n = STATE["requests"]
        self._log({"method": "POST", "path": self.path, "n": n, "body": req})

        if not self.path.rstrip("/").endswith("/responses"):
            self.send_response(404)
            self.end_headers()
            return

        # How many of OUR calls have already had an answer: the index of the next scripted step.
        outputs = [i for i in req.get("input", []) if isinstance(i, dict) and i.get("type") == "function_call_output"]
        step = len(outputs)
        commands = self.server.commands
        rid = f"resp_{n}"
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        self.wfile.write(sse("response.created", {"response": {"id": rid}}))
        if step < len(commands):
            tool, _ = pick_shell_tool(req.get("tools"))
            item = {
                "type": "function_call",
                "id": f"fc_{n}",
                "call_id": f"call_{step + 1}",
                "name": tool,
                "arguments": json.dumps(shell_arguments(tool, commands[step])),
            }
        else:
            item = {
                "type": "message",
                "id": f"msg_{n}",
                "role": "assistant",
                "content": [{"type": "output_text", "text": "SCRIPT-DONE"}],
            }
        self.wfile.write(sse("response.output_item.done", {"item": item}))
        usage = {"input_tokens": 0, "input_tokens_details": None, "output_tokens": 0,
                 "output_tokens_details": None, "total_tokens": 0}
        self.wfile.write(sse("response.completed", {"response": {"id": rid, "usage": usage}}))
        self.wfile.flush()
        if n >= self.server.max_requests:
            self.server.stop_requested = True


def summarize(argv):
    """Print what the proof needs from a request log: each command result Codex sent back, and
    where each needle appears in what the session was given."""
    ap = argparse.ArgumentParser(prog="scripted_model.py summarize")
    ap.add_argument("log")
    ap.add_argument("--needle", action="append", default=[])
    a = ap.parse_args(argv)
    records = [json.loads(line) for line in open(a.log, encoding="utf-8") if line.strip()]
    posts = [r for r in records if r["method"] == "POST"]
    print(f"REQUESTS: {len(records)} ({len(posts)} model turns)")
    seen = set()
    for r in posts:
        for item in r["body"].get("input", []):
            if isinstance(item, dict) and item.get("type") == "function_call_output" and item.get("call_id") not in seen:
                seen.add(item.get("call_id"))
                out = item.get("output")
                if not isinstance(out, str):
                    out = json.dumps(out)
                print(f"COMMAND RESULT {item.get('call_id')}: {' '.join(out.split())}")
    first = json.dumps(posts[0]["body"]) if posts else ""
    for needle in a.needle:
        i = first.find(needle)
        if i < 0:
            print(f"NEEDLE '{needle}': ABSENT from what the session was given")
        else:
            ctx = first[max(0, i - 160): i + 200]
            print(f"NEEDLE '{needle}': PRESENT ... {ctx} ...")
    return 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "summarize":
        return summarize(sys.argv[2:])
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--commands-json", required=True)
    ap.add_argument("--log", required=True)
    ap.add_argument("--port-file", required=True)
    ap.add_argument("--max-requests", type=int, default=40)
    a = ap.parse_args()
    with open(a.commands_json, encoding="utf-8-sig") as f:
        commands = json.load(f)
    server = ThreadingHTTPServer(("127.0.0.1", a.port), Handler)
    server.commands = commands
    server.log_path = a.log
    server.max_requests = a.max_requests
    server.stop_requested = False
    with open(a.port_file, "w", encoding="ascii") as f:
        f.write(str(server.server_address[1]))
    print(f"scripted model listening on 127.0.0.1:{server.server_address[1]}", flush=True)
    server.timeout = 1
    while not server.stop_requested:
        server.handle_request()
    return 0


if __name__ == "__main__":
    sys.exit(main())
