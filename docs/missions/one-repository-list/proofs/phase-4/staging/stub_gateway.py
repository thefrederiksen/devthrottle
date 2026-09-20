"""A stand-in Gateway for the phase-4 screenshots.

It serves ONLY what the Cockpit's New Session dialog reads, with responses read from a scenario
file on every request, so a scenario can be swapped between screenshots without restarting
anything. It is a QA instrument and no product code knows it exists: the Vite dev server fronts
it exactly as it would front a real Gateway (COCKPIT_PROXY_TARGET), so the screen makes its real
fetch calls, through the real client-core reader, and sees real HTTP status codes.
"""
import json, os, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

SCENARIO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "scenario.json")


def scenario():
    with open(SCENARIO, encoding="utf-8") as handle:
        return json.load(handle)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        sys.stderr.write("%s - %s\n" % (self.command, self.path))

    def _send(self, status, body):
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        s = scenario()
        path = self.path.split("?")[0]
        if path == "/directors":
            return self._send(200, s["directors"])
        if path.endswith("/known-repositories"):
            answer = s["knownRepositories"]
            if isinstance(answer, dict) and "status" in answer:
                return self._send(answer["status"], {"error": answer.get("error", "")})
            return self._send(200, answer)
        if path.endswith("/agents"):
            return self._send(200, s["agents"])
        if path.startswith("/gateway/"):
            return self._send(200, s.get("gateway", {}).get(path, {}))
        return self._send(200, [])

    def do_POST(self):
        s = scenario()
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b"{}"
        path = self.path.split("?")[0]
        if path.endswith("/repos"):
            answer = s.get("repoAdd", {"status": 201, "added": True})
            if answer.get("status", 201) >= 400:
                return self._send(answer["status"], {"error": answer.get("error", "")})
            asked = json.loads(body.decode("utf-8") or "{}").get("path", "")
            return self._send(
                answer.get("status", 201),
                {"added": answer.get("added", True),
                 "repo": {"name": answer.get("name") or asked.rstrip("/").split("/")[-1],
                          "path": asked, "lastUsed": None}},
            )
        return self._send(200, {})

    def do_PUT(self):
        return self._send(200, {})


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
