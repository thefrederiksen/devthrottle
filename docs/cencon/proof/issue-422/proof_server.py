"""
Proof harness for issue #422 (voice turn count on rows + header).

Serves the MODIFIED pages/voice/ static files exactly as the Cockpit does (injecting the
real Gateway token into index.html at the __GATEWAY_TOKEN__ placeholder) and proxies the
page's same-origin /sessions/** API calls to the live Gateway on 127.0.0.1:7878. This
reproduces the real front-door behaviour the page expects, so what renders here is what a
phone hitting the Cockpit /voice route would render. ASCII output only.
"""
import http.server
import socketserver
import urllib.request
import os

PORT = 8422
GATEWAY = "http://127.0.0.1:7878"
VOICE_DIR = os.path.join(
    os.path.dirname(__file__), "..", "..", "..", "..",
    "src", "CcDirector.Cockpit", "wwwroot", "pages", "voice")
VOICE_DIR = os.path.abspath(VOICE_DIR)
TOKEN_FILE = os.path.expandvars(
    r"%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt")

with open(TOKEN_FILE, "r", encoding="utf-8") as fh:
    TOKEN = fh.read().strip()


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _serve_voice(self):
        # /voice -> inject token into index.html, like Cockpit Program.cs does.
        path = os.path.join(VOICE_DIR, "index.html")
        with open(path, "r", encoding="utf-8") as fh:
            html = fh.read().replace("__GATEWAY_TOKEN__", TOKEN)
        body = html.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _serve_static(self, rel):
        path = os.path.join(VOICE_DIR, rel)
        if not os.path.isfile(path):
            self.send_error(404)
            return
        ctype = "application/javascript" if rel.endswith(".js") else \
                "text/css" if rel.endswith(".css") else "application/octet-stream"
        with open(path, "rb") as fh:
            body = fh.read()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _proxy(self):
        url = GATEWAY + self.path
        req = urllib.request.Request(url, method="GET")
        auth = self.headers.get("Authorization")
        if auth:
            req.add_header("Authorization", auth)
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                body = resp.read()
                self.send_response(resp.status)
                self.send_header("Content-Type", resp.headers.get("Content-Type", "application/json"))
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
        except urllib.error.HTTPError as e:
            body = e.read()
            self.send_response(e.code)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def do_GET(self):
        if self.path == "/voice" or self.path == "/":
            self._serve_voice()
        elif self.path.startswith("/pages/voice/"):
            self._serve_static(self.path[len("/pages/voice/"):])
        elif self.path.startswith("/sessions") or self.path.startswith("/directors"):
            self._proxy()
        else:
            self.send_error(404)


if __name__ == "__main__":
    print("VOICE_DIR=" + VOICE_DIR)
    print("TOKEN_LEN=" + str(len(TOKEN)))
    with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), Handler) as httpd:
        print("Serving on http://127.0.0.1:" + str(PORT) + "/voice")
        httpd.serve_forever()
