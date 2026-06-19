"""Proof server for issue #533 (lives under docs/cencon/proof/, never shipped).

Serves the REAL, unmodified /m/ assets from the worktree (m.js, m.css, index.html) so the page
runs exactly as it ships. The only change: index.html is rewritten on the fly to inject
harness-stub.js right before the real m.js, which stubs the gateway network calls and supplies a
real playable audio file. The page's own logic is untouched.
"""
import http.server
import socketserver
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
M_DIR = os.path.normpath(os.path.join(ROOT, "..", "..", "..", "..", "src", "CcDirector.Cockpit", "wwwroot", "m"))
PORT = 8533


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path in ("/", "/m/", "/m/index.html"):
            return self._serve_index()
        if path == "/harness-stub.js":
            return self._serve_file(os.path.join(ROOT, "harness-stub.js"), "application/javascript")
        if path.startswith("/m/"):
            rel = path[len("/m/"):]
            full = os.path.normpath(os.path.join(M_DIR, rel))
            if not full.startswith(M_DIR):
                self.send_error(403)
                return
            ctype = "application/javascript" if full.endswith(".js") else \
                    "text/css" if full.endswith(".css") else "text/html"
            return self._serve_file(full, ctype)
        self.send_error(404)

    def _serve_index(self):
        with open(os.path.join(M_DIR, "index.html"), "r", encoding="utf-8") as f:
            html = f.read()
        # Inject the stub immediately before the real m.js script tag.
        marker = '<script src="/m/m.js'
        idx = html.find(marker)
        inject = '<script src="/harness-stub.js"></script>\n  '
        html = html[:idx] + inject + html[idx:]
        body = html.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _serve_file(self, full, ctype):
        if not os.path.isfile(full):
            self.send_error(404)
            return
        with open(full, "rb") as f:
            data = f.read()
        self.send_response(200)
        self.send_header("Content-Type", ctype + ("; charset=utf-8" if ctype != "text/css" else ""))
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    os.chdir(ROOT)
    print("M_DIR=" + M_DIR)
    with socketserver.TCPServer(("127.0.0.1", PORT), Handler) as httpd:
        print("Serving proof on http://127.0.0.1:%d/m/" % PORT)
        httpd.serve_forever()
