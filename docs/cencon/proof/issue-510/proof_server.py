"""
Issue #510 UI proof server.

Serves the REAL Cockpit settings.html (from the worktree wwwroot, unmodified) and a small
/gateway/* REST surface that returns the exact JSON shape the updated
SettingsEndpoints.BrainBlockAsync now produces: a brain block carrying the machine's
registered "agents" (more than one) plus the saved "agentId". This lets a browser render the
Wingman tab so we can screenshot the "Agent" label and the multi-agent dropdown, and exercise
the Save path (PUT /gateway/brain/config with { agentId, model }).

The end-to-end persistence + round-trip behaviour is proven separately and independently by the
in-process GatewayHost tests in CcDirector.Gateway.Tests/BrainConfigEndpointTests.cs; this server
only proves the browser-facing rendering of the new shape.

ASCII output only. Loopback only.
"""
import json
import http.server
import socketserver
import os

WWWROOT = os.path.join(
    os.path.dirname(__file__), "..", "..", "..", "..",
    "src", "CcDirector.Cockpit", "wwwroot")
WWWROOT = os.path.abspath(WWWROOT)
PORT = 8531

# State the PUT mutates so a reload pre-selects the saved agent (the round-trip).
STATE = {
    "agentId": "11111111-1111-1111-1111-111111111111",  # Claude Code, the default selection
    "model": "opus",
    "wingman_enabled": False,
}

AGENTS = [
    {"id": "11111111-1111-1111-1111-111111111111", "displayName": "Claude Code", "type": "ClaudeCode"},
    {"id": "22222222-2222-2222-2222-222222222222", "displayName": "Pi",          "type": "Pi"},
    {"id": "33333333-3333-3333-3333-333333333333", "displayName": "Codex",       "type": "Codex"},
    {"id": "44444444-4444-4444-4444-444444444444", "displayName": "Gemini",      "type": "Gemini"},
    {"id": "55555555-5555-5555-5555-555555555555", "displayName": "OpenCode",    "type": "OpenCode"},
]


def settings_payload():
    saved = next((a for a in AGENTS if a["id"] == STATE["agentId"]), AGENTS[0])
    return {
        "version": "0.9.12",
        "state": "Running",
        "port": PORT,
        "uptimeSeconds": 42,
        "directors": 1,
        "mode": "dev",
        "addressingMode": "tailscale",
        "cockpit": {"port": PORT, "up": True, "url": None},
        "autostart": {"supported": True, "enabled": False},
        "brain": {
            "tool": saved["type"],
            "agents": AGENTS,
            "agentId": STATE["agentId"],
            "model": STATE["model"],
            "sessionId": "demo-session",
            "pid": 1234,
            "alive": True,
            "started": True,
            "status": "Alive",
            "detail": "alive - Idle, idle 3s, context 12,000 tokens",
        },
    }


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=WWWROOT, **kwargs)

    def log_message(self, fmt, *args):
        print("[proof-server] " + (fmt % args))

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/settings":
            with open(os.path.join(WWWROOT, "pages", "settings.html"), "rb") as f:
                body = f.read()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path == "/gateway/settings":
            self._json(settings_payload()); return
        if self.path == "/gateway/wingman":
            self._json({"enabled": STATE["wingman_enabled"]}); return
        return super().do_GET()

    def do_PUT(self):
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length else b"{}"
        body = json.loads(raw.decode("utf-8")) if raw else {}
        if self.path == "/gateway/brain/config":
            agent_id = (body.get("agentId") or "").strip()
            model = (body.get("model") or "").strip()
            match = next((a for a in AGENTS if a["id"] == agent_id), None)
            if not match:
                self._json({"error": "agentId must be a registered, enabled agent on this machine"}, 400); return
            if not model:
                self._json({"error": "model is required"}, 400); return
            STATE["agentId"] = agent_id
            STATE["model"] = model
            print("[proof-server] saved brain config: agentId=%s tool=%s model=%s"
                  % (agent_id, match["type"], model))
            self._json({"agentId": agent_id, "tool": match["type"], "model": model}); return
        if self.path == "/gateway/wingman":
            STATE["wingman_enabled"] = bool(body.get("enabled"))
            self._json({"enabled": STATE["wingman_enabled"]}); return
        self._json({"error": "not found"}, 404)


if __name__ == "__main__":
    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.TCPServer(("127.0.0.1", PORT), Handler) as httpd:
        print("[proof-server] serving on http://127.0.0.1:%d (settings: /settings)" % PORT)
        print("[proof-server] wwwroot=%s" % WWWROOT)
        httpd.serve_forever()
