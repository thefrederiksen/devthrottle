#!/usr/bin/env python3
"""
TEST HARNESS (issue #425 proof only - NOT shipped product code).

A loopback front door that serves the issue-425 Voice page static files from this worktree
and forwards every other path to the live Gateway on 127.0.0.1:7878. This lets the real,
updated voice.js / db.js / index.html run same-origin against the real voice-turn upload
backend, exactly as the Gateway front door serves them in production (the Gateway proxies
/voice + /pages/voice/* to the Cockpit and serves /sessions/* itself; here we collapse that
into one process so we can run OUR files without disturbing the live Cockpit on 7470).

Run:  python scripts/voice-proof-proxy-425.py <listen-port>
"""
import http.server
import os
import sys
import urllib.request
import urllib.error

GATEWAY = "http://127.0.0.1:7878"
HERE = os.path.dirname(os.path.abspath(__file__))
VOICE_DIR = os.path.join(HERE, "..", "src", "CcDirector.Cockpit", "wwwroot", "pages", "voice")
VOICE_DIR = os.path.normpath(VOICE_DIR)

def read_token():
    root = os.environ.get("CC_DIRECTOR_ROOT") or os.path.join(
        os.environ["LOCALAPPDATA"], "cc-director")
    path = os.path.join(root, "config", "director", "gateway-token.txt")
    with open(path, "r", encoding="utf-8") as f:
        return f.read().strip()

TOKEN = read_token()

STATIC = {
    "/db.js": ("db.js", "application/javascript"),
    "/voice.js": ("voice.js", "application/javascript"),
    "/voice.css": ("voice.css", "text/css"),
    "/pages/voice/db.js": ("db.js", "application/javascript"),
    "/pages/voice/voice.js": ("voice.js", "application/javascript"),
    "/pages/voice/voice.css": ("voice.css", "text/css"),
}

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):  # quieter
        pass

    def _serve_static(self, fname, ctype):
        with open(os.path.join(VOICE_DIR, fname), "rb") as f:
            body = f.read()
        self.send_response(200)
        self.send_header("Content-Type", ctype + "; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _serve_voice_page(self):
        with open(os.path.join(VOICE_DIR, "index.html"), "r", encoding="utf-8") as f:
            html = f.read().replace("__GATEWAY_TOKEN__", TOKEN)
        body = html.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _forward(self, method):
        url = GATEWAY + self.path
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else None
        req = urllib.request.Request(url, data=body, method=method)
        for h in ("Content-Type", "Idempotency-Key", "Accept"):
            if h in self.headers:
                req.add_header(h, self.headers[h])
        req.add_header("Authorization", "Bearer " + TOKEN)
        try:
            with urllib.request.urlopen(req, timeout=300) as resp:
                data = resp.read()
                self.send_response(resp.status)
                ct = resp.headers.get("Content-Type", "application/json")
                self.send_header("Content-Type", ct)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        except urllib.error.HTTPError as e:
            data = e.read()
            self.send_response(e.code)
            self.send_header("Content-Type", e.headers.get("Content-Type", "application/json"))
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    def do_GET(self):
        p = self.path.split("?")[0]
        if p == "/voice" or p == "/" or p == "/pages/voice/index.html":
            self._serve_voice_page(); return
        if p in STATIC:
            self._serve_static(*STATIC[p]); return
        self._forward("GET")

    def do_POST(self):
        self._forward("POST")

    def do_PUT(self):
        self._forward("PUT")

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 7935
    print("voice-proof-proxy on http://127.0.0.1:%d  (voice files from %s, API -> %s)"
          % (port, VOICE_DIR, GATEWAY))
    http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
